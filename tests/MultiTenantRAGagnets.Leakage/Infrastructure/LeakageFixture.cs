using Npgsql;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;

namespace MultiTenantRAGagnets.Leakage.Infrastructure;

/// <summary>Everything that touches the database lives in this one collection: tests run one at a time, because the negative controls break and restore RLS.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LeakageCollection : ICollectionFixture<LeakageFixture>
{
    public const string Name = "leakage-db";
}

/// <summary>A wired RetrievalService over one data source, with the recording chat and bait-capable embedder.</summary>
public sealed class Pipeline(
    NpgsqlChunkStore store,
    RetrievalService service,
    RecordingChatProvider chat,
    BaitEmbeddingProvider embeddings,
    LeakageRunner runner)
{
    public NpgsqlChunkStore Store { get; } = store;
    public RetrievalService Service { get; } = service;
    public RecordingChatProvider Chat { get; } = chat;
    public BaitEmbeddingProvider Embeddings { get; } = embeddings;
    public LeakageRunner Runner { get; } = runner;
}

public sealed class LeakageFixture : IAsyncLifetime
{
    public const int K = 5;

    /// <summary>Planner settings that make the HNSW index the only way to satisfy ORDER BY embedding &lt;=&gt; q LIMIT k.</summary>
    public const string ForcedHnswOptions = "-c enable_seqscan=off -c enable_bitmapscan=off -c enable_sort=off";

    /// <summary>Index scans off: exact (sequential) nearest neighbour, for the labelled eval fallback TESTING.md section 3 allows.</summary>
    public const string ExactOptions = "-c enable_indexscan=off -c enable_bitmapscan=off";

    public bool Available { get; private set; }
    public SyntheticCorpus Corpus { get; private set; } = null!;
    public CorpusOracle Oracle { get; private set; } = null!;
    public LeakageResults Results { get; } = new();

    public NpgsqlDataSource OwnerDs { get; private set; } = null!;
    /// <summary>app_user, planner left to its own devices.</summary>
    public NpgsqlDataSource AppDs { get; private set; } = null!;
    public NpgsqlDataSource AppForcedHnswDs { get; private set; } = null!;
    public NpgsqlDataSource AppExactDs { get; private set; } = null!;

