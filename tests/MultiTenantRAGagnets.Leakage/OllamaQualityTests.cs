using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// Opt-in retrieval QUALITY run with real Ollama embeddings (nomic-embed-text, 768 dims) for both ingest and query.
///
///   EMBEDDINGS=ollama   turns it on (anything else, or unset: every test here is skipped and nothing touches the DB)
///   OLLAMA_URL          base URL of Ollama, default http://localhost:11434
///   OWNER_URL / APP_URL same as the leakage suite
///
/// It is a separate collection with its own fixture and its own results files (results/quality-results-ollama.*).
/// It never touches the leakage suite's assertions or leakage-results.*. Chat stays the fake LLM: only embeddings
/// are real here. The same security assertions as QualityAndLatencyTests are kept on the labelled traffic.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OllamaQualityCollection : ICollectionFixture<OllamaQualityFixture>
{
    public const string Name = "ollama-quality";
}

public static class OllamaEnv
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("EMBEDDINGS")?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase);

    public static string Url =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_URL"))
            ? "http://localhost:11434"
            : Environment.GetEnvironmentVariable("OLLAMA_URL")!.Trim();
}

/// <summary>Skips unless EMBEDDINGS=ollama. When it IS set, a missing database is a failure, never a skip.</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (!OllamaEnv.Enabled)
        {
            Skip = "SKIPPED: EMBEDDINGS=ollama is not set. This is the opt-in real-embeddings quality run (see docs/TESTING.md).";
        }
    }
}

/// <summary>Memoises by text so the same question is embedded once per run. Same model, same vectors; only fewer HTTP calls.</summary>
internal sealed class CachingEmbeddingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _cache = new();
    public string ModelName => inner.ModelName;
    public int Dimension => inner.Dimension;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(text, out var hit)) return hit;
        }
        var v = await inner.EmbedAsync(text, cancellationToken);
        lock (_cache) _cache[text] = v;
        return v;
    }
}

public sealed class OllamaQualityResults
{
    public string Schema { get; set; } = "mtra-quality-ollama-results/1";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string GitSha { get; set; } = "unknown";
    public string Label { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimension { get; set; }
    public string EmbeddingProvider { get; set; } = "Ollama";
    public string OllamaVersion { get; set; } = "unknown";
    public string OllamaHostNote { get; set; } = "";
    public string ChatProvider { get; set; } = "Fake (deterministic; chat is not part of retrieval quality)";
    public int CorpusSeed { get; set; }
    public int Tenants { get; set; }
    public int Documents { get; set; }
    public int BaseCorpusChunks { get; set; }
    public int DistinctChunkTextsEmbedded { get; set; }
    public long TotalChunksInTableAtMeasurement { get; set; }
    public string HostileRowsNote { get; set; } = "";
    public int LabelledQuestions { get; set; }
    public int DistinctQuestionTexts { get; set; }
    public string ChunkingParameters { get; set; } = "";
    public EnvironmentInfo Environment { get; set; } = new();
    public string Runner { get; set; } = "";
    public double IngestEmbedSeconds { get; set; }
    public List<QualityRow> Quality { get; set; } = [];
    public List<string> Caveats { get; set; } = [];
}

public sealed class OllamaQualityFixture : IAsyncLifetime
{
    public bool Available { get; private set; }
    public SyntheticCorpus Corpus { get; private set; } = null!;
    public CorpusOracle Oracle { get; private set; } = null!;
    public OllamaQualityResults Results { get; } = new();
    public IEmbeddingProvider Embeddings { get; private set; } = null!;

    public NpgsqlDataSource OwnerDs { get; private set; } = null!;
    public NpgsqlDataSource AppExactDs { get; private set; } = null!;
    public NpgsqlDataSource AppForcedHnswDs { get; private set; } = null!;
    public NpgsqlDataSource AppDs { get; private set; } = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        if (!OllamaEnv.Enabled) return;
        if (!DbEnv.Configured)
            throw new InvalidOperationException(
                $"EMBEDDINGS=ollama but {DbEnv.OwnerVar}/{DbEnv.AppVar} are missing: a quality run with no database proves nothing.");

        var options = new OllamaOptions { BaseUrl = OllamaEnv.Url };
        _http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        var provider = new OllamaProvider(_http, Options.Create(options));
        Embeddings = new CachingEmbeddingProvider(provider);

