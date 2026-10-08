using Npgsql;
using NpgsqlTypes;

namespace MultiTenantRAGagnets.Core.Security;

/// <summary>
/// Resolves (tenant slug, email) to a user BEFORE the tenant context exists. That lookup cannot go through
/// the request-path <c>app_user</c> connection: tenants/users are tenant-scoped under RLS, so an unset
/// context sees nothing (by design, fail-closed).
///
/// The lookup uses a SEPARATE connection string, <c>ConnectionStrings:Identity</c>, which must log in as the
/// <c>identity_reader</c> role (db/migrations/004_identity_role.sql). That role holds no table privileges; its
/// only privilege is EXECUTE on <c>public.rag_resolve_user(slug, email)</c>, a SECURITY DEFINER function that
/// returns zero rows for an unknown tenant and for an unknown user alike. It is used only by the dev
/// token-issuing endpoint (off unless <c>Auth:DevTokenIssuer:Enabled=true</c>, Development only) and must never
/// be the request-path connection or be reachable from /query, /ingest or /audit.
/// </summary>
public interface IUserDirectory
{
    Task<AuthenticatedUser?> FindAsync(string tenantSlug, string email, CancellationToken cancellationToken = default);
}

/// <summary>The one statement the identity connection runs. Pinned by a unit test, like <c>RetrievalSql</c>.</summary>
internal static class UserDirectorySql
{
    public const string ResolveUser = "SELECT user_id, tenant_id, role FROM public.rag_resolve_user(@slug, @email)";
}

/// <summary>
/// UNVERIFIED against a real database (CI runs db/tests/10_identity_reader.sql, not this class).
/// Parameterised; returns null (not an error) when the function yields zero rows. Matching is exact: the
/// function does no case folding or trimming, so callers normalise.
/// </summary>
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

        await using var cmd = _identitySource.CreateCommand(UserDirectorySql.ResolveUser);
        cmd.Parameters.AddWithValue("slug", NpgsqlDbType.Text, tenantSlug);
        cmd.Parameters.AddWithValue("email", NpgsqlDbType.Text, email);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AuthenticatedUser(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))
            : null;
    }
}