    public string AppConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!DbEnv.Configured)
        {
            if (DbEnv.HollowRunError is { } hollow) throw new InvalidOperationException(hollow);
            return; // local run without a database: every [DbFact] is skipped.
        }

        Corpus = CorpusGenerator.Generate();
        Oracle = new CorpusOracle(Corpus);

        var ownerCs = DbEnv.ToNpgsqlConnectionString(DbEnv.OwnerUrl!);
        AppConnectionString = DbEnv.ToNpgsqlConnectionString(DbEnv.AppUrl!);

        var ob = new NpgsqlDataSourceBuilder(ownerCs);
        ob.UseVector();
        OwnerDs = ob.Build();

        AppDs = NpgsqlDataSourceFactory.Create(AppConnectionString);
        AppForcedHnswDs = NpgsqlDataSourceFactory.Create(WithOptions(AppConnectionString, ForcedHnswOptions));
        AppExactDs = NpgsqlDataSourceFactory.Create(WithOptions(AppConnectionString, ExactOptions));

        await Seeder.SeedAsync(OwnerDs, Corpus);
        await Seeder.VerifyAgainstManifestAsync(OwnerDs, Corpus);

        await FillEnvironmentAsync();
        Available = true;
    }

    public static string WithOptions(string connectionString, string options, int? maxPool = null)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString) { Options = options };
        if (maxPool is { } m) csb.MaxPoolSize = m;
        return csb.ConnectionString;
    }

    public Pipeline NewPipeline(NpgsqlDataSource appDataSource)
    {
        var store = new NpgsqlChunkStore(appDataSource);
        var chat = new RecordingChatProvider();
        var embeddings = new BaitEmbeddingProvider();
        var service = ServiceFactory.Create(store, embeddings, chat);
        var runner = new LeakageRunner(Oracle, chat, embeddings, (caller, question, k) => service.QueryAsync(caller, question, k));
        return new Pipeline(store, service, chat, embeddings, runner);
    }

    /// <summary>Same as <see cref="NewPipeline"/> but the runner goes through the internal test seam with NO app-layer filter.</summary>
    public Pipeline NewRlsOnlyPipeline(NpgsqlDataSource appDataSource)
    {
        var store = new NpgsqlChunkStore(appDataSource);
        var chat = new RecordingChatProvider();
        var embeddings = new BaitEmbeddingProvider();
        var service = ServiceFactory.Create(store, embeddings, chat);
        var runner = new LeakageRunner(Oracle, chat, embeddings,
            (caller, question, k) => service.QueryWithoutAppFilterForTestingAsync(caller, question, k));
        return new Pipeline(store, service, chat, embeddings, runner);
    }

    public async Task<long> CountChunksAsync()
    {
        await using var c = await OwnerDs.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chunks", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task FillEnvironmentAsync()
    {
        var env = EnvironmentInfo.Capture();
        await using (var c = await OwnerDs.OpenConnectionAsync())
        {
            await using (var cmd = new NpgsqlCommand("SELECT version()", c))
                env.Postgres = (string)(await cmd.ExecuteScalarAsync())!;
            await using (var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", c))
                env.Pgvector = (await cmd.ExecuteScalarAsync()) as string ?? "not installed";
        }
        var host = new NpgsqlConnectionStringBuilder(AppConnectionString).Host ?? "";
        env.DatabaseLocation = host is "localhost" or "127.0.0.1" or "::1"
            ? "on the same machine as the test runner (CI service container or local)"
            : "on a separate host";

        IEmbeddingProvider embedder = new FakeEmbeddingProvider();
        Results.GeneratedAtUtc = DateTimeOffset.UtcNow;
        Results.GitSha = ResultsWriter.GitSha() ?? "unknown";
        Results.Environment = env;
        Results.Corpus = new CorpusInfo
        {
            Seed = Corpus.Seed,
            Tenants = Corpus.Tenants.Count,
            Documents = Corpus.Documents.Count,
            BaseCorpusChunks = Corpus.Chunks.Count,
            ExtraHostileChunks = HostileScenario.TotalRows,
            AdversarialQueries = Corpus.AdversarialQueries.Count,
            LabelledQuestions = Corpus.Questions.Count,
            ChunkingParameters = Corpus.ChunkingParameters,
            EmbeddingProviderKind = embedder is FakeEmbeddingProvider ? "Fake" : "Ollama",
            EmbeddingModel = embedder.ModelName,
            EmbeddingDimension = embedder.Dimension,
        };
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;

        try
        {
            Results.Corpus.TotalChunksInTableAtMeasurement = await CountChunksAsync();
            var missing = Results.MissingSections;
            void Need(object? section, string name) { if (section is null) missing.Add(name); }
            Need(Results.RunA_AppLayer_Natural, "runA natural");
            Need(Results.RunA_AppLayer_BaitOnQueryPoint, "runA bait-on-query-point");
            Need(Results.RunB_RlsOnly_Natural, "runB natural");
            Need(Results.RunB_RlsOnly_BaitOnQueryPoint, "runB bait-on-query-point");
            if (Results.NegativeControls.Select(n => n.Broken).Distinct().Count() < 2) missing.Add("negative controls (expected 2 distinct)");
            Need(Results.UnderReturn, "returned==k");
            if (Results.Quality.Count == 0) missing.Add("recall/MRR");
            if (Results.Latency.Count == 0) missing.Add("latency");

            var (json, md) = ResultsWriter.Write(Results, ResultsWriter.FindResultsDirectory());
            Console.WriteLine($"Leakage results written: {json} and {md}" +
                              (missing.Count > 0 ? $"  (INCOMPLETE: {string.Join(", ", missing)})" : ""));
        }
        finally
        {
            await OwnerDs.DisposeAsync();
            await AppDs.DisposeAsync();
            await AppForcedHnswDs.DisposeAsync();
            await AppExactDs.DisposeAsync();
        }
    }
}
