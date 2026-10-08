using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// No database needed. These prove the HARNESS LOGIC can tell a clean system from a leaky one, using an
/// in-memory model of the store over the real generated corpus. They say nothing about Postgres or RLS;
/// that is what the [DbFact] tests are for.
/// </summary>
public class HarnessSelfTests
{
    private const int K = 5;
    private static readonly SyntheticCorpus Corpus = CorpusGenerator.Generate();
    private static readonly CorpusOracle Oracle = new(Corpus);

    private static LeakageRunner Runner(ModelStore store, RunMode mode, out RecordingChatProvider chat, out BaitEmbeddingProvider emb)
    {
        chat = new RecordingChatProvider();
        emb = new BaitEmbeddingProvider();
        var service = ServiceFactory.Create(store, emb, chat);
        return new LeakageRunner(Oracle, chat, emb, (caller, q, k) =>
            mode == RunMode.AppLayer
                ? service.QueryAsync(caller, q, k)
                : service.QueryWithoutAppFilterForTestingAsync(caller, q, k));
    }

    [Fact]
    public void Corpus_has_200_adversarial_queries_and_every_bait_is_forbidden_for_its_caller()
    {
        Assert.Equal(200, Corpus.AdversarialQueries.Count);
        foreach (var q in Corpus.AdversarialQueries)
        {
            Assert.DoesNotContain(q.BaitChunkId, Oracle.VisibleChunks(q.TenantSlug, q.Role));
        }
    }

    [Theory]
    [InlineData(RunMode.AppLayer, false)]
    [InlineData(RunMode.AppLayer, true)]
    [InlineData(RunMode.RlsOnly, false)]
    [InlineData(RunMode.RlsOnly, true)]
    public async Task Correct_model_reports_zero_leaks_and_full_result_sets(RunMode mode, bool bait)
    {
        var runner = Runner(new ModelStore(Corpus), mode, out _, out _);
        var report = await runner.RunAsync("model-correct", mode, K, bait);

        report.AssertClean();
        Assert.Equal(0, report.TotalLeaks);
        Assert.Equal(200, report.Queries);
        Assert.Equal(200, report.NonEmptyQueries);
    }

    [Fact]
    public async Task Model_without_rls_leaks_when_the_app_filter_is_removed_and_the_detector_sees_it()
    {
        var runner = Runner(new ModelStore(Corpus) { RlsActive = false }, RunMode.RlsOnly, out _, out _);
        var report = await runner.RunAsync("model-no-rls", RunMode.RlsOnly, K, baitOnQueryPoint: true);

        Assert.True(report.TotalLeaks > 0, report.Summary);
        Assert.True(report.CrossTenantLeakedChunks > 0, report.Summary);
        Assert.True(report.CrossRoleLeakedChunks > 0, report.Summary);
        Assert.True(report.BaitRetrievedQueries >= 190, report.Summary); // the bait sits at distance 0
        Assert.True(report.LeakedSentChunks > 0, "leaked chunks must also be seen arriving at the chat provider: " + report.Summary);
        Assert.True(report.SecretMarkerHits > 0, "the content scan must catch forbidden secret markers in the prompt: " + report.Summary);
        Assert.ThrowsAny<Exception>(report.AssertClean);
        NegativeControlVerdict.RequireLeaks(report, "model without RLS"); // must not throw
    }

    [Fact]
    public async Task Negative_control_verdict_throws_when_a_run_reports_zero_leaks()
    {
        var runner = Runner(new ModelStore(Corpus), RunMode.RlsOnly, out _, out _);
        var clean = await runner.RunAsync("model-correct", RunMode.RlsOnly, K, baitOnQueryPoint: true);

        var ex = Assert.Throws<HollowHarnessException>(() => NegativeControlVerdict.RequireLeaks(clean, "policy dropped"));
        Assert.Contains("HOLLOW", ex.Message);
    }

