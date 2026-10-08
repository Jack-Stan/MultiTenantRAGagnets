using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// TESTING.md section 1a and 1b: 200 adversarial queries, as app_user, against real Postgres.
/// UNVERIFIED until CI runs them: no database was available when these were written.
/// "Natural" = the query text is embedded as-is. "Bait on query point" = the query vector is placed exactly on a
/// chunk the caller must not see (distance 0), so similarity search genuinely wants to return it.
/// </summary>
[Collection(LeakageCollection.Name)]
public class LeakageRunTests(LeakageFixture fx)
{
    private const int K = LeakageFixture.K;

    [DbFact]
    public async Task A_app_layer_natural_queries_leak_nothing()
    {
        var p = fx.NewPipeline(fx.AppDs);
        var report = await p.Runner.RunAsync("A app-layer, natural", RunMode.AppLayer, K, baitOnQueryPoint: false);
        fx.Results.RunA_AppLayer_Natural = RunSummary.From(report);
        report.AssertClean();
    }

    [DbFact]
    public async Task A_app_layer_bait_on_query_point_leaks_nothing()
    {
        var p = fx.NewPipeline(fx.AppDs);
        var report = await p.Runner.RunAsync("A app-layer, bait on query point", RunMode.AppLayer, K, baitOnQueryPoint: true);
        fx.Results.RunA_AppLayer_BaitOnQueryPoint = RunSummary.From(report);
        report.AssertClean();
    }

    [DbFact]
    public async Task B_rls_only_natural_queries_leak_nothing()
    {
        var p = fx.NewRlsOnlyPipeline(fx.AppDs);
        var report = await p.Runner.RunAsync("B RLS-only, natural", RunMode.RlsOnly, K, baitOnQueryPoint: false);
        fx.Results.RunB_RlsOnly_Natural = RunSummary.From(report);
        report.AssertClean();
    }

    [DbFact]
    public async Task B_rls_only_bait_on_query_point_leaks_nothing()
    {
        var p = fx.NewRlsOnlyPipeline(fx.AppDs);
        var report = await p.Runner.RunAsync("B RLS-only, bait on query point", RunMode.RlsOnly, K, baitOnQueryPoint: true);
        fx.Results.RunB_RlsOnly_BaitOnQueryPoint = RunSummary.From(report);
        report.AssertClean();
    }

    [DbFact]
    public async Task B_rls_only_also_holds_on_the_forced_hnsw_index_path()
    {
        // The planner on a small table may seq-scan; this run forces the HNSW index so RLS-only is tested on it too.
        var p = fx.NewRlsOnlyPipeline(fx.AppForcedHnswDs);
        var natural = await p.Runner.RunAsync("B RLS-only, forced HNSW, natural", RunMode.RlsOnly, K, baitOnQueryPoint: false);
        natural.AssertClean();
        var bait = await p.Runner.RunAsync("B RLS-only, forced HNSW, bait", RunMode.RlsOnly, K, baitOnQueryPoint: true);
        bait.AssertClean();
    }

    [DbFact]
    public async Task Prompt_is_built_from_permitted_chunks_only_even_for_injection_documents()
    {
        // Same runner, but spelled out: the recording chat provider is the ground truth for what reached the "LLM".
        var p = fx.NewPipeline(fx.AppDs);
        var checkedPrompts = 0;
        foreach (var q in fx.Corpus.AdversarialQueries.Take(40))
        {
            var visible = fx.Oracle.VisibleChunks(q.TenantSlug, q.Role);
            p.Chat.Reset();
            p.Embeddings.Override = FakeEmbeddingProvider.Embed(fx.Oracle.ChunkById[q.BaitChunkId].Text);
            try
            {
                await p.Service.QueryAsync(fx.Oracle.CallerFor(q.TenantSlug, q.Role), q.Text, K);
            }
            finally
            {
                p.Embeddings.Override = null;
            }

            Assert.NotEmpty(p.Chat.Requests);
            foreach (var chunk in p.Chat.Requests.SelectMany(r => r.Context))
            {
                Assert.Contains(Guid.Parse(chunk.Id), visible);
                checkedPrompts++;
            }
        }
        Assert.True(checkedPrompts > 0, "no prompt chunks were inspected: the assertion would be vacuous");
    }
}