        Corpus = CorpusGenerator.Generate();
        Oracle = new CorpusOracle(Corpus);

        // Embed every distinct chunk text once, through the real provider. Fails loudly if Ollama is down
        // or returns the wrong dimension (OllamaProvider throws), never falls back to fake vectors.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var byText = new Dictionary<string, float[]>();
        foreach (var text in Corpus.Chunks.Select(c => c.Text).Distinct())
            byText[text] = await Embeddings.EmbedAsync(text);
        var vectors = Corpus.Chunks.ToDictionary(c => c.Id, c => byText[c.Text]);
        Results.IngestEmbedSeconds = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, 1);
        Results.DistinctChunkTextsEmbedded = byText.Count;

        var ownerCs = DbEnv.ToNpgsqlConnectionString(DbEnv.OwnerUrl!);
        var appCs = DbEnv.ToNpgsqlConnectionString(DbEnv.AppUrl!);
        var ob = new NpgsqlDataSourceBuilder(ownerCs);
        ob.UseVector();
        OwnerDs = ob.Build();
        AppDs = NpgsqlDataSourceFactory.Create(appCs);
        AppForcedHnswDs = NpgsqlDataSourceFactory.Create(LeakageFixture.WithOptions(appCs, LeakageFixture.ForcedHnswOptions));
        AppExactDs = NpgsqlDataSourceFactory.Create(LeakageFixture.WithOptions(appCs, LeakageFixture.ExactOptions));

        await Seeder.SeedAsync(OwnerDs, Corpus, vectors);
        await Seeder.VerifyAgainstManifestAsync(OwnerDs, Corpus);

