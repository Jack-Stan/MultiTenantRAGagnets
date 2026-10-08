using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MultiTenantRAGagnets.Core.Audit;

public sealed class AuditOptions
{
    public const int MinKeyLength = 32;

    /// <summary>Secret for the query hash. From env / user-secrets (Audit:HashKey); never committed.</summary>
    public string? HashKey { get; set; }
}

public sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options) =>
        string.IsNullOrWhiteSpace(options.HashKey) || options.HashKey.Length < AuditOptions.MinKeyLength
            ? ValidateOptionsResult.Fail(
                $"Audit:HashKey is missing or shorter than {AuditOptions.MinKeyLength} characters. " +
                "Provide it via user-secrets or the Audit__HashKey environment variable; it is never committed.")
            : ValidateOptionsResult.Success;
}

public interface IQueryHasher
{
    string Hash(string queryText);
}

/// <summary>
/// HMAC-SHA256 under a server-side secret. A plain SHA-256 of a short natural-language query can be
/// reversed by guessing candidate queries; without the key someone holding the audit table cannot even
/// test a guess. Same query + same key gives the same hash, so repeats stay correlatable.
/// Output: 64 lowercase hex characters. The query text is hashed exactly as received (no normalisation).
/// </summary>
public sealed class HmacQueryHasher : IQueryHasher
{
    private readonly byte[] _key;

    public HmacQueryHasher(IOptions<AuditOptions> options)
    {
        var result = new AuditOptionsValidator().Validate(null, options.Value);
        if (result.Failed) throw new InvalidOperationException(result.FailureMessage);
        _key = Encoding.UTF8.GetBytes(options.Value.HashKey!);
    }

    public string Hash(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(queryText)));
    }
}