    [Fact]
    public async Task App_layer_run_notices_a_broken_app_filter_even_when_the_service_backstop_fires()
    {
        // RLS gone AND app filter broken: the service's own tenant backstop aborts the query before the LLM.
        // The run must still fail (errors), not quietly pass because nothing reached the prompt.
        var runner = Runner(new ModelStore(Corpus) { RlsActive = false, AppFilterBroken = true }, RunMode.AppLayer, out _, out _);
        var report = await runner.RunAsync("model-broken-app-filter", RunMode.AppLayer, K, baitOnQueryPoint: true);

        Assert.True(report.Errors > 0 || report.TotalLeaks > 0, report.Summary);
        Assert.ThrowsAny<Exception>(report.AssertClean);
    }

    [Fact]
    public async Task Under_returning_store_fails_the_run_even_though_it_leaks_nothing()
    {
        var runner = Runner(new ModelStore(Corpus) { CapReturnedRows = 2 }, RunMode.RlsOnly, out _, out _);
        var report = await runner.RunAsync("model-under-return", RunMode.RlsOnly, K, baitOnQueryPoint: false);

        Assert.Equal(0, report.TotalLeaks);
        Assert.True(report.WrongCount > 0, report.Summary);
        Assert.ThrowsAny<Exception>(report.AssertClean);
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        var xs = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(95, Stats.Percentile(xs, 95));
        Assert.Equal(50, Stats.Percentile(xs, 50));
        Assert.Equal(100, Stats.Percentile(xs, 100));
        Assert.Equal(1, Stats.Percentile(xs, 0));
        Assert.Equal(7, Stats.Percentile([7.0], 95));
    }

    [Fact]
    public void Recall_and_mrr_match_hand_worked_examples()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var x = Guid.NewGuid();
        var ranked = new[] { x, b, a, c };

        Assert.Equal(1.0, Stats.RecallAtK(ranked, [a, b], 3));
        Assert.Equal(0.5, Stats.RecallAtK(ranked, [a, b], 2));
        Assert.Equal(0.0, Stats.RecallAtK(ranked, [a], 2));
        Assert.Equal(0.5, Stats.ReciprocalRank(ranked, [a, b], 3)); // first hit is b at rank 2
        Assert.Equal(0.0, Stats.ReciprocalRank(ranked, [c], 3));    // c is rank 4, outside k
        Assert.Equal(1.0 / 3, Stats.ReciprocalRank(ranked, [a], 3), 10);
    }

    [Fact]
    public void Results_markdown_flags_an_incomplete_run_and_states_the_fake_embedding_caveat()
    {
        var results = new LeakageResults { MissingSections = ["latency"] };
        results.Corpus.EmbeddingProviderKind = "Fake";
        results.QualityLabel = "HARNESS SMOKE TEST ONLY";
        var md = ResultsWriter.Markdown(results);

        Assert.Contains("INCOMPLETE RUN", md);
        Assert.Contains("HARNESS SMOKE TEST", md);
        Assert.Contains("Embedding provider kind: **Fake**", md);
    }
}

public class DbEnvTests
{
    [Fact]
    public void Url_form_is_converted_to_an_npgsql_connection_string()
    {
        var cs = DbEnv.ToNpgsqlConnectionString("postgres://app_user:p%40ss@db.example:6543/mtra");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("db.example", b.Host);
        Assert.Equal(6543, b.Port);
        Assert.Equal("mtra", b.Database);
        Assert.Equal("app_user", b.Username);
        Assert.Equal("p@ss", b.Password);
    }

    [Fact]
    public void Default_port_and_key_value_form_are_handled()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(DbEnv.ToNpgsqlConnectionString("postgres://u:p@localhost/db1"));
        Assert.Equal(5432, b.Port);
        Assert.Equal("Host=h;Database=d", DbEnv.ToNpgsqlConnectionString("Host=h;Database=d"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ci_flag_parsing(string? value, bool expected) => Assert.Equal(expected, DbEnv.IsCiValue(value));

    [Fact]
    public void A_ci_run_without_database_variables_fails_instead_of_going_green()
    {
        // This is the guard against a hollow green: locally it passes (skip is allowed), in CI it fails
        // unless OWNER_URL and APP_URL are really there.
        if (DbEnv.HollowRunError is { } error) Assert.Fail(error);
    }
}