        await FillEnvironmentAsync(appCs);
        Available = true;
    }

    private async Task FillEnvironmentAsync(string appCs)
    {
        var env = EnvironmentInfo.Capture();
        await using (var c = await OwnerDs.OpenConnectionAsync())
        {
            await using (var cmd = new NpgsqlCommand("SELECT version()", c))
                env.Postgres = (string)(await cmd.ExecuteScalarAsync())!;
            await using (var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", c))
                env.Pgvector = (await cmd.ExecuteScalarAsync()) as string ?? "not installed";
        }
        var host = new NpgsqlConnectionStringBuilder(appCs).Host ?? "";
        env.DatabaseLocation = host is "localhost" or "127.0.0.1" or "::1"
            ? "on the same machine as the test runner (CI service container or local)"
            : "on a separate host";

        string ollamaVersion = "unknown";
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("api/version"));
            ollamaVersion = doc.RootElement.GetProperty("version").GetString() ?? "unknown";
        }
        catch (Exception) { /* informational only */ }

        var runnerName = Environment.GetEnvironmentVariable("RUNNER_NAME");
        var imageOs = Environment.GetEnvironmentVariable("ImageOS");
        var runner = string.IsNullOrWhiteSpace(runnerName)
            ? "local machine (not a GitHub runner)"
            : $"GitHub Actions runner '{runnerName}' (RUNNER_OS={Environment.GetEnvironmentVariable("RUNNER_OS")}, ImageOS={imageOs})";

        var r = Results;
        r.GeneratedAtUtc = DateTimeOffset.UtcNow;
        r.GitSha = ResultsWriter.GitSha() ?? "unknown";
        r.Label =
            "RETRIEVAL QUALITY with REAL embeddings (Ollama) for ingest and query. Chat is the fake LLM. " +
            "Corpus is the repo's synthetic, template-generated corpus, not production data: read these numbers as " +
            "'this model on this corpus', not as a general claim.";
        r.EmbeddingModel = Embeddings.ModelName;
        r.EmbeddingDimension = Embeddings.Dimension;
        r.OllamaVersion = ollamaVersion;
        r.OllamaHostNote = $"Ollama at {new Uri(OllamaEnv.Url).Host}, CPU inference unless the host has a GPU; hardware below is the test runner's.";
        r.CorpusSeed = Corpus.Seed;
        r.Tenants = Corpus.Tenants.Count;
        r.Documents = Corpus.Documents.Count;
        r.BaseCorpusChunks = Corpus.Chunks.Count;
        r.HostileRowsNote =
            $"{HostileScenario.TotalRows} extra rows in 3 hostile tenants (fake hash vectors, never the target of a labelled question) " +
            "are also in the chunks table, as in the leakage suite; they are invisible to the labelled callers under RLS.";
        r.LabelledQuestions = Corpus.Questions.Count;
        r.DistinctQuestionTexts = Corpus.Questions.Select(q => q.Text).Distinct().Count();
        r.ChunkingParameters = Corpus.ChunkingParameters;
        r.Environment = env;
        r.Runner = runner;
        r.Caveats =
        [
            "Embedding input is the raw chunk / question text; OllamaProvider adds no nomic task prefixes (search_document:/search_query:), so this measures the repo's provider as shipped.",
            "The corpus is synthetic and template-generated: many questions share wording across tenants and roles, so labelled sets are far easier than real documents. Do not compare with public benchmarks.",
            "Questions are scored per (tenant, role) caller against only the chunks that caller may see; the oracle asserts labels never point at forbidden chunks.",
            "recall@k = |expected in top-k| / |expected|; MRR@k = 1/rank of the first expected chunk in top-k, else 0.",
            "Single run, no confidence intervals; embedding vectors are deterministic for a given model build, but model builds and CPU float differences can shift ranks slightly.",
        ];
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;
        try
        {
            Results.TotalChunksInTableAtMeasurement = await CountChunksAsync();
            var dir = ResultsWriter.FindResultsDirectory();
            Directory.CreateDirectory(dir);
            var jsonPath = Path.Combine(dir, "quality-results-ollama.json");
            var mdPath = Path.Combine(dir, "quality-results-ollama.md");
            var utf8 = new UTF8Encoding(false);
            var json = new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(Results, json) + "\n", utf8);
            File.WriteAllText(mdPath, Markdown(Results), utf8);
            Console.WriteLine($"Ollama quality results written: {jsonPath} and {mdPath}");
        }
        finally
        {
            await OwnerDs.DisposeAsync();
            await AppDs.DisposeAsync();
            await AppForcedHnswDs.DisposeAsync();
            await AppExactDs.DisposeAsync();
            _http.Dispose();
        }
    }

    private async Task<long> CountChunksAsync()
    {
        await using var c = await OwnerDs.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chunks", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Markdown(OllamaQualityResults r)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string F(double v) => v.ToString("0.000", inv);

        L("# Retrieval quality with real embeddings (Ollama)");
        L();
        L($"Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z, git `{r.GitSha}`. Machine-readable copy: `quality-results-ollama.json`.");
        L();
        if (r.Quality.Count == 0)
        {
            L("> **INCOMPLETE RUN.** No quality rows were produced. Do not quote this file.");
            L();
        }
        L($"**{r.Label}**");
        L();
        L("## Setup (stated with every number)");
        L();
        L($"- Embedding model: `{r.EmbeddingModel}`, {r.EmbeddingDimension} dimensions, via {r.EmbeddingProvider} {r.OllamaVersion}. {r.OllamaHostNote}");
        L($"- Chat: {r.ChatProvider}.");
        L($"- Corpus: {r.Tenants} tenants, {r.Documents} documents, {r.BaseCorpusChunks} base chunks ({r.DistinctChunkTextsEmbedded} distinct texts embedded in {r.IngestEmbedSeconds.ToString("0.0", inv)} s), seed {r.CorpusSeed}; {r.TotalChunksInTableAtMeasurement} rows in `chunks` at measurement.");
        L($"- Hostile rows: {r.HostileRowsNote}");
        L($"- Labelled questions: {r.LabelledQuestions} ({r.DistinctQuestionTexts} distinct question texts).");
        L($"- Chunking: {r.ChunkingParameters}.");
        L($"- Runner / hardware: {r.Runner}; {r.Environment.Cpu}, {r.Environment.LogicalCores} logical cores, {r.Environment.TotalMemoryGiB} GiB RAM, {r.Environment.Os}, {r.Environment.Runtime}.");
        L($"- Database: {r.Environment.Postgres}, pgvector {r.Environment.Pgvector}; {r.Environment.DatabaseLocation}.");
        L();
        L("## recall@k and MRR@k");
        L();
        L("| Search path | k | Questions | recall@k | MRR@k |");
        L("|---|---|---|---|---|");
        foreach (var q in r.Quality) L($"| {q.SearchPath} | {q.K} | {q.Questions} | {F(q.RecallAtK)} | {F(q.Mrr)} |");
        if (r.Quality.Count == 0) L("| (not run) | | | | |");
        L();
        L("## Caveats");
        L();
        foreach (var c in r.Caveats) L($"- {c}");
        return sb.ToString();
    }
}

