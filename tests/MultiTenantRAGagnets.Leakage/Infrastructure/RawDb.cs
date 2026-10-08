using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace MultiTenantRAGagnets.Leakage.Infrastructure;

/// <summary>
/// A hand-rolled app_user transaction with the same per-request context the store applies
/// (set_config(..., true) == SET LOCAL). Used where the test needs SQL the service never runs.
/// </summary>
public sealed class RawTx : IAsyncDisposable
{
    public NpgsqlConnection Conn { get; }
    public NpgsqlTransaction Tx { get; }

    private RawTx(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        Conn = conn;
        Tx = tx;
    }

    /// <summary>Opens a transaction WITHOUT any tenant/role context (the fail-closed case).</summary>
    public static async Task<RawTx> BeginBareAsync(NpgsqlDataSource ds)
    {
        var conn = await ds.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        return new RawTx(conn, tx);
    }

    public static async Task<RawTx> BeginAsync(NpgsqlDataSource ds, Guid tenantId, string role)
    {
        var raw = await BeginBareAsync(ds);
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT set_config('app.tenant_id',  @tenant, true),
                       set_config('app.role_level', coalesce(rag_role_level(@role)::text, ''), true)
                """, raw.Conn, raw.Tx);
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, role);
            await cmd.ExecuteNonQueryAsync();
            return raw;
        }
        catch
        {
            await raw.DisposeAsync();
            throw;
        }
    }

    public async Task ExecAsync(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, Conn, Tx);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<Guid>> IdsAsync(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, Conn, Tx);
        var set = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetGuid(0));
        return set;
    }

    public async Task<long> CountAsync(string table)
    {
        // table is a literal from the test code, never user input.
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", Conn, Tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<string> ScalarStringAsync(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, Conn, Tx);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        try { await Tx.RollbackAsync(); }
        catch { /* connection already broken; disposing discards it from the pool */ }
        await Tx.DisposeAsync();
        await Conn.DisposeAsync();
    }

    /// <summary>Invariant-culture pgvector text literal. Built only from this suite's own floats, never from input.</summary>
    public static string VectorLiteral(float[] v) =>
        "'[" + string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]'::vector";
}

public sealed record PolicyDef(string Name, string Cmd, bool Permissive, string? Using, string? WithCheck, bool ForPublic);

/// <summary>Exact picture of RLS on one table, so a deliberately broken policy can be put back exactly.</summary>
public sealed record RlsSnapshot(bool Enabled, bool Forced, IReadOnlyList<PolicyDef> Policies)
{
    public static async Task<RlsSnapshot> CaptureAsync(NpgsqlDataSource owner, string table = "chunks")
    {
        await using var conn = await owner.OpenConnectionAsync();
        bool enabled, forced;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = 'public.{table}'::regclass", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            await r.ReadAsync();
            enabled = r.GetBoolean(0);
            forced = r.GetBoolean(1);
        }

        var policies = new List<PolicyDef>();
        await using (var cmd = new NpgsqlCommand(
            $$"""
            SELECT p.polname, p.polcmd::text, p.polpermissive,
                   pg_get_expr(p.polqual, p.polrelid), pg_get_expr(p.polwithcheck, p.polrelid),
                   (p.polroles = '{0}'::oid[])
            FROM pg_policy p WHERE p.polrelid = 'public.{{table}}'::regclass ORDER BY p.polname
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                policies.Add(new PolicyDef(
                    r.GetString(0), r.GetString(1), r.GetBoolean(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5)));
            }
        }
        return new RlsSnapshot(enabled, forced, policies);
    }

    public bool SameAs(RlsSnapshot other) =>
        Enabled == other.Enabled && Forced == other.Forced && Policies.SequenceEqual(other.Policies);

    public string Describe() =>
        $"rowsecurity={Enabled} force={Forced} policies=[{string.Join("; ", Policies.Select(p => $"{p.Name} {p.Cmd} using({p.Using}) check({p.WithCheck})"))}]";
}

