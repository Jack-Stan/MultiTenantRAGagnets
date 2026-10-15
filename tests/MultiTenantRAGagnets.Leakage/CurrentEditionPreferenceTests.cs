using Npgsql;
using NpgsqlTypes;
using Pgvector;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// The current-edition ranking preference on the real database path (migration 006, documents.superseded_at).
///
/// What this proves, against Postgres + pgvector + RLS as app_user (non-owner, NOBYPASSRLS):
///   1. The preference reorders: three withdrawn editions nearer to the query than the current one come back in
///      the order withdrawn-first with the preference off and current-first with it on.
///   2. It never changes WHO is permitted: for every role, with the preference off and on, draining the search
///      returns exactly the rows the SQL hierarchy function says the caller may see, in both filter modes (the app
///      predicate and RLS-only), and never a row of the other tenant that is CURRENT and nearer.
///   3. returned == min(k, visible) holds with the over-fetch, including when the index path is starved and the
///      exact-scan fallback has to carry the preference.
///   4. The new column honours the write policies: only an hr-admin of the tenant can mark a document superseded.
///
/// The scenario lives in two throwaway tenants created and deleted by the test, separate from the leakage corpus.
/// </summary>
[Collection(LeakageCollection.Name)]
public class CurrentEditionPreferenceTests(LeakageFixture fx)
{
    private static readonly Guid TenantA = Ids.Name("ranking|tenant|a");
    private static readonly Guid TenantB = Ids.Name("ranking|tenant|b");

    private static Guid DocId(string key) => Ids.Name($"ranking|doc|{key}");
    private static Guid ChunkId(string key) => Ids.Name($"ranking|chunk|{key}");

    private static readonly RankingPreference Strict = new() { PreferCurrentEdition = true };
    private static readonly RankingPreference[] Both = [RankingPreference.None, Strict];

    /// <summary>Unit vector at cosine distance <paramref name="distance"/> from the query point e0.</summary>
    private static float[] AtDistance(double distance, int axis)
    {
        var v = new float[EmbeddingDefaults.Dimension];
        var cos = 1.0 - distance;
        v[0] = (float)cos;
        v[axis] = (float)Math.Sqrt(Math.Max(0, 1 - cos * cos));
        return v;
    }

    private static float[] QueryPoint()
    {
        var v = new float[EmbeddingDefaults.Dimension];
        v[0] = 1f;
        return v;
    }

    // (key, tenant, level, superseded, distance from the query)
    private static readonly (string Key, Guid Tenant, int Level, bool Superseded, double Distance)[] Rows =
    [
        ("old1",       TenantA, 1, true,  0.10),
        ("old2",       TenantA, 1, true,  0.11),
        ("old3",       TenantA, 1, true,  0.12),
        ("current",    TenantA, 1, false, 0.13),
        ("mgr-cur",    TenantA, 2, false, 0.05),
        ("hr-cur",     TenantA, 3, false, 0.02),
        ("hr-old",     TenantA, 3, true,  0.03),
        // The wall: another tenant's CURRENT chunk, nearest of all. A current-edition boost must never pull it in.
        ("b-current",  TenantB, 1, false, 0.01),
    ];

    private static readonly CallerContext EmployeeA = new(TenantA, Ids.Name("ranking|user|emp"), Roles.Employee);
    private static readonly CallerContext ManagerA = new(TenantA, Ids.Name("ranking|user|mgr"), Roles.Manager);
    private static readonly CallerContext HrA = new(TenantA, Ids.Name("ranking|user|hr"), Roles.HrAdmin);