[Collection(OllamaQualityCollection.Name)]
public class OllamaQualityTests(OllamaQualityFixture fx)
{
    [OllamaFact]
    public async Task Recall_at_k_and_mrr_with_real_ollama_embeddings()
    {
        Assert.True(fx.Available, "Ollama quality fixture did not initialise.");
        Assert.Equal(768, fx.Embeddings.Dimension);

        var variants = new (string Name, NpgsqlDataSource Ds)[]
        {
            ("exact search (index scans off)", fx.AppExactDs),
            ("HNSW forced, iterative scan on", fx.AppForcedHnswDs),
            ("planner default, iterative scan on", fx.AppDs),
        };

        // Diagnostic only: QUALITY_REPEAT=N replays the whole variant/question sweep N times in one process
        // (results rows are recorded once). Unset means 1, i.e. unchanged behaviour.
        var repeat = int.TryParse(Environment.GetEnvironmentVariable("QUALITY_REPEAT"), out var rp) && rp > 1 ? rp : 1;
        for (var rep = 0; rep < repeat; rep++)
        foreach (var (name, ds) in variants)
        {
            var store = new NpgsqlChunkStore(ds);
            var service = ServiceFactory.Create(store, fx.Embeddings, new RecordingChatProvider());
            foreach (var k in new[] { 5, 10 })
            {
                var recalls = new List<double>();
                var rrs = new List<double>();
                foreach (var q in fx.Corpus.Questions)
                {
                    var visible = fx.Oracle.VisibleChunks(q.TenantSlug, q.Role);
                    var caller = fx.Oracle.CallerFor(q.TenantSlug, q.Role);
                    var result = await service.QueryAsync(caller, q.Text, k);

                    // Same security checks as the fake-embedding quality run: quality is not allowed to cost isolation.
                    Assert.All(result.RetrievedChunkIds, id => Assert.Contains(id, visible));
                    if (Math.Min(k, visible.Count) != result.RetrievedChunkIds.Count)
                    {
                        // Assertion strength unchanged; on failure we additionally capture why (plan, GUCs, vector sanity).
                        var diag = await DiagnoseAsync(fx, ds, caller, q.Text, k, rep);
                        Assert.Fail(
                            $"returned == min(k, visible) violated on [{name}] k={k} question={q.Id} caller={q.TenantSlug}/{q.Role}: " +
                            $"expected {Math.Min(k, visible.Count)}, got {result.RetrievedChunkIds.Count} (visible={visible.Count}).\n{diag}");
                    }
                    Assert.All(q.ExpectedChunkIds, id => Assert.Contains(id, visible));

                    recalls.Add(Stats.RecallAtK(result.RetrievedChunkIds, q.ExpectedChunkIds, k));
                    rrs.Add(Stats.ReciprocalRank(result.RetrievedChunkIds, q.ExpectedChunkIds, k));
                }

                Assert.Equal(fx.Corpus.Questions.Count, recalls.Count);
                if (rep == 0)
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

    /// <summary>Failure-path only. Re-runs the failing query on a fresh connection of the same data source and reports facts.</summary>
    private static async Task<string> DiagnoseAsync(OllamaQualityFixture fx, NpgsqlDataSource ds, CallerContext caller, string question, int k, int rep)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        try
        {
            var v = await fx.Embeddings.EmbedAsync(question);
            double ss = 0; var nan = 0; var inf = 0; var zeros = 0;
            foreach (var x in v)
            {
                if (float.IsNaN(x)) nan++;
                else if (float.IsInfinity(x)) inf++;
                else { ss += (double)x * x; if (x == 0f) zeros++; }
            }
            L($"DIAG rep={rep} query embedding: dim={v.Length} l2norm={Math.Sqrt(ss):0.0000} nan={nan} inf={inf} zeros={zeros} first3=[{v[0]:0.0000},{v[1]:0.0000},{v[2]:0.0000}]");

            await using (var oc = await fx.OwnerDs.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM chunks), (SELECT count(*) FROM chunks WHERE tenant_id=@t), (SELECT count(*) FROM chunks WHERE embedding IS NULL), " +
                    "(SELECT count(*) FROM chunks WHERE vector_norm(embedding)=0), (SELECT n_live_tup FROM pg_stat_user_tables WHERE relname='chunks'), " +
                    "(SELECT last_analyze::text FROM pg_stat_user_tables WHERE relname='chunks'), (SELECT reltuples::bigint FROM pg_class WHERE relname='chunks')", oc);
                cmd.Parameters.AddWithValue("t", caller.TenantId);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                    L($"DIAG table: total_rows={r.GetValue(0)} tenant_rows(owner)={r.GetValue(1)} null_emb={r.GetValue(2)} zero_norm_emb={r.GetValue(3)} n_live_tup={r.GetValue(4)} last_analyze={r.GetValue(5)} reltuples={r.GetValue(6)}");
            }

            await using var conn = await ds.OpenConnectionAsync();
            const string sql = """
                SELECT c.id, c.doc_id, c.tenant_id, c.text, (c.embedding <=> @q)::float8 AS distance
                FROM chunks c
                JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
                WHERE c.tenant_id = @tenant
                  AND rag_level_allows(rag_role_level(@role), d.required_level)
                ORDER BY c.embedding <=> @q
                LIMIT @k
                """;
            void Bind(NpgsqlCommand cmd)
            {
                cmd.Parameters.AddWithValue("q", new Pgvector.Vector(v));
                cmd.Parameters.AddWithValue("k", NpgsqlDbType.Integer, k);
                cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, caller.TenantId);
                cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, caller.Role);
            }

