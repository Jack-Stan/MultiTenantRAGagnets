using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Security;
using MultiTenantRAGagnets.Core.Tests.Support;

namespace MultiTenantRAGagnets.Api.Tests;

/// <summary>
/// Wiring tests with NO database: the store is the in-memory fake and the user directory is a stub.
/// Everything that needs real Postgres (Npgsql store, RLS) is covered by db/tests and the leakage suite.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // Throwaway values that exist only for these tests; they secure nothing.
    public const string JwtKey = "api-test-jwt-key-0123456789abcdefghij";
    public const string HashKey = "api-test-hash-key-0123456789abcdefghi";

    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public InMemoryChunkStore Store { get; } = new();
    public string? SigningKey { get; init; } = JwtKey;
    public string Environment { get; init; } = "Development";
    public bool DevIssuer { get; init; } = true;
    public int? DevTokenPermitLimit { get; init; }
    public bool OmitDevIssuerSetting { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        if (SigningKey is not null) builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Audit:HashKey", HashKey);
        builder.UseSetting("ConnectionStrings:App", "Host=localhost;Database=x;Username=app_user"); // never connected to
        builder.UseSetting("ConnectionStrings:Identity", "Host=localhost;Database=x;Username=identity_reader"); // never connected to
        if (!OmitDevIssuerSetting) builder.UseSetting("Auth:DevTokenIssuer:Enabled", DevIssuer ? "true" : "false");
        if (DevTokenPermitLimit is { } limit) builder.UseSetting("Auth:DevTokenIssuer:PermitLimit", limit.ToString());

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChunkStore>();
            services.AddSingleton<IChunkStore>(Store);
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(new StubDirectory());
        });
    }

    public HttpClient ClientFor(Guid tenant, string role)
    {
        var jwt = new JwtTokenService(Options.Create(new JwtOptions { SigningKey = JwtKey }));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt.Issue(new AuthenticatedUser(Guid.NewGuid(), tenant, role)));
        return client;
    }

    private sealed class StubDirectory : IUserDirectory
    {
        public Task<AuthenticatedUser?> FindAsync(string tenantSlug, string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedUser?>(tenantSlug == "acme" && email == "hr@acme.test"
                ? new AuthenticatedUser(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), TenantA, "hr-admin")
                : null);
    }
}

public class ApiTests
{
    private sealed record Ingested(Guid DocumentId, int ChunkCount, List<Guid> ChunkIds);
    private sealed record Answer(string Answer_, List<Guid> CitedChunkIds, string Model, int LatencyMs);

    [Fact]
    public void Host_refuses_to_start_without_a_jwt_signing_key()
    {
        using var factory = new ApiFactory { SigningKey = null };

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Jwt:SigningKey", ex!.ToString());
    }