    private async Task SeedAsync()
    {
        await CleanAsync();
        await using var conn = await fx.OwnerDs.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var (t, slug) in new[] { (TenantA, "ranking-a"), (TenantB, "ranking-b") })
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO tenants (id, slug, name) VALUES (@id, @slug, @slug)", conn, tx);
            cmd.Parameters.AddWithValue("id", t);
            cmd.Parameters.AddWithValue("slug", slug);
            await cmd.ExecuteNonQueryAsync();
        }

        var axis = 1;
        foreach (var (key, tenant, level, superseded, distance) in Rows)
        {
            await using (var d = new NpgsqlCommand(
                "INSERT INTO documents (id, tenant_id, title, required_level, version, superseded_at) " +
                "VALUES (@id, @t, @title, @lvl, 1, CASE WHEN @sup THEN now() END)", conn, tx))
            {
                d.Parameters.AddWithValue("id", DocId(key));
                d.Parameters.AddWithValue("t", tenant);
                d.Parameters.AddWithValue("title", $"ranking {key}");
                d.Parameters.AddWithValue("lvl", (short)level);
                d.Parameters.AddWithValue("sup", superseded);
                await d.ExecuteNonQueryAsync();
            }
            await using (var c = new NpgsqlCommand(
                "INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding, version) " +
                "VALUES (@id, @doc, @t, 0, @text, @emb, 1)", conn, tx))
            {
                c.Parameters.AddWithValue("id", ChunkId(key));
                c.Parameters.AddWithValue("doc", DocId(key));
                c.Parameters.AddWithValue("t", tenant);
                c.Parameters.AddWithValue("text", $"ranking chunk {key}");
                c.Parameters.AddWithValue("emb", new Vector(AtDistance(distance, axis++)));
                await c.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();

        await using var an = new NpgsqlCommand("ANALYZE chunks; ANALYZE documents;", conn);
        await an.ExecuteNonQueryAsync();
    }

    private async Task CleanAsync()
    {
        await using var conn = await fx.OwnerDs.OpenConnectionAsync();
        foreach (var table in new[] { "chunks", "documents" })
        {
            await using var del = new NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id = ANY(@ids)", conn);
            del.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { TenantA, TenantB });
            await del.ExecuteNonQueryAsync();
        }
        await using var delT = new NpgsqlCommand("DELETE FROM tenants WHERE id = ANY(@ids)", conn);
        delT.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { TenantA, TenantB });
        await delT.ExecuteNonQueryAsync();
    }

    /// <summary>What the caller may see, computed by the owner with the SAME SQL hierarchy function the policies use.</summary>
    private async Task<HashSet<Guid>> VisibleAsync(CallerContext caller)
    {
        await using var conn = await fx.OwnerDs.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT c.id FROM chunks c
            JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
            WHERE c.tenant_id = @t AND rag_level_allows(rag_role_level(@role), d.required_level)
            """, conn);
        cmd.Parameters.AddWithValue("t", caller.TenantId);
        cmd.Parameters.AddWithValue("role", caller.Role);
        var set = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetGuid(0));
        return set;
    }

    private async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        NpgsqlDataSource ds, CallerContext caller, int k, RankingPreference ranking, RetrievalFilterMode mode = RetrievalFilterMode.AppAndRls)
    {
        var store = new NpgsqlChunkStore(ds);
        await using var session = await store.BeginAsync(caller);
        return await session.SearchAsync(QueryPoint(), k, mode, ranking);
    }

    [DbFact]
    public async Task Preference_puts_the_current_edition_first_where_plain_search_puts_withdrawn_ones_first()
    {
        await SeedAsync();
        try
        {
            foreach (var ds in new[] { fx.AppExactDs, fx.AppForcedHnswDs, fx.AppDs })
            {
                var off = await SearchAsync(ds, EmployeeA, 4, RankingPreference.None);
                Assert.Equal([ChunkId("old1"), ChunkId("old2"), ChunkId("old3"), ChunkId("current")], off.Select(c => c.Id));

                var on = await SearchAsync(ds, EmployeeA, 4, Strict);
                Assert.Equal(ChunkId("current"), on[0].Id);
                Assert.False(on[0].Superseded);
                Assert.All(on.Skip(1), c => Assert.True(c.Superseded));
                // Demoted, not hidden: the withdrawn editions are all still there, in distance order.
                Assert.Equal([ChunkId("current"), ChunkId("old1"), ChunkId("old2"), ChunkId("old3")], on.Select(c => c.Id));

                // k=1: the only thing that matters for MRR.
                var top = await SearchAsync(ds, EmployeeA, 1, Strict);
                Assert.Equal([ChunkId("current")], top.Select(c => c.Id));
            }
        }
        finally { await CleanAsync(); }
    }

    [DbFact]
    public async Task Preference_never_changes_the_permitted_set_in_either_filter_mode()
    {
        await SeedAsync();
        try
        {
            foreach (var caller in new[] { EmployeeA, ManagerA, HrA })
            {
                var visible = await VisibleAsync(caller);
                Assert.DoesNotContain(ChunkId("b-current"), visible); // the oracle itself excludes the other tenant

                foreach (var ds in new[] { fx.AppExactDs, fx.AppForcedHnswDs })
                foreach (var mode in new[] { RetrievalFilterMode.AppAndRls, RetrievalFilterMode.RlsOnly })
                {
                    var sets = new List<HashSet<Guid>>();
                    foreach (var ranking in Both)
                    {
                        // k far above the visible count drains the search: the returned set IS the permitted set.
                        var rows = await SearchAsync(ds, caller, 50, ranking, mode);
                        Assert.Equal(visible.Count, rows.Count);
                        Assert.Equal(visible, rows.Select(c => c.Id).ToHashSet());
                        Assert.DoesNotContain(rows, c => c.TenantId != caller.TenantId);
                        sets.Add(rows.Select(c => c.Id).ToHashSet());
                    }
                    Assert.True(sets[0].SetEquals(sets[1]),
                        $"permitted set differs with and without the preference for {caller.Role}/{mode}");
                }
            }

            // The employee never receives the above-role CURRENT chunks even though the preference "likes" current ones.
            var emp = await SearchAsync(fx.AppForcedHnswDs, EmployeeA, 50, Strict);
            Assert.DoesNotContain(emp, c => c.Id == ChunkId("hr-cur") || c.Id == ChunkId("mgr-cur") || c.Id == ChunkId("hr-old"));
        }
        finally { await CleanAsync(); }
    }

    [DbFact]
    public async Task Preference_keeps_returned_equal_to_min_k_visible()
    {
        await SeedAsync();
        try
        {
            var visible = (await VisibleAsync(EmployeeA)).Count; // 4
            foreach (var ds in new[] { fx.AppExactDs, fx.AppForcedHnswDs, fx.AppDs })
            foreach (var k in new[] { 1, 2, 3, 4, 5, 20 })
            foreach (var ranking in Both)
            {
                var rows = await SearchAsync(ds, EmployeeA, k, ranking);
                Assert.Equal(Math.Min(k, visible), rows.Count);
                Assert.Equal(rows.Count, rows.Select(c => c.Id).Distinct().Count());
            }
        }
        finally { await CleanAsync(); }
    }

    [DbFact]
    public async Task Preference_with_a_starved_index_still_returns_k_through_the_exact_fallback()
    {
        // Same starvation as IndexExhaustionFallbackTests (iterative scan OFF on the hostile victim tenant): the index path
        // returns 0 although the caller can see more, so the store must repeat the search as an exact scan. With the
        // over-fetch (pool = 4 x k) the guard is still keyed on k, so the fallback must still fire and still fill k.
        const int k = 5;
        var victim = new CallerContext(
            HostileScenario.TenantId(HostileScenario.VictimSlug),
            Ids.User(HostileScenario.VictimSlug, Roles.Employee),
            Roles.Employee);

        var store = new NpgsqlChunkStore(fx.AppForcedHnswDs, "off");
        foreach (var mode in new[] { RetrievalFilterMode.AppAndRls, RetrievalFilterMode.RlsOnly })
        {
            var before = store.IndexFallbackCount;
            await using var session = await store.BeginAsync(victim);
            var rows = await session.SearchAsync(HostileScenario.QueryPoint, k, mode, Strict);
            Assert.Equal(k, rows.Count);
            Assert.All(rows, c => Assert.Equal(victim.TenantId, c.TenantId));
            Assert.Equal(before + 1, store.IndexFallbackCount);
            // No superseded rows in this scenario, so the preference leaves the pure distance order intact.
            for (var i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].Distance <= rows[i].Distance);
        }
    }

    [DbFact]
    public async Task Only_an_hr_admin_of_the_tenant_can_mark_a_document_superseded()
    {
        await SeedAsync();
        try
        {
            var doc = DocId("current");

            // Employee: the 005 write policy hides the row from UPDATE, so 0 rows are touched and nothing changes.
            await using (var raw = await RawTx.BeginAsync(fx.AppDs, EmployeeA.TenantId, Roles.Employee))
            {
                await using var cmd = new NpgsqlCommand("UPDATE documents SET superseded_at = now() WHERE id = @id", raw.Conn, raw.Tx);
                cmd.Parameters.AddWithValue("id", doc);
                Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
            }

            // hr-admin of ANOTHER tenant: the tenant wall hides it.
            await using (var raw = await RawTx.BeginAsync(fx.AppDs, TenantB, Roles.HrAdmin))
            {
                await using var cmd = new NpgsqlCommand("UPDATE documents SET superseded_at = now() WHERE id = @id", raw.Conn, raw.Tx);
                cmd.Parameters.AddWithValue("id", doc);
                Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
            }

            // hr-admin of the tenant: allowed (rolled back, so the scenario stays intact).
            await using (var raw = await RawTx.BeginAsync(fx.AppDs, TenantA, Roles.HrAdmin))
            {
                await using var cmd = new NpgsqlCommand("UPDATE documents SET superseded_at = now() WHERE id = @id", raw.Conn, raw.Tx);
                cmd.Parameters.AddWithValue("id", doc);
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            }

            await using var check = await fx.OwnerDs.OpenConnectionAsync();
            await using var q = new NpgsqlCommand("SELECT superseded_at IS NULL FROM documents WHERE id = @id", check);
            q.Parameters.AddWithValue("id", doc);
            Assert.True((bool)(await q.ExecuteScalarAsync())!, "no failed or rolled-back UPDATE may have marked the document");
        }
        finally { await CleanAsync(); }
    }

    [DbFact]
    public async Task Ingest_path_writes_and_clears_the_supersession_mark()
    {
        await SeedAsync();
        try
        {
            var id = DocId("ingested");
            var chunk = new ChunkRecord(ChunkId("ingested"), id, TenantA, 0, "ingested edition", AtDistance(0.2, 100), 1);

            async Task<bool> IsSupersededAsync()
            {
                await using var c = await fx.OwnerDs.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand("SELECT superseded_at IS NOT NULL FROM documents WHERE id = @id", c);
                cmd.Parameters.AddWithValue("id", id);
                return (bool)(await cmd.ExecuteScalarAsync())!;
            }

            var store = new NpgsqlChunkStore(fx.AppDs);
            foreach (var superseded in new[] { true, false })
            {
                await using var session = await store.BeginAsync(HrA);
                await session.StoreDocumentAsync(
                    new DocumentRecord(id, TenantA, "Ingested policy", 1, 1, superseded), [chunk]);
                await session.CommitAsync();
                Assert.Equal(superseded, await IsSupersededAsync());
            }
        }
        finally { await CleanAsync(); }
    }
}