            foreach (var attempt in new[] { 1, 2 })
            {
                await using var tx = await conn.BeginTransactionAsync();
                await using (var cmd = new NpgsqlCommand(
                    "SELECT set_config('app.tenant_id', @tenant, true), set_config('app.role_level', coalesce(rag_role_level(@role)::text, ''), true)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, caller.TenantId.ToString("D"));
                    cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, caller.Role);
                    await cmd.ExecuteNonQueryAsync();
                }
                await using (var cmd = new NpgsqlCommand("SET LOCAL hnsw.iterative_scan = 'strict_order'", conn, tx))
                    await cmd.ExecuteNonQueryAsync();

                await using (var cmd = new NpgsqlCommand(
                    "SELECT current_setting('hnsw.iterative_scan',true), current_setting('hnsw.ef_search',true), current_setting('hnsw.max_scan_tuples',true), " +
                    "current_setting('enable_seqscan'), current_setting('enable_indexscan'), current_setting('enable_sort'), current_setting('enable_bitmapscan'), " +
                    "current_setting('app.tenant_id',true), current_setting('app.role_level',true), (SELECT count(*) FROM chunks)", conn, tx))
                await using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                        L($"DIAG attempt={attempt} gucs: iterative_scan={r.GetValue(0)} ef_search={r.GetValue(1)} max_scan_tuples={r.GetValue(2)} seqscan={r.GetValue(3)} indexscan={r.GetValue(4)} sort={r.GetValue(5)} bitmapscan={r.GetValue(6)} tenant={r.GetValue(7)} role_level={r.GetValue(8)} rows_visible_under_rls={r.GetValue(9)}");
                }

                await using (var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, conn, tx))
                {
                    Bind(cmd);
                    await using var r = await cmd.ExecuteReaderAsync();
                    L($"DIAG attempt={attempt} EXPLAIN ANALYZE:");
                    while (await r.ReadAsync()) L("   " + r.GetString(0));
                }
                await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM (" + sql + ") s", conn, tx))
                {
                    Bind(cmd);
                    L($"DIAG attempt={attempt} rerun row count = {await cmd.ExecuteScalarAsync()}");
                }
                await tx.RollbackAsync();
            }
        }
        catch (Exception e)
        {
            L($"DIAG collection itself failed: {e.GetType().Name}: {e.Message}");
        }
        return sb.ToString();
    }
}
