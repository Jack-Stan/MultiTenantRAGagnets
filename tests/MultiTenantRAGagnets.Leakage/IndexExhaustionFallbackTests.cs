using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using MultiTenantRAGagnets.Core.Providers;
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
/// A graph with unreachable nodes cannot be built deterministically, so this test starves the index path the other
/// way: iterative scan OFF (via the store's internal seam) on the hostile scenario. Same observable failure (index
/// path returns fewer than k although the caller can see more), same remedy (the store falls back to an exact scan).
/// The control proves the index path alone under-returns on this connection, so the assertion is not vacuous.
/// </summary>
[Collection(LeakageCollection.Name)]
public class IndexExhaustionFallbackTests(LeakageFixture fx)
{

    [DbFact]
    public async Task Store_returns_k_when_the_index_path_is_exhausted_before_k_visible_rows_are_found()
    {
        const int k = 5;
        var victim = new CallerContext(
            HostileScenario.TenantId(HostileScenario.VictimSlug),
            Ids.User(HostileScenario.VictimSlug, Roles.Employee),
            Roles.Employee);

        // Forced HNSW, and iterative scan OFF through the store's internal seam: the index window (ef_search 40) is
        // all rows the caller may not see, so the bare index path returns 0. hnsw.max_scan_tuples is NOT usable for
        // this: pgvector drains its "discarded" candidates after the cap and still returns rows (measured in CI).
        var starved = fx.AppForcedHnswDs;

        // Control: the bare index path (RLS-only SQL has no WHERE, so the planner has no alternative to the HNSW index; same settings as the store) under-returns.
        await using (var raw = await RawTx.BeginAsync(starved, victim.TenantId, victim.Role))
        {
            await raw.ExecAsync("SET LOCAL hnsw.iterative_scan = 'off'");
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

        var store = new NpgsqlChunkStore(starved, "off");
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

    [DbFact]
    public async Task Fallback_increments_the_counter_and_logs_counts_only()
    {
        const int k = 5;
        var victim = new CallerContext(
            HostileScenario.TenantId(HostileScenario.VictimSlug),
            Ids.User(HostileScenario.VictimSlug, Roles.Employee),
            Roles.Employee);
        var log = new CapturingLogger();
        using var meter = new FallbackMeterListener();
        var store = new NpgsqlChunkStore(fx.AppForcedHnswDs, "off", log);

        await using (var session = await store.BeginAsync(victim))
        {
            var rows = await session.SearchAsync(HostileScenario.QueryPoint, k, RetrievalFilterMode.AppAndRls);
            Assert.Equal(k, rows.Count);
        }

        Assert.Equal(1, store.IndexFallbackCount);
        Assert.Equal(1, meter.Total);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("RequestedK=5", entry.Message);
        Assert.Contains("IndexRows=", entry.Message);
        Assert.Contains($"ExactRows={k}", entry.Message);
        // Structured counts only; the message template has no placeholder for query or chunk text.
    }

    [DbFact]
    public async Task Normal_index_hit_does_not_increment_the_counter()
    {
        const int k = 5;
        // A corpus caller with at least k visible chunks, on the data source whose index path fills k by itself.
        var q = fx.Corpus.Questions.First(x => fx.Oracle.VisibleChunks(x.TenantSlug, x.Role).Count >= k);
        var caller = fx.Oracle.CallerFor(q.TenantSlug, q.Role);
        var embedding = await new FakeEmbeddingProvider().EmbedAsync(q.Text);

        var log = new CapturingLogger();
        using var meter = new FallbackMeterListener();
        var store = new NpgsqlChunkStore(fx.AppExactDs, "strict_order", log);

        await using (var session = await store.BeginAsync(caller))
        {
            var rows = await session.SearchAsync(embedding, k, RetrievalFilterMode.AppAndRls);
            Assert.Equal(k, rows.Count);
        }

        Assert.Equal(0, store.IndexFallbackCount);
        Assert.Equal(0, meter.Total);
        Assert.Empty(log.Entries);
    }

    /// <summary>Reads the process-wide <c>retrieval.index_fallback</c> instrument, so the test proves the metric and not only the field.</summary>
    private sealed class FallbackMeterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;
        public long Total => Interlocked.Read(ref _total);

        public FallbackMeterListener()
        {
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == RetrievalMetrics.MeterName && inst.Name == RetrievalMetrics.IndexFallbackInstrument)
                    l.EnableMeasurementEvents(inst);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