/// <summary>Breaks and restores RLS on the chunks table. Always run through try/finally with Restore.</summary>
public static class RlsControl
{
    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// "Dropping the policy" the naive way (DROP POLICY) leaves RLS enabled with NO policy, which means DENY ALL:
    /// 0 rows, 0 leaks, a control that passes for the wrong reason. Disabling RLS is what a missing policy
    /// really looks like to a query.
    /// </summary>
    public static async Task DisableRlsAsync(NpgsqlDataSource owner)
    {
        await using var conn = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("ALTER TABLE chunks NO FORCE ROW LEVEL SECURITY; ALTER TABLE chunks DISABLE ROW LEVEL SECURITY;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Replaces the chunks READ (SELECT) policy with a tenant-only one: the documents EXISTS / level clause is removed.
    /// The write policies are left alone, so the only thing that changes is who may READ which level.
    /// </summary>
    public static async Task ReplaceReadPolicyWithTenantOnlyAsync(NpgsqlDataSource owner, RlsSnapshot original)
    {
        var read = original.Policies.Single(p => p.Cmd == "r");
        var name = Q(read.Name);
        await using var conn = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"DROP POLICY {name} ON chunks; " +
            $"CREATE POLICY {name} ON chunks FOR SELECT USING (tenant_id = rag_current_tenant());", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task RestoreAsync(NpgsqlDataSource owner, RlsSnapshot original)
    {
        await using (var conn = await owner.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var existing in (await RlsSnapshot.CaptureAsync(owner)).Policies)
            {
                await using var drop = new NpgsqlCommand($"DROP POLICY IF EXISTS {Q(existing.Name)} ON chunks", conn, tx);
                await drop.ExecuteNonQueryAsync();
            }
            foreach (var p in original.Policies)
            {
                if (!p.ForPublic) throw new InvalidOperationException($"Policy {p.Name} targets specific roles; the restore helper only handles PUBLIC policies.");
                var cmdWord = p.Cmd switch { "*" => "ALL", "r" => "SELECT", "a" => "INSERT", "w" => "UPDATE", "d" => "DELETE", _ => throw new InvalidOperationException($"Unknown policy command '{p.Cmd}'.") };
                var sql = $"CREATE POLICY {Q(p.Name)} ON chunks AS {(p.Permissive ? "PERMISSIVE" : "RESTRICTIVE")} FOR {cmdWord}" +
                          (p.Using is null ? "" : $" USING ({p.Using})") +
                          (p.WithCheck is null ? "" : $" WITH CHECK ({p.WithCheck})");
                await using var create = new NpgsqlCommand(sql, conn, tx);
                await create.ExecuteNonQueryAsync();
            }
            await using (var en = new NpgsqlCommand(
                $"ALTER TABLE chunks {(original.Enabled ? "ENABLE" : "DISABLE")} ROW LEVEL SECURITY; " +
                $"ALTER TABLE chunks {(original.Forced ? "FORCE" : "NO FORCE")} ROW LEVEL SECURITY;", conn, tx))
            {
                await en.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var after = await RlsSnapshot.CaptureAsync(owner);
        if (!after.SameAs(original))
        {
            throw new InvalidOperationException(
                "RLS ON chunks WAS NOT RESTORED EXACTLY. The database is now in a modified state; re-run the migrations before trusting anything.\n" +
                $"before: {original.Describe()}\nafter:  {after.Describe()}");
        }
    }
}

/// <summary>The migrated policy set on chunks after 005_write_policies.sql: four per-command permissive policies, nothing else.</summary>
public static class RlsExpectations
{
    public static readonly (string Name, string Cmd)[] ChunkPolicies =
        [("chunks_delete", "d"), ("chunks_insert", "a"), ("chunks_select", "r"), ("chunks_update", "w")];

    public static void AssertChunkPolicySet(RlsSnapshot snap)
    {
        var actual = snap.Policies.Select(p => (p.Name, p.Cmd)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        Assert.True(actual.SequenceEqual(ChunkPolicies),
            $"chunks must carry exactly the four per-command policies {string.Join(", ", ChunkPolicies.Select(c => c.Name + "/" + c.Cmd))}. " + snap.Describe());
        Assert.All(snap.Policies, p => Assert.True(p.Permissive && p.ForPublic, $"policy {p.Name} must be permissive and for PUBLIC: " + snap.Describe()));

        var select = snap.Policies.Single(p => p.Cmd == "r");
        Assert.Contains("rag_current_tenant", select.Using);
        Assert.Contains("rag_level_allows", select.Using);
        Assert.Contains("documents", select.Using);   // the EXISTS on the parent document: the ACL is read, never copied
        Assert.Null(select.WithCheck);

        Assert.All(snap.Policies.Where(p => p.Cmd != "r"), p =>
        {
            var text = (p.Using ?? "") + " " + (p.WithCheck ?? "");
            Assert.True(text.Contains("rag_can_write", StringComparison.Ordinal), $"write policy {p.Name} does not carry rag_can_write(): " + snap.Describe());
            Assert.True(text.Contains("rag_current_tenant", StringComparison.Ordinal), $"write policy {p.Name} lost the tenant wall: " + snap.Describe());
        });
        Assert.NotNull(snap.Policies.Single(p => p.Cmd == "a").WithCheck);
        Assert.NotNull(snap.Policies.Single(p => p.Cmd == "w").WithCheck);
        Assert.NotNull(snap.Policies.Single(p => p.Cmd == "d").Using);
    }
}