    [Fact]
    public async Task Health_is_open()
    {
        using var factory = new ApiFactory();
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/query")]
    [InlineData("POST", "/ingest")]
    [InlineData("GET", "/audit")]
    public async Task Endpoints_without_a_token_are_401(string method, string path)
    {
        using var factory = new ApiFactory();
        var res = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { question = "x", title = "t", text = "t", requiredLevel = 1 }) : null,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_tampered_token_is_401()
    {
        using var factory = new ApiFactory();
        var client = factory.ClientFor(ApiFactory.TenantA, "employee");
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var parts = token.Split('.');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}");

        var res = await client.PostAsJsonAsync("/query", new { question = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Query_returns_only_the_callers_permitted_chunks_and_writes_one_audit_row()
    {
        using var factory = new ApiFactory();
        var a1 = Guid.NewGuid(); var a3 = Guid.NewGuid(); var b1 = Guid.NewGuid();
        factory.Store.AddChunk(ApiFactory.TenantA, 1, "holiday policy is 25 days", a1);
        factory.Store.AddChunk(ApiFactory.TenantA, 3, "salary secret 90k", a3);
        factory.Store.AddChunk(ApiFactory.TenantB, 1, "holiday policy is 25 days", b1);
        var client = factory.ClientFor(ApiFactory.TenantA, "employee");

        var res = await client.PostAsJsonAsync("/query", new { question = "holiday policy", k = 10 });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains(a1.ToString(), body);
        Assert.DoesNotContain(a3.ToString(), body);
        Assert.DoesNotContain(b1.ToString(), body);
        Assert.DoesNotContain("salary", body);
        var row = Assert.Single(factory.Store.AuditRows);
        Assert.Equal(ApiFactory.TenantA, row.TenantId);
        Assert.Equal("employee", row.Role);
        Assert.Equal([a1], row.SentChunkIds);
    }

    [Fact]
    public async Task Request_body_cannot_choose_the_tenant_or_role()
    {
        using var factory = new ApiFactory();
        var b1 = Guid.NewGuid();
        factory.Store.AddChunk(ApiFactory.TenantB, 1, "tenant B secret", b1);
        var client = factory.ClientFor(ApiFactory.TenantA, "employee");

        var res = await client.PostAsJsonAsync("/query", new { question = "tenant B secret", tenant = ApiFactory.TenantB, role = "hr-admin", k = 10 });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(b1.ToString(), await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bad_k_is_a_400_not_a_500()
    {
        using var factory = new ApiFactory();
        var res = await factory.ClientFor(ApiFactory.TenantA, "employee").PostAsJsonAsync("/query", new { question = "x", k = 9999 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Empty_question_is_a_400()
    {
        using var factory = new ApiFactory();
        var res = await factory.ClientFor(ApiFactory.TenantA, "employee").PostAsJsonAsync("/query", new { question = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("manager")]
    public async Task Ingest_and_audit_are_forbidden_below_hr_admin(string role)
    {
        using var factory = new ApiFactory();
        var client = factory.ClientFor(ApiFactory.TenantA, role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/ingest", new { title = "t", text = "text", requiredLevel = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/audit")).StatusCode);
    }

    [Fact]
    public async Task Hr_admin_can_ingest_then_query_then_read_the_audit_log()
    {
        using var factory = new ApiFactory();
        var admin = factory.ClientFor(ApiFactory.TenantA, "hr-admin");

        var ingest = await admin.PostAsJsonAsync("/ingest", new { title = "Pay", text = "Engineers are paid 90k.", requiredLevel = 3 });
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        var query = await admin.PostAsJsonAsync("/query", new { question = "Engineers are paid 90k." });
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);

        var audit = await admin.GetAsync("/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var text = await audit.Content.ReadAsStringAsync();
        Assert.Contains("queryHash", text);
        Assert.DoesNotContain("Engineers are paid", text); // no raw query text in the audit output
    }

    [Fact]
    public async Task Ingest_validation_failures_are_400()
    {
        using var factory = new ApiFactory();
        var admin = factory.ClientFor(ApiFactory.TenantA, "hr-admin");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/ingest", new { title = "t", text = "x", requiredLevel = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/ingest", new { title = "", text = "x", requiredLevel = 1 })).StatusCode);
    }

    [Fact]
    public async Task Audit_only_shows_the_callers_tenant()
    {
        using var factory = new ApiFactory();
        factory.Store.AddChunk(ApiFactory.TenantA, 1, "x");
        await factory.ClientFor(ApiFactory.TenantA, "employee").PostAsJsonAsync("/query", new { question = "x" });
        await factory.ClientFor(ApiFactory.TenantB, "employee").PostAsJsonAsync("/query", new { question = "y" });

        var text = await (await factory.ClientFor(ApiFactory.TenantB, "hr-admin").GetAsync("/audit")).Content.ReadAsStringAsync();

        Assert.Contains(ApiFactory.TenantB.ToString(), text);
        Assert.DoesNotContain(ApiFactory.TenantA.ToString(), text);
    }

    [Fact]
    public async Task Dev_token_endpoint_issues_a_token_that_works_and_gives_the_same_401_for_unknown_users()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "hr@acme.test" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var token = (await ok.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!["token"].GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/audit")).StatusCode);

        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/auth/dev-token", new { tenant = "nope", email = "x@y.z" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "nobody@acme.test" })).StatusCode);
    }

    [Fact]
    public async Task Dev_token_unknown_tenant_and_unknown_user_are_indistinguishable()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var unknownTenant = await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "nope", email = "hr@acme.test" });
        var unknownUser = await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "nobody@acme.test" });
        var bothUnknown = await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "nope", email = "nobody@acme.test" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknownTenant.StatusCode);
        var expectedBody = await unknownTenant.Content.ReadAsStringAsync();
        foreach (var res in new[] { unknownUser, bothUnknown })
        {
            Assert.Equal(unknownTenant.StatusCode, res.StatusCode);
            Assert.Equal(expectedBody, await res.Content.ReadAsStringAsync());
            Assert.Equal(unknownTenant.Content.Headers.ContentType, res.Content.Headers.ContentType);
            Assert.Equal(
                unknownTenant.Headers.Select(h => h.Key).OrderBy(k => k),
                res.Headers.Select(h => h.Key).OrderBy(k => k));
        }
    }

    [Fact]
    public async Task Dev_token_is_rate_limited_per_client_with_429_past_the_limit()
    {
        using var factory = new ApiFactory { DevTokenPermitLimit = 3 };
        var client = factory.CreateClient();
        var body = new { tenant = "nope", email = "x@y.z" };

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/dev-token", body)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/auth/dev-token", body)).StatusCode);
        // Even a valid pair is throttled once the window is spent: the limit is on the caller, not the outcome.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "hr@acme.test" })).StatusCode);
    }

    [Fact]
    public async Task Dev_token_rate_limit_does_not_throttle_other_endpoints()
    {
        using var factory = new ApiFactory { DevTokenPermitLimit = 1 };
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "nope", email = "x@y.z" });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/auth/dev-token", new { tenant = "nope", email = "x@y.z" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Dev_token_endpoint_does_not_exist_when_disabled()
    {
        using var factory = new ApiFactory { DevIssuer = false, Environment = "Production" };
        var res = await factory.CreateClient().PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "hr@acme.test" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Dev_token_endpoint_does_not_exist_when_the_setting_is_absent()
    {
        // No Auth:DevTokenIssuer setting at all (Production reads only appsettings.json): the default is off.
        using var factory = new ApiFactory { Environment = "Production", OmitDevIssuerSetting = true };
        var res = await factory.CreateClient().PostAsJsonAsync("/auth/dev-token", new { tenant = "acme", email = "hr@acme.test" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public void Host_refuses_to_start_with_the_dev_token_issuer_outside_development()
    {
        using var factory = new ApiFactory { Environment = "Production", DevIssuer = true };

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("not Development", ex!.ToString());
    }

    [Fact]
    public async Task Swagger_ui_and_document_are_served_in_development_but_not_production()
    {
        using var dev = new ApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await dev.CreateClient().GetAsync("/swagger/index.html")).StatusCode);
        var doc = await (await dev.CreateClient().GetAsync("/swagger/v1/swagger.json")).Content.ReadAsStringAsync();
        Assert.Contains("/query", doc);
        Assert.Contains("/ingest", doc);
        Assert.Contains("/audit", doc);

        using var prod = new ApiFactory { Environment = "Production", DevIssuer = false };
        Assert.Equal(HttpStatusCode.NotFound, (await prod.CreateClient().GetAsync("/swagger/index.html")).StatusCode);
    }
}
