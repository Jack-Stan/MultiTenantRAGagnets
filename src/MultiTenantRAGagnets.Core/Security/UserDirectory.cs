using Npgsql;
using NpgsqlTypes;

namespace MultiTenantRAGagnets.Core.Security;

/// <summary>
/// Resolves (tenant slug, email) to a user BEFORE the tenant context exists. That lookup cannot go through
/// the request-path <c>app_user</c> connection: tenants/users are tenant-scoped under RLS, so an unset
/// context sees nothing (by design, fail-closed).
///
/// Chosen approach (simplest safe one, documented as a decision): the lookup uses a SEPARATE connection
/// string, <c>ConnectionStrings:Identity</c>, used only by the dev token-issuing endpoint, which is off unless
/// <c>Auth:DevTokenIssuer:Enabled=true</c>. It must never be the request-path connection and never be
/// reachable from /query, /ingest or /audit. Locally it can point at the owner; the better target is a
/// dedicated role with SELECT on tenants and users only (a db/ change, requested from RÓISÍN).
/// </summary>
public interface IUserDirectory
{
    Task<AuthenticatedUser?> FindAsync(string tenantSlug, string email, CancellationToken cancellationToken = default);
}

/// <summary>UNVERIFIED against a real database. Parameterised; returns null (not an error) for an unknown pair.</summary>
public sealed class NpgsqlUserDirectory : IUserDirectory
{
    private readonly NpgsqlDataSource _identitySource;

    public NpgsqlUserDirectory(NpgsqlDataSource identitySource)
    {
        _identitySource = identitySource ?? throw new ArgumentNullException(nameof(identitySource));
    }

    public async Task<AuthenticatedUser?> FindAsync(string tenantSlug, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        await using var cmd = _identitySource.CreateCommand(
            """
            SELECT u.id, u.tenant_id, u.role
            FROM users u
            JOIN tenants t ON t.id = u.tenant_id
            WHERE t.slug = @slug AND u.email = @email
            """);
        cmd.Parameters.AddWithValue("slug", NpgsqlDbType.Text, tenantSlug);
        cmd.Parameters.AddWithValue("email", NpgsqlDbType.Text, email);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AuthenticatedUser(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))
            : null;
    }
}
