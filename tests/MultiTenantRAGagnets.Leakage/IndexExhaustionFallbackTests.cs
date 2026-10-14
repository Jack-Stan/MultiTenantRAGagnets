using Npgsql;
using Pgvector;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// Regression for the intermittent opt-in Ollama quality failure (CI: "returned == min(k, visible)", expected 5, got 0).
///
/// Root cause (measured in CI, see the commit message): a pgvector HNSW graph can leave rows UNREACHABLE (about 5% of a
/// 2665-row table, including every row of one tenant). <c>hnsw.iterative_scan</c> keeps scanning, but only over
/// reachable nodes, so the index path returns nothing for that tenant even though the rows are visible and
/// <c>max_scan_tuples</c> was never hit. The plan is "Index Scan using chunks_embedding_hnsw ... rows=0 ... Rows Removed
/// by Filter: ~2500" and a rerun on the same index returns 0 again; an exact scan returns 5.
///
/// A graph with unreachable nodes cannot be built deterministically, so this test exhausts the index path the other
/// way: <c>hnsw.max_scan_tuples = 20</c> stops the iterative scan after 20 index tuples. Same observable failure (index
/// path returns fewer than k although the caller can see more), same remedy (the store falls back to an exact scan).
/// The control proves the index path alone under-returns on this connection, so the assertion is not vacuous.
/// </summary>
[Collection(LeakageCollection.Name)]
public class IndexExhaustionFallbackTests(LeakageFixture fx)
{
    private const string StarvedIndexOptions = LeakageFixture.ForcedHnswOptions + " -c hnsw.max_scan_tuples=20";

    [DbFact]
    public async Task Store_returns_k_when_the_index_path_is_exhausted_before_k_visible_rows_are_found()
    {
        const int k = 5;
        var victim = new CallerContext(
            HostileScenario.TenantId(HostileScenario.VictimSlug),
            Ids.User(HostileScenario.VictimSlug, Roles.Employee),
            Roles.Employee);

        var csb = LeakageFixture.WithOptions(fx.AppConnectionString, StarvedIndexOptions);
        await using var starved = NpgsqlDataSourceFactory.Create(csb);

        // Control: the bare index path (RLS-only SQL has no WHERE, so the planner has no alternative to the HNSW index; same settings as the store) under-returns.
        await using (var raw = await RawTx.BeginAsync(starved, victim.TenantId, victim.Role))
        {
            await raw.ExecAsync("SET LOCAL hnsw.iterative_scan = 'strict_order'");
            await using var cmd = new NpgsqlCommand(RetrievalSql.RlsOnlySearch, raw.Conn, raw.Tx);
            cmd.Parameters.AddWithValue("q", new Vector(HostileScenario.QueryPoint));
            cmd.Parameters.AddWithValue("k", k);
            var n = 0;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) n++;
            await r.CloseAsync();
            var gucs = await raw.ScalarStringAsync(
                "SELECT current_setting('hnsw.max_scan_tuples',true) || '/' || current_setting('hnsw.ef_search',true) || '/' || current_setting('hnsw.iterative_scan',true)");
            Assert.True(n < k,
                $"CONTROL FAILED: the starved index path returned {n}/{k}; the scenario no longer exercises index exhaustion. max_scan_tuples/ef_search/iterative_scan = {gucs}");
        }

        var store = new NpgsqlChunkStore(starved);
        foreach (var mode in new[] { RetrievalFilterMode.AppAndRls, RetrievalFilterMode.RlsOnly })
        {
            await using var session = await store.BeginAsync(victim);
            var rows = await session.SearchAsync(HostileScenario.QueryPoint, k, mode);
            Assert.True(rows.Count == k,
                $"UNDER-RETURN: index path exhausted and no exact fallback. mode={mode} k={k} returned={rows.Count}.");
            Assert.All(rows, c => Assert.Equal(victim.TenantId, c.TenantId));
            for (var i = 1; i < rows.Count; i++)
                Assert.True(rows[i - 1].Distance <= rows[i].Distance, "fallback result is not ordered by distance");
        }
    }
}
