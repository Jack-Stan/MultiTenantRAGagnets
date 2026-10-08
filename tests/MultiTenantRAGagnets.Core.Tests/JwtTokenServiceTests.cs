using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MultiTenantRAGagnets.Core.Security;

namespace MultiTenantRAGagnets.Core.Tests;

public class JwtTokenServiceTests
{
    // Throwaway values that exist only inside this test file; not a real key.
    private const string KeyA = "unit-test-signing-key-A-0123456789abcdef";
    private const string KeyB = "unit-test-signing-key-B-0123456789abcdef";

    private static readonly AuthenticatedUser Alice =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "manager");

    private static JwtTokenService Service(string key = KeyA, TimeProvider? time = null, string issuer = "iss", string audience = "aud") =>
        new(Options.Create(new JwtOptions { SigningKey = key, Issuer = issuer, Audience = audience, LifetimeMinutes = 10 }), time);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Round_trip_carries_user_tenant_and_role()
    {
        var svc = Service();

        var user = await svc.ValidateAsync(svc.Issue(Alice));

        Assert.Equal(Alice, user);
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var svc = Service();
        var parts = svc.Issue(Alice).Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var forged = payload.Replace("\"manager\"", "\"hr-admin\"");
        Assert.NotEqual(payload, forged);
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(forged)}.{parts[2]}";

        Assert.Null(await svc.ValidateAsync(tampered));
    }

    [Fact]
    public async Task Tampered_tenant_is_rejected()
    {
        var svc = Service();
        var parts = svc.Issue(Alice).Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var forged = payload.Replace(Alice.TenantId.ToString("D"), Guid.NewGuid().ToString("D"));
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(forged)}.{parts[2]}";

        Assert.Null(await svc.ValidateAsync(tampered));
    }

    [Fact]
    public async Task Truncated_or_swapped_signature_is_rejected()
    {
        var svc = Service();
        var token = svc.Issue(Alice);
        var other = svc.Issue(Alice with { Role = "employee" });

        Assert.Null(await svc.ValidateAsync(token[..^4]));
        var swapped = string.Join('.', token.Split('.')[..2].Append(other.Split('.')[2]));
        Assert.Null(await svc.ValidateAsync(swapped));
    }

    [Fact]
    public async Task Token_signed_with_a_different_key_is_rejected()
    {
        var token = Service(KeyB).Issue(Alice);

        Assert.Null(await Service(KeyA).ValidateAsync(token));
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"iss":"iss","aud":"aud","sub":"{{Alice.UserId}}","tenant":"{{Alice.TenantId}}","role":"hr-admin","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""");

        Assert.Null(await Service().ValidateAsync($"{header}.{payload}."));
    }

    [Fact]
    public async Task Hs512_token_signed_with_the_very_same_key_is_rejected_because_only_hs256_is_allowed()
    {
        const string longKey = "unit-test-long-key-0123456789abcdef-0123456789abcdef-0123456789abcdef"; // >= 64 bytes so HS512 can sign
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "iss",
            Audience = "aud",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(longKey)), SecurityAlgorithms.HmacSha512),
            Claims = new Dictionary<string, object> { ["sub"] = Alice.UserId.ToString(), ["tenant"] = Alice.TenantId.ToString(), ["role"] = "manager" },
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        Assert.Null(await Service(longKey).ValidateAsync(token));
        // control: the same key with HS256 is accepted, so the rejection above is the algorithm and nothing else
        var svc = Service(longKey);
        Assert.NotNull(await svc.ValidateAsync(svc.Issue(Alice)));
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var issuedLongAgo = Service(time: new FixedTime(DateTimeOffset.UtcNow.AddHours(-3))).Issue(Alice);

        Assert.Null(await Service().ValidateAsync(issuedLongAgo));
    }

    [Fact]
    public async Task Wrong_issuer_or_audience_is_rejected()
    {
        Assert.Null(await Service().ValidateAsync(Service(issuer: "someone-else").Issue(Alice)));
        Assert.Null(await Service().ValidateAsync(Service(audience: "another-api").Issue(Alice)));
    }

    [Fact]
    public async Task Validly_signed_token_missing_the_tenant_claim_is_rejected()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "iss",
            Audience = "aud",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(KeyA)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = Alice.UserId.ToString(), ["role"] = "manager" },
        };

        Assert.Null(await Service().ValidateAsync(new JsonWebTokenHandler().CreateToken(descriptor)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    public async Task Junk_is_rejected(string token) => Assert.Null(await Service().ValidateAsync(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void Missing_or_short_signing_key_fails_fast(string? key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JwtTokenService(Options.Create(new JwtOptions { SigningKey = key })));
        Assert.Contains("Jwt:SigningKey", ex.Message);
        if (!string.IsNullOrEmpty(key)) Assert.DoesNotContain(key, ex.Message); // the message never echoes the secret
    }

    [Fact]
    public void Options_validator_rejects_a_missing_key()
    {
        Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions()).Failed);
        Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = KeyA }).Succeeded);
    }

    [Fact]
    public void Issuing_with_an_empty_tenant_or_role_is_refused()
    {
        var svc = Service();
        Assert.Throws<ArgumentException>(() => svc.Issue(Alice with { TenantId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => svc.Issue(Alice with { Role = " " }));
    }
}
