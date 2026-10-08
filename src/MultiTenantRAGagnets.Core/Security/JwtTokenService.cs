using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MultiTenantRAGagnets.Core.Retrieval;

namespace MultiTenantRAGagnets.Core.Security;

public sealed class JwtOptions
{
    public const int MinKeyLength = 32;

    /// <summary>HS256 signing secret. From env / user-secrets (Jwt:SigningKey); NEVER committed. Required.</summary>
    public string? SigningKey { get; set; }
    public string Issuer { get; set; } = "multitenant-rag";
    public string Audience { get; set; } = "multitenant-rag-api";
    public int LifetimeMinutes { get; set; } = 60;
}

/// <summary>Fail-fast validation: the host refuses to start without a usable signing key.</summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < JwtOptions.MinKeyLength)
        {
            return ValidateOptionsResult.Fail(
                $"Jwt:SigningKey is missing or shorter than {JwtOptions.MinKeyLength} characters. " +
                "Provide it via user-secrets (dotnet user-secrets set \"Jwt:SigningKey\" ...) or the Jwt__SigningKey " +
                "environment variable; it is never committed.");
        }
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
            return ValidateOptionsResult.Fail("Jwt:Issuer and Jwt:Audience must not be empty.");
        if (options.LifetimeMinutes is < 1 or > 1440)
            return ValidateOptionsResult.Fail("Jwt:LifetimeMinutes must be between 1 and 1440.");
        return ValidateOptionsResult.Success;
    }
}

/// <summary>The identity a verified token carries. Tenant and role come from here and nowhere else.</summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role)
{
    public CallerContext ToCaller() => new(TenantId, UserId, Role);

    /// <summary>Reads the claims issued by <see cref="JwtTokenService"/>. Null when any required claim is absent or malformed.</summary>
    public static AuthenticatedUser? FromClaims(IEnumerable<Claim> claims)
    {
        string? Find(string type) => claims.FirstOrDefault(c => c.Type == type)?.Value;

        if (!Guid.TryParse(Find(JwtTokenService.SubjectClaim), out var user) || user == Guid.Empty) return null;
        if (!Guid.TryParse(Find(JwtTokenService.TenantClaim), out var tenant) || tenant == Guid.Empty) return null;
        var role = Find(JwtTokenService.RoleClaim);
        if (string.IsNullOrWhiteSpace(role)) return null;
        return new AuthenticatedUser(user, tenant, role);
    }
}

/// <summary>
/// Issues and validates the locally signed JWT (HS256 only). Claims: <c>sub</c> (user id),
/// <c>tenant</c> (tenant id), <c>role</c>. Validation pins algorithm, issuer, audience, lifetime and
/// signature; there is no path that accepts an unsigned or differently-signed token.
/// </summary>
public sealed class JwtTokenService
{
    public const string SubjectClaim = "sub";
    public const string TenantClaim = "tenant";
    public const string RoleClaim = "role";

    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _key;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        var check = new JwtOptionsValidator().Validate(null, _options);
        if (check.Failed) throw new InvalidOperationException(check.FailureMessage);
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey!));
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Issue(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.UserId == Guid.Empty || user.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(user.Role))
            throw new ArgumentException("User, tenant and role are all required to issue a token.", nameof(user));

        var now = _time.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_options.LifetimeMinutes),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [SubjectClaim] = user.UserId.ToString("D"),
                [TenantClaim] = user.TenantId.ToString("D"),
                [RoleClaim] = user.Role,
            },
        };
        return _handler.CreateToken(descriptor);
    }

    /// <summary>The same parameters back the API's JwtBearer middleware, so there is one definition of "valid".</summary>
    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        RequireSignedTokens = true,
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = SubjectClaim,
        RoleClaimType = RoleClaim,
    };

    /// <summary>Returns the identity, or null if the token is invalid for any reason (reason is deliberately not leaked).</summary>
    public async Task<AuthenticatedUser?> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var result = await _handler.ValidateTokenAsync(token, CreateValidationParameters()).ConfigureAwait(false);
        return result.IsValid ? AuthenticatedUser.FromClaims(result.ClaimsIdentity.Claims) : null;
    }
}
