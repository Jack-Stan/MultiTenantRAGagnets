using Npgsql;
using NpgsqlTypes;
using Pgvector;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;

namespace MultiTenantRAGagnets.Leakage.Infrastructure;

/// <summary>
/// Extra rows, in extra tenants, that make HNSW + RLS under-return actually possible (TESTING.md section 2).
///   hostile-noise  : many rows packed right next to the query point (other tenant, level 1)
///   hostile-victim : a few FORBIDDEN rows (level 3) even closer to the query point, and the rows the
///                    employee may really see (level 1) far away
///   hostile-small  : fewer rows than k, to test returned == available
/// With only a handful of rows Postgres would seq-scan and never touch the HNSW window, so this bug ships green.
/// </summary>
public sealed class HostileScenario
{
    public const int NoiseRows = 2500;
    public const int VictimEmployeeRows = 60;
    public const int VictimHrRows = 30;
    public const int SmallRows = 3;

    public const string NoiseSlug = "hostile-noise";
    public const string VictimSlug = "hostile-victim";
    public const string SmallSlug = "hostile-small";
    public static readonly string[] Slugs = [NoiseSlug, VictimSlug, SmallSlug];

    public static Guid TenantId(string slug) => Ids.Name($"leakage-tenant|{slug}");
    public static Guid DocId(string slug, int level) => Ids.Name($"leakage-doc|{slug}|{level}");
    public static Guid ChunkId(string slug, int level, int index) => Ids.Name($"leakage-chunk|{slug}|{level}|{index}");

    public const string QueryPointText = "hostile-query-point";
    public static float[] QueryPoint => FakeEmbeddingProvider.Embed(QueryPointText);

    public static int TotalRows => NoiseRows + VictimEmployeeRows + VictimHrRows + SmallRows;

    /// <summary>unit(q + eps * random unit vector): cosine to q is about 1/sqrt(1+eps^2).</summary>
    public static float[] Near(float[] q, string salt, double eps)
    {
        var r = FakeEmbeddingProvider.Embed(salt);
        var v = new double[q.Length];
        double ss = 0;
        for (var i = 0; i < q.Length; i++)
        {
            v[i] = q[i] + eps * r[i];
            ss += v[i] * v[i];
        }
        var n = Math.Sqrt(ss);
        return v.Select(x => (float)(x / n)).ToArray();
    }

    internal static IEnumerable<(string Slug, int Level, int Index, string Text, float[] Vector)> Rows()
    {
        var q = QueryPoint;
        for (var i = 0; i < NoiseRows; i++)
            yield return (NoiseSlug, 1, i, $"hostile noise row {i}", Near(q, $"noise-{i}", 0.30));
        for (var j = 0; j < VictimHrRows; j++)
            yield return (VictimSlug, 3, j, $"hostile victim hr-only row {j}", Near(q, $"victim-hr-{j}", 0.10));
        for (var j = 0; j < VictimEmployeeRows; j++)
            yield return (VictimSlug, 1, j, $"hostile victim employee-visible row {j}", FakeEmbeddingProvider.Embed($"victim-employee-{j}"));
        for (var j = 0; j < SmallRows; j++)
            yield return (SmallSlug, 1, j, $"hostile small row {j}", FakeEmbeddingProvider.Embed($"small-{j}"));
    }
}

public static class Seeder
{
    private sealed class Batcher(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        private NpgsqlBatch _batch = new(conn, tx);

        public void Add(string sql, params (string Name, object Value)[] ps)
        {
            var cmd = new NpgsqlBatchCommand(sql);
            foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
            _batch.BatchCommands.Add(cmd);
        }

        public async Task FlushAsync(bool force = false)
        {
            if (_batch.BatchCommands.Count == 0 || (!force && _batch.BatchCommands.Count < 200)) return;
            await _batch.ExecuteNonQueryAsync();
            await _batch.DisposeAsync();
            _batch = new NpgsqlBatch(conn, tx);
        }
    }

    public static async Task SeedAsync(NpgsqlDataSource owner, SyntheticCorpus corpus)
    {
        var tenantIds = corpus.Tenants.Select(t => t.Id)
            .Concat(HostileScenario.Slugs.Select(HostileScenario.TenantId)).ToArray();

        await using var conn = await owner.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Only this suite's own tenants are touched. Rows from the db smoke-test fixture (tenant-a/tenant-b) or
        // anything else stay exactly as they are; they simply count as "outside every manifest".
        foreach (var table in new[] { "audit_log", "chunks", "documents", "users", "tenants" })
        {
            var col = table == "tenants" ? "id" : "tenant_id";
            await using var del = new NpgsqlCommand($"DELETE FROM {table} WHERE {col} = ANY(@ids)", conn, tx);
            del.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, tenantIds);
            await del.ExecuteNonQueryAsync();
        }

        var b = new Batcher(conn, tx);

