using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using MultiTenantRAGagnets.Api;
using MultiTenantRAGagnets.Core;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Security;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Provider selection: "Fake" (default, keyless, the CI path) or "Ollama".
builder.Services.AddRagProviders(builder.Configuration);

// Retrieval, ingestion, audit, JWT, Npgsql store. Jwt:SigningKey, Audit:HashKey and ConnectionStrings:App are
// validated at host start: the app refuses to boot without them.
builder.Services.AddRagCore(builder.Configuration);

// Dev-only token issuer. Off unless explicitly enabled (appsettings.Development.json enables it).
var devTokenIssuerEnabled = builder.Configuration.GetValue<bool>("Auth:DevTokenIssuer:Enabled");
if (devTokenIssuerEnabled)
{
    var identityConnection = builder.Configuration.GetConnectionString("Identity");
    if (string.IsNullOrWhiteSpace(identityConnection))
        throw new InvalidOperationException(
            "Auth:DevTokenIssuer:Enabled is true but ConnectionStrings:Identity is not set. " +
            "It is the separate (non-request-path) connection used only to look users up before a tenant is known.");
    builder.Services.AddSingleton<IUserDirectory>(_ => new NpgsqlUserDirectory(NpgsqlDataSource.Create(identityConnection)));
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtTokenService>((options, jwt) =>
    {
        options.MapInboundClaims = false; // keep "sub" / "tenant" / "role" as issued
        options.TokenValidationParameters = jwt.CreateValidationParameters();
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.TenantAdmin, p => p.RequireAuthenticatedUser().RequireClaim(JwtTokenService.RoleClaim, Roles.HrAdmin));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "MultiTenantRAGagnets", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Locally issued JWT carrying tenant + role.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(ErrorMapping.HandleAsync));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (devTokenIssuerEnabled)
{
    // DEV ONLY. Anyone who can reach this can mint a token for any seeded user: token forgery is out of
    // scope for the MVP threat model (README), and this endpoint is why it is off by default.
    app.MapPost("/auth/dev-token", async (DevTokenRequest req, IUserDirectory users, JwtTokenService jwt, IOptions<JwtOptions> jwtOptions, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(req.Tenant) || string.IsNullOrWhiteSpace(req.Email))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["tenant and email are required."] });

        var user = await users.FindAsync(req.Tenant.Trim(), req.Email.Trim(), ct);
        // Same answer for "no such tenant" and "no such user": no enumeration.
        if (user is null) return Results.Unauthorized();

        return Results.Ok(new DevTokenResponse(jwt.Issue(user), jwtOptions.Value.LifetimeMinutes));
    })
    .WithTags("Auth (dev only)")
    .AllowAnonymous();
}

app.MapPost("/query", async (QueryRequest req, ClaimsPrincipal principal, RetrievalService retrieval, CancellationToken ct) =>
{
    if (!principal.TryGetCaller(out var caller)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["question"] = ["question is required."] });

    // Tenant and role come from the verified token only; the request body has no such fields.
    var result = await retrieval.QueryAsync(caller, req.Question, req.K, ct);
    return Results.Ok(new QueryResponse(result.Answer, result.CitedChunkIds, result.Model, result.LatencyMs));
})
.RequireAuthorization()
.WithTags("Query");

app.MapPost("/ingest", async (IngestBody req, ClaimsPrincipal principal, CallerAccessor accessor, IngestionService ingestion, CancellationToken ct) =>
{
    if (!principal.TryGetCaller(out var caller)) return Results.Unauthorized();
    accessor.Set(caller); // lets the store-backed writer apply this caller's tenant/role in its transaction

    // DocumentId is server-generated; the tenant is the token's, never the body's.
    var result = await ingestion.IngestAsync(
        new IngestRequest(Guid.NewGuid(), caller.TenantId, req.Title ?? "", req.RequiredLevel, Version: 1, req.Text ?? ""), ct);
    return Results.Created($"/documents/{result.DocumentId}", result);
})
.RequireAuthorization(Policies.TenantAdmin)
.WithTags("Ingest");

app.MapGet("/audit", async (ClaimsPrincipal principal, IChunkStore store, int? limit, CancellationToken ct) =>
{
    if (!principal.TryGetCaller(out var caller)) return Results.Unauthorized();
    var take = Math.Clamp(limit ?? 50, 1, 500);

    await using var session = await store.BeginAsync(caller, ct);
    var rows = await session.ListAuditAsync(take, ct);
    await session.CommitAsync(ct);
    return Results.Ok(rows);
})
.RequireAuthorization(Policies.TenantAdmin)
.WithTags("Audit");

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

namespace MultiTenantRAGagnets.Api
{
    public static class Roles
    {
        /// <summary>Top of the hierarchy in db/migrations/001_schema.sql; the only role allowed to ingest and read /audit.</summary>
        public const string HrAdmin = "hr-admin";
    }

    public static class Policies
    {
        public const string TenantAdmin = "TenantAdmin";
    }

    public sealed record DevTokenRequest(string? Tenant, string? Email);
    public sealed record DevTokenResponse(string Token, int ExpiresInMinutes);
    public sealed record QueryRequest(string? Question, int? K);
    public sealed record QueryResponse(string Answer, IReadOnlyList<Guid> CitedChunkIds, string Model, int LatencyMs);
    public sealed record IngestBody(string? Title, string? Text, int RequiredLevel);

    public static class PrincipalExtensions
    {
        public static bool TryGetCaller(this ClaimsPrincipal principal, out CallerContext caller)
        {
            var user = AuthenticatedUser.FromClaims(principal.Claims);
            caller = user?.ToCaller()!;
            return user is not null;
        }
    }

    public static class ErrorMapping
    {
        /// <summary>Boring but necessary: no stack traces or SQL text in responses; the real error goes to the log.</summary>
        public static async Task HandleAsync(HttpContext context)
        {
            var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Errors");

            var (status, title) = ex switch
            {
                ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request."),
                PostgresException { SqlState: "42501" } => (StatusCodes.Status403Forbidden, "Not permitted."),
                HttpRequestException => (StatusCodes.Status502BadGateway, "The model provider failed."),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error."),
            };

            if (status >= 500) logger.LogError(ex, "Unhandled error serving {Path}", context.Request.Path);
            else logger.LogWarning("Request rejected ({Status}) on {Path}: {Type}", status, context.Request.Path, ex?.GetType().Name);

            context.Response.StatusCode = status;
            // ArgumentException messages are ours (validation text); everything else stays generic.
            var detail = ex is ArgumentException ae ? ae.Message : null;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail });
        }
    }
}
