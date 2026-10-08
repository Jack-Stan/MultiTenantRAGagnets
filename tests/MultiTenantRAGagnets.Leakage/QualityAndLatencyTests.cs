using System.Diagnostics;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// TESTING.md sections 3 and 4. recall@k / MRR over the labelled set, and p95 latency, with corpus size, k, chunking,
/// embedding kind and hardware stated in the results file.
///
/// With FakeEmbeddingProvider the vectors are hashes of the text: a question and the chunk that answers it share NO
/// geometry. recall@k and MRR here are therefore a HARNESS SMOKE TEST (labels, manifest, ranking and metric code
/// all line up) and NOT a retrieval quality result. They are labelled that way in the output and must never be
/// quoted as quality.
/// </summary>
[Collection(LeakageCollection.Name)]
public class QualityAndLatencyTests(LeakageFixture fx)
{
    private const int Rounds = 4; // round 1 is warm-up and is discarded

    private string QualityLabel =>
        "HARNESS SMOKE TEST ONLY. Embedding provider kind: Fake (hash of text, no semantics). These recall@k and MRR values " +
        "check that the labelled set, manifest and metric code are wired correctly; they are NOT a retrieval quality result " +
        "and say nothing about how well real embeddings (Ollama nomic-embed-text) would retrieve.";

    [DbFact]
    public async Task Recall_at_k_and_mrr_over_the_labelled_set_are_computed_and_leak_nothing()
    {
        fx.Results.QualityLabel = QualityLabel;

        var variants = new (string Name, Npgsql.NpgsqlDataSource Ds)[]
        {
            ("exact search (index scans off)", fx.AppExactDs),
            ("HNSW forced, iterative scan on", fx.AppForcedHnswDs),
            ("planner default, iterative scan on", fx.AppDs),
        };

        foreach (var (name, ds) in variants)
        {
            var p = fx.NewPipeline(ds);
            foreach (var k in new[] { 5, 10 })
            {
                var recalls = new List<double>();
                var rrs = new List<double>();
                foreach (var q in fx.Corpus.Questions)
                {
                    var visible = fx.Oracle.VisibleChunks(q.TenantSlug, q.Role);
                    var caller = fx.Oracle.CallerFor(q.TenantSlug, q.Role);
                    var result = await p.Service.QueryAsync(caller, q.Text, k);

                    // Same security check on the labelled traffic: nothing outside the manifest, and full result sets.
                    Assert.All(result.RetrievedChunkIds, id => Assert.Contains(id, visible));
                    Assert.Equal(Math.Min(k, visible.Count), result.RetrievedChunkIds.Count);
                    Assert.All(q.ExpectedChunkIds, id => Assert.Contains(id, visible)); // labels never point at forbidden chunks

                    recalls.Add(Stats.RecallAtK(result.RetrievedChunkIds, q.ExpectedChunkIds, k));
                    rrs.Add(Stats.ReciprocalRank(result.RetrievedChunkIds, q.ExpectedChunkIds, k));
                }

                Assert.Equal(fx.Corpus.Questions.Count, recalls.Count);
                Assert.InRange(recalls.Average(), 0.0, 1.0);
                Assert.InRange(rrs.Average(), 0.0, 1.0);
                fx.Results.Quality.Add(new QualityRow
                {
                    SearchPath = name,
                    K = k,
                    Questions = recalls.Count,
                    RecallAtK = recalls.Average(),
                    Mrr = rrs.Average(),
                });
            }
        }
    }

    [DbFact]
    public async Task P95_latency_for_retrieval_and_end_to_end_with_the_fake_llm()
    {
        const int k = LeakageFixture.K;
        fx.Results.LatencyNote =
            "Retrieval = open app_user connection + BEGIN + SET LOCAL context + vector search + rollback, query embedding precomputed. " +
            "End-to-end = RetrievalService.QueryAsync: fake embedding + retrieval + FakeChatProvider (deterministic, ~0 ms, so NOT a real LLM latency) + audit INSERT + COMMIT. " +
            $"Samples: the {fx.Corpus.Questions.Count} labelled questions x {Rounds - 1} measured rounds after 1 discarded warm-up round, sequential, single client. " +
            "p95 is nearest-rank. The database ran as stated under Setup; numbers are not comparable across different hardware or corpus sizes.";

        foreach (var (name, ds) in new[] { ("planner default", fx.AppDs), ("HNSW forced", fx.AppForcedHnswDs) })
        {
            var pipeline = fx.NewPipeline(ds);
            var retrieval = new List<double>();
            var endToEnd = new List<double>();

            for (var round = 0; round < Rounds; round++)
            {
                foreach (var q in fx.Corpus.Questions)
                {
                    var caller = fx.Oracle.CallerFor(q.TenantSlug, q.Role);
                    var vector = FakeEmbeddingProvider.Embed(q.Text);

                    var t0 = Stopwatch.GetTimestamp();
                    await using (var session = await pipeline.Store.BeginAsync(caller))
                    {
                        var rows = await session.SearchAsync(vector, k, RetrievalFilterMode.AppAndRls);
                        Assert.NotEmpty(rows);
                    }
                    var retrievalMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                    pipeline.Chat.Reset();
                    var t1 = Stopwatch.GetTimestamp();
                    var result = await pipeline.Service.QueryAsync(caller, q.Text, k);
                    var e2eMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                    Assert.NotEmpty(result.RetrievedChunkIds);

                    if (round == 0) continue; // warm-up
                    retrieval.Add(retrievalMs);
                    endToEnd.Add(e2eMs);
                }
            }

            fx.Results.Latency.Add(Row("retrieval only", name, k, retrieval));
            fx.Results.Latency.Add(Row("end-to-end (fake LLM)", name, k, endToEnd));
        }
    }

    private static LatencyRow Row(string measure, string path, int k, List<double> ms) => new()
    {
        Measure = measure,
        PlannerPath = path,
        K = k,
        Samples = ms.Count,
        P50Ms = Stats.Percentile(ms, 50),
        P95Ms = Stats.Percentile(ms, 95),
        P99Ms = Stats.Percentile(ms, 99),
        MeanMs = ms.Average(),
    };
}