        foreach (var t in corpus.Tenants)
        {
            b.Add("INSERT INTO tenants (id, slug, name) VALUES (@id, @slug, @name)",
                ("id", t.Id), ("slug", t.Slug), ("name", t.Name));
            AddUsers(b, t.Id, t.Slug);
        }
        foreach (var slug in HostileScenario.Slugs)
        {
            var id = HostileScenario.TenantId(slug);
            b.Add("INSERT INTO tenants (id, slug, name) VALUES (@id, @slug, @name)", ("id", id), ("slug", slug), ("name", slug));
            AddUsers(b, id, slug);
        }
        await b.FlushAsync(force: true);

        foreach (var d in corpus.Documents)
        {
            b.Add("INSERT INTO documents (id, tenant_id, title, required_level, version) VALUES (@id, @t, @title, @lvl, @v)",
                ("id", d.Id), ("t", d.TenantId), ("title", d.Title), ("lvl", (short)d.RequiredLevel), ("v", d.Version));
            await b.FlushAsync();
        }
        foreach (var (slug, level) in new[] { (HostileScenario.NoiseSlug, 1), (HostileScenario.VictimSlug, 1), (HostileScenario.VictimSlug, 3), (HostileScenario.SmallSlug, 1) })
        {
            b.Add("INSERT INTO documents (id, tenant_id, title, required_level, version) VALUES (@id, @t, @title, @lvl, 1)",
                ("id", HostileScenario.DocId(slug, level)), ("t", HostileScenario.TenantId(slug)),
                ("title", $"{slug} level {level}"), ("lvl", (short)level));
        }
        await b.FlushAsync(force: true);

        // The generator's own chunk ids and texts; the embedding is exactly what IngestionService would produce
        // (FakeEmbeddingProvider over the chunk text), so stored ids match the manifest.
        var docTenant = corpus.Documents.ToDictionary(d => d.Id, d => d.TenantId);
        foreach (var c in corpus.Chunks)
        {
            b.Add("INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding, version) VALUES (@id, @doc, @t, @idx, @text, @emb, 1)",
                ("id", c.Id), ("doc", c.DocId), ("t", docTenant[c.DocId]), ("idx", c.ChunkIndex), ("text", c.Text),
                ("emb", new Vector(FakeEmbeddingProvider.Embed(c.Text))));
            await b.FlushAsync();
        }
        foreach (var (slug, level, index, text, vector) in HostileScenario.Rows())
        {
            b.Add("INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding, version) VALUES (@id, @doc, @t, @idx, @text, @emb, 1)",
                ("id", HostileScenario.ChunkId(slug, level, index)), ("doc", HostileScenario.DocId(slug, level)),
                ("t", HostileScenario.TenantId(slug)), ("idx", index), ("text", text), ("emb", new Vector(vector)));
            await b.FlushAsync();
        }
        await b.FlushAsync(force: true);

        await tx.CommitAsync();

        // Fresh statistics so plan choice is not left to stale defaults.
        await using var an = new NpgsqlCommand("ANALYZE chunks; ANALYZE documents;", conn);
        await an.ExecuteNonQueryAsync();
    }

    private static void AddUsers(Batcher b, Guid tenantId, string slug)
    {
        foreach (var role in Roles.All)
        {
            b.Add("INSERT INTO users (id, tenant_id, email, role) VALUES (@id, @t, @email, @role)",
                ("id", Ids.User(slug, role)), ("t", tenantId), ("email", $"{role}@{slug}.test"), ("role", role));
        }
    }

    /// <summary>
    /// Proves the seeded database, the generator's manifest and the SQL hierarchy function all agree, as the owner.
    /// A wrong oracle would make every later "0 leaks" (or "N leaks") meaningless, so this runs before any test.
    /// </summary>
    public static async Task VerifyAgainstManifestAsync(NpgsqlDataSource owner, SyntheticCorpus corpus)
    {
        await using var conn = await owner.OpenConnectionAsync();

        foreach (var t in corpus.Tenants)
        {
            var stored = await ReadIds(conn, "SELECT id FROM chunks WHERE tenant_id = @t", ("t", t.Id));
            var expected = corpus.Chunks.Where(c => c.TenantSlug == t.Slug).Select(c => c.Id).ToHashSet();
            if (!stored.SetEquals(expected))
                throw new InvalidOperationException(
                    $"Seed check failed: tenant {t.Slug} has {stored.Count} chunks in the database but the generator says {expected.Count}; ids differ.");
        }

        foreach (var m in corpus.Manifest)
        {
            var tenant = corpus.Tenants.Single(t => t.Slug == m.TenantSlug).Id;
            var visible = await ReadIds(conn,
                """
                SELECT c.id FROM chunks c
                JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
                WHERE c.tenant_id = @t AND rag_level_allows(rag_role_level(@role), d.required_level)
                """,
                ("t", tenant), ("role", m.Role));
            if (!visible.SetEquals(m.VisibleChunkIds))
                throw new InvalidOperationException(
                    $"Oracle check failed: manifest says {m.TenantSlug}/{m.Role} sees {m.VisibleChunkIds.Count} chunks, " +
                    $"the database hierarchy says {visible.Count}. The manifest or the schema is wrong; no leakage number can be trusted.");
        }
    }

    private static async Task<HashSet<Guid>> ReadIds(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var set = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetGuid(0));
        return set;
    }
}
