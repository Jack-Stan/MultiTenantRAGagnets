using MultiTenantRAGagnets.Core.Security;
using Npgsql;

namespace MultiTenantRAGagnets.Core.Tests;

/// <summary>No database: pins the SQL and the guard clauses. The function itself is tested by db/tests/10_identity_reader.sql.</summary>
public class UserDirectoryTests
{
    [Fact]
    public void Lookup_calls_the_resolve_function_and_nothing_else()
    {
        // Pinned verbatim: identity_reader has no table privileges, so any other statement would fail anyway,
        // but a drift back to a direct users/tenants query must fail a test, not only production.
        Assert.Equal(
            "SELECT user_id, tenant_id, role FROM public.rag_resolve_user(@slug, @email)",
            UserDirectorySql.ResolveUser);
    }

    [Fact]
    public void Lookup_sql_is_schema_qualified_parameterised_and_touches_no_table()
    {
        var sql = UserDirectorySql.ResolveUser;

        Assert.Contains("public.rag_resolve_user(", sql);
        Assert.Contains("@slug", sql);
        Assert.Contains("@email", sql);
        Assert.DoesNotContain("users", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenants", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Directory_requires_a_data_source()
    {
        Assert.Throws<ArgumentNullException>(() => new NpgsqlUserDirectory(null!));
    }

    [Theory]
    [InlineData("", "a@b.test")]
    [InlineData("  ", "a@b.test")]
    [InlineData("acme", "")]
    [InlineData("acme", "   ")]
    public async Task Blank_input_is_rejected_before_any_connection_is_attempted(string slug, string email)
    {
        // Never connects: the data source is built but validation throws first.
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=x;Username=identity_reader");
        var directory = new NpgsqlUserDirectory(source);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => directory.FindAsync(slug, email));
    }
}
