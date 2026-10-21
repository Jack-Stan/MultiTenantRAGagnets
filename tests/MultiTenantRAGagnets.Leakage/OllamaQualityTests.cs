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
///   CORPUS              "standard" (default, unset = same) or "hard": the additive harder corpus
///                       (CorpusProfile.Hard). Hard writes results/quality-results-ollama-hard.* and never the standard files.
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

    /// <summary>CORPUS=hard selects the hard profile; unset/empty/"standard" the original corpus; anything else fails loudly.</summary>
    public static CorpusProfile Profile
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CORPUS")?.Trim();
            if (string.IsNullOrEmpty(v) || v.Equals("standard", StringComparison.OrdinalIgnoreCase)) return CorpusProfile.Standard;
            if (v.Equals("hard", StringComparison.OrdinalIgnoreCase)) return CorpusProfile.Hard;
            throw new InvalidOperationException($"CORPUS='{v}' is not recognised; use 'standard' or 'hard'.");
        }
    }

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

/// <summary>recall@k / MRR@k / complete@k for one (search path, k, category). Hard profile only.</summary>
public sealed class CategoryRow
{
    public string SearchPath { get; set; } = "";
    public int K { get; set; }
    public string Category { get; set; } = "";
    public int Questions { get; set; }
    public int DistinctQuestionTexts { get; set; }
    public double RecallAtK { get; set; }
    public double Mrr { get; set; }

    /// <summary>Share of questions whose every answer part (needle) has a gold chunk in the top k. Equals recall for single-chunk answers.</summary>
    public double CompleteAtK { get; set; }
}

/// <summary>Probes whose answer is above the caller's role. There is nothing to recall; the check is that no forbidden gold comes back.</summary>
public sealed class AboveRoleRow
{
    public string SearchPath { get; set; } = "";
    public int K { get; set; }
    public int Probes { get; set; }
    public int ForbiddenGoldReturned { get; set; }
    public int ProbesWithFullResultSet { get; set; }
}

/// <summary>
/// One cell of the current-edition-preference comparison: the same questions, same search path and k, with the preference
/// off or on. Preference "off" is repeated here (it equals PerCategory) so the table is self-contained.
/// </summary>
public sealed class RankingRow
{
    public string Preference { get; set; } = "";
    public string SearchPath { get; set; } = "";
    public int K { get; set; }
    public string Category { get; set; } = "";
    public int Questions { get; set; }
    public double RecallAtK { get; set; }
    public double Mrr { get; set; }
    public double CompleteAtK { get; set; }

    /// <summary>Mean share of the returned top-k chunks that belong to a superseded document (lower is better for current-edition questions).</summary>
    public double SupersededShareInTopK { get; set; }
}

public sealed class RankingAboveRoleRow
{
    public string Preference { get; set; } = "";
    public string SearchPath { get; set; } = "";
    public int K { get; set; }
    public int Probes { get; set; }
    public int ForbiddenGoldReturned { get; set; }
    public int ProbesWithFullResultSet { get; set; }
}

public sealed class RankingPreferenceInfo
{
    public string Name { get; set; } = "";
    public bool PreferCurrentEdition { get; set; }
    public int CandidateMultiplier { get; set; }
    public double SupersededPenalty { get; set; }
}

public sealed class HardCorpusInfo
{
    public int DocumentsCurrent { get; set; }
    public int DocumentsSuperseded { get; set; }
    public int DocumentsDistractor { get; set; }
    public int DocumentsNoise { get; set; }
    public int DocumentsInjection { get; set; }
    public int QuestionInstances { get; set; }
    public int AnswerableInstances { get; set; }
    public int AboveRoleInstances { get; set; }
    public Dictionary<string, int> DistinctTextsByCategory { get; set; } = new();
    public Dictionary<string, int> AnswerableInstancesByCategory { get; set; } = new();
}

public sealed class OllamaQualityResults
{
    /// <summary>"hard" for the hard run. Null (not written) for the standard run so its file keeps its original shape.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorpusProfile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HardCorpusInfo? HardCorpus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CategoryRow>? PerCategory { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AboveRoleRow>? AboveRole { get; set; }

    /// <summary>Hard profile only. Preference off versus on, per category, per search path, per k, in the same run. Additive: older readers ignore it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RankingPreferenceInfo>? RankingPreferences { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RankingRow>? RankingComparison { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RankingAboveRoleRow>? RankingAboveRole { get; set; }

    /// <summary>Questions of the extra "withdrawn-reachability" probe set (asks about the withdrawn edition), per tenant/role instance; null for the standard run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WithdrawnReachabilityProbes { get; set; }

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

    /// <summary>
    /// Total times the store's exact-scan fallback fired during the whole run (all variants, all repeats). 0 means the
    /// HNSW path always filled k on its own; anything above 0 means a quality number below includes exact-scan rows.
    /// </summary>
    public long IndexFallbackCount { get; set; }

    /// <summary>Same count split by search path, so a fallback in one variant is not averaged away.</summary>
    public Dictionary<string, long> IndexFallbackByPath { get; set; } = new();

    /// <summary>Same count split by "search path | preference" (hard profile), so a fallback under one preference is not averaged away.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? IndexFallbackByPathAndPreference { get; set; }
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

        Corpus = CorpusGenerator.Generate(OllamaEnv.Profile);
        Oracle = new CorpusOracle(Corpus);
        Oracle.VerifyGold(); // no-op for the standard corpus; hard labels are cross-checked against the manifest here

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
        var hard = Corpus.Hard is not null;
        r.Label = hard
            ? "RETRIEVAL QUALITY with REAL embeddings (Ollama) for ingest and query, on the HARD synthetic corpus " +
              "(near-duplicates across and within tenants, distractors, paraphrases, multi-chunk answers, above-role questions). " +
              "Chat is the fake LLM. Synthetic and generated: read these numbers as 'this model on this corpus', not as a general claim."
            : "RETRIEVAL QUALITY with REAL embeddings (Ollama) for ingest and query. Chat is the fake LLM. " +
              "Corpus is the repo's synthetic, template-generated corpus, not production data: read these numbers as " +
              "'this model on this corpus', not as a general claim.";
        if (hard) FillHardInfo(r);
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
        r.Caveats = hard
        ?
        [
            "Embedding input is the raw chunk / question text; OllamaProvider adds no nomic task prefixes (search_document:/search_query:), so this measures the repo's provider as shipped.",
            "The hard corpus is synthetic and generated from hand-written topic templates with a fixed seed: it is harder than the standard corpus by construction, but it is still not real documents. Do not compare with public benchmarks.",
            "recall@k = |gold in top-k| / |gold| over ALL gold chunks (overlap duplicates of one answer sentence count separately, so this is strict); MRR@k = 1/rank of the first gold chunk in top-k, else 0; complete@k = share of questions whose every answer part has a gold chunk in top-k.",
            "Scored questions are those whose answer the caller may see. Above-role probes have no recall: the check is that the permitted set holds no answer and that no forbidden gold chunk is returned.",
            "Questions are scored per (tenant, role) caller against only the chunks that caller may see; the oracle asserts labels never point at forbidden chunks and that no non-gold chunk of the tenant carries the answer text.",
            "Single run per CI job, no confidence intervals. Per-category buckets are small (12 to 72 distinct questions); treat differences of a few points as noise.",
        ]
        :
        [
            "Embedding input is the raw chunk / question text; OllamaProvider adds no nomic task prefixes (search_document:/search_query:), so this measures the repo's provider as shipped.",
            "The corpus is synthetic and template-generated: many questions share wording across tenants and roles, so labelled sets are far easier than real documents. Do not compare with public benchmarks.",
            "Questions are scored per (tenant, role) caller against only the chunks that caller may see; the oracle asserts labels never point at forbidden chunks.",
            "recall@k = |expected in top-k| / |expected|; MRR@k = 1/rank of the first expected chunk in top-k, else 0.",
            "Single run, no confidence intervals; embedding vectors are deterministic for a given model build, but model builds and CPU float differences can shift ranks slightly.",
        ];
    }

    private void FillHardInfo(OllamaQualityResults r)
    {
        var meta = Corpus.Hard!.DocMeta.ToDictionary(m => m.DocId);
        int Kind(string k) => Corpus.Documents.Count(d => meta[d.Id].Kind == k);
        var gold = Corpus.Hard.Gold;
        r.CorpusProfile = "hard";
        r.HardCorpus = new HardCorpusInfo
        {
            DocumentsCurrent = Kind(HardDocKinds.Current),
            DocumentsSuperseded = Kind(HardDocKinds.Superseded),
            DocumentsDistractor = Kind(HardDocKinds.Distractor),
            DocumentsNoise = Kind(HardDocKinds.Noise),
            DocumentsInjection = Kind(HardDocKinds.Injection),
            QuestionInstances = gold.Count,
            AnswerableInstances = gold.Count(g => g.AnswerPermitted),
            AboveRoleInstances = gold.Count(g => !g.AnswerPermitted),
            DistinctTextsByCategory = HardCategories.All.ToDictionary(c => c, c => gold.Where(g => g.Category == c).Select(g => g.Text).Distinct().Count()),
            AnswerableInstancesByCategory = HardCategories.All.ToDictionary(c => c, c => gold.Count(g => g.AnswerPermitted && g.Category == c)),
        };
        r.PerCategory = [];
        r.AboveRole = [];
        r.RankingPreferences = [];
        r.RankingComparison = [];
        r.RankingAboveRole = [];
        r.IndexFallbackByPathAndPreference = new();
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;
        try
        {
            Results.TotalChunksInTableAtMeasurement = await CountChunksAsync();
            var dir = ResultsWriter.FindResultsDirectory();
            Directory.CreateDirectory(dir);
            var stem = Results.CorpusProfile == "hard" ? "quality-results-ollama-hard" : "quality-results-ollama";
            var jsonPath = Path.Combine(dir, stem + ".json");
            var mdPath = Path.Combine(dir, stem + ".md");
            var utf8 = new UTF8Encoding(false);
            var json = new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(Results, json) + "\n", utf8);
            File.WriteAllText(mdPath, Markdown(Results, stem), utf8);
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

    private static string Markdown(OllamaQualityResults r, string stem)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string F(double v) => v.ToString("0.000", inv);

        L("# Retrieval quality with real embeddings (Ollama)");
        L();
        L($"Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z, git `{r.GitSha}`. Machine-readable copy: `{stem}.json`.");
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
        if (r.HardCorpus is { } h)
        {
            L("## Hard corpus");
            L();
            L($"- Documents: {r.Documents} = {h.DocumentsCurrent} current, {h.DocumentsSuperseded} superseded editions, {h.DocumentsDistractor} distractor (guidance + FAQ), {h.DocumentsNoise} noise, {h.DocumentsInjection} prompt-injection.");
            L($"- Question instances: {h.QuestionInstances} = {h.AnswerableInstances} answerable by their caller + {h.AboveRoleInstances} above the caller's role (none permitted). {r.DistinctQuestionTexts} distinct question texts.");
            L();
            L("| Category | Distinct texts | Answerable instances |");
            L("|---|---|---|");
            foreach (var (cat, n) in h.DistinctTextsByCategory) L($"| {cat} | {n} | {h.AnswerableInstancesByCategory[cat]} |");
            L();
            L("## recall@k, MRR@k and complete@k by category");
            L();
            L("| Search path | k | Category | Questions | recall@k | MRR@k | complete@k |");
            L("|---|---|---|---|---|---|---|");
            foreach (var c in r.PerCategory ?? []) L($"| {c.SearchPath} | {c.K} | {c.Category} | {c.Questions} | {F(c.RecallAtK)} | {F(c.Mrr)} | {F(c.CompleteAtK)} |");
            if ((r.PerCategory ?? []).Count == 0) L("| (not run) | | | | | | |");
            L();
            L("## Above-role probes (answer exists, caller may not see it)");
            L();
            L("| Search path | k | Probes | Forbidden gold chunks returned | Probes with a full result set |");
            L("|---|---|---|---|---|");
            foreach (var a in r.AboveRole ?? []) L($"| {a.SearchPath} | {a.K} | {a.Probes} | {a.ForbiddenGoldReturned} | {a.ProbesWithFullResultSet} |");
            if ((r.AboveRole ?? []).Count == 0) L("| (not run) | | | | |");
            L();
        }

        if (r.RankingComparison is { Count: > 0 } rc)
        {
            L("## Current-edition preference: before / after (same run)");
            L();
            L("Preference **off** is the previous behaviour. The others over-fetch k times the multiplier nearest PERMITTED candidates and re-order them with `distance + penalty` for chunks of a superseded document (penalty 2.0 or more is strict: every current candidate before any superseded one). The pool is produced by the unchanged tenant + role predicate and RLS, so the preference reorders and never admits a row; the 'above-role' and 'returned == min(k, visible)' assertions run for every preference.");
            L();
            foreach (var p in r.RankingPreferences ?? [])
                L($"- `{p.Name}`: " + (p.PreferCurrentEdition ? $"pool = {p.CandidateMultiplier} x k, penalty {p.SupersededPenalty.ToString("0.###", inv)}" : "off"));
            L();
            var prefNames = (r.RankingPreferences ?? []).Select(p => p.Name).ToList();
            foreach (var path in rc.Select(x => x.SearchPath).Distinct())
            foreach (var k in rc.Where(x => x.SearchPath == path).Select(x => x.K).Distinct().OrderBy(x => x))
            {
                L($"### {path}, k = {k}: MRR@k / recall@k / complete@k / superseded share of top-k");
                L();
                L("| Category | Questions | " + string.Join(" | ", prefNames) + " |");
                L("|---|---|" + string.Join("|", prefNames.Select(_ => "---")) + "|");
                foreach (var cat in rc.Where(x => x.SearchPath == path && x.K == k).Select(x => x.Category).Distinct())
                {
                    var cells = prefNames.Select(pn =>
                    {
                        var x = rc.FirstOrDefault(y => y.SearchPath == path && y.K == k && y.Category == cat && y.Preference == pn);
                        return x is null ? "n/a" : $"{F(x.Mrr)} / {F(x.RecallAtK)} / {F(x.CompleteAtK)} / {F(x.SupersededShareInTopK)}";
                    });
                    var n = rc.First(y => y.SearchPath == path && y.K == k && y.Category == cat).Questions;
                    L($"| {cat} | {n} | " + string.Join(" | ", cells) + " |");
                }
                L();
            }
            L("### Above-role probes by preference");
            L();
            L("| Preference | Search path | k | Probes | Forbidden gold chunks returned | Probes with a full result set |");
            L("|---|---|---|---|---|---|");
            foreach (var a in r.RankingAboveRole ?? []) L($"| {a.Preference} | {a.SearchPath} | {a.K} | {a.Probes} | {a.ForbiddenGoldReturned} | {a.ProbesWithFullResultSet} |");
            L();
            L("Category `withdrawn-reachability` is an extra probe set built by this run (not in the corpus gold): the same topic question but asking about the withdrawn edition, gold = the fact chunk of any superseded edition. It measures whether withdrawn material is still reachable (demoted, not hidden); it does not measure which withdrawn edition is picked.");
            L();
        }

        L("## Index fallback");
        L();
        L($"- Exact-scan fallback fired **{r.IndexFallbackCount}** time(s) during this run (the HNSW path returned fewer than k rows and the store repeated the search as an exact scan).");
        foreach (var (path, n) in r.IndexFallbackByPath) L($"  - {path}: {n}");
        if (r.IndexFallbackByPathAndPreference is { Count: > 0 } byPref)
        {
            L("- By search path and preference:");
            foreach (var (key, n) in byPref) L($"  - {key}: {n}");
        }
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
    /// <summary>
    /// The preference variants measured in one run. The first entry is the baseline (off) and feeds the original
    /// Quality / PerCategory / AboveRole rows exactly as before; every entry, baseline included, feeds RankingComparison.
    /// Penalties are in cosine-distance units (range 0..2); 2.0 is strict.
    /// </summary>
    private static readonly (string Name, RetrievalOptions Options)[] Preferences =
    [
        ("off", new RetrievalOptions()),
        ("strict, pool 1xk", new RetrievalOptions { PreferCurrentEdition = true, CurrentEditionCandidateMultiplier = 1, SupersededDistancePenalty = 2.0 }),
        ("strict, pool 4xk", new RetrievalOptions { PreferCurrentEdition = true, CurrentEditionCandidateMultiplier = 4, SupersededDistancePenalty = 2.0 }),
        ("soft 0.05, pool 4xk", new RetrievalOptions { PreferCurrentEdition = true, CurrentEditionCandidateMultiplier = 4, SupersededDistancePenalty = 0.05 }),
        ("soft 0.10, pool 4xk", new RetrievalOptions { PreferCurrentEdition = true, CurrentEditionCandidateMultiplier = 4, SupersededDistancePenalty = 0.10 }),
    ];

    private const string WithdrawnCategory = "withdrawn-reachability";
    private const string AllCategory = "(all answerable)";
    private const string AsThingsStand = "As things stand today, ";

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

        var hard = fx.Corpus.Hard is not null;
        // The standard corpus has no superseded documents, so the preference would be a no-op there: only the baseline runs.
        var preferences = hard ? Preferences : Preferences.Take(1).ToArray();

        var supersededDocs = hard
            ? fx.Corpus.Hard!.DocMeta.Where(m => m.Kind == HardDocKinds.Superseded).Select(m => m.DocId).ToHashSet()
            : [];
        bool IsSuperseded(Guid chunkId) => fx.Oracle.ChunkById.TryGetValue(chunkId, out var ch) && supersededDocs.Contains(ch.DocId);

        // Standard corpus: exactly the original labelled questions. Hard corpus: every gold instance, including
        // the ones whose answer is above the caller's role (those have no expected chunks to recall).
        var probes = fx.Corpus.Hard is { } hardExtras
            ? hardExtras.Gold.Select(g => new Probe(g.Id, g.TenantSlug, g.Role, g.Category, g.Text, g.AnswerPermitted, g.PermittedGoldChunkIds, g.GoldChunkIds, g.GoldGroups)).ToList()
            : fx.Corpus.Questions.Select(q => new Probe(q.Id, q.TenantSlug, q.Role, "base", q.Text, true, q.ExpectedChunkIds, q.ExpectedChunkIds, [q.ExpectedChunkIds])).ToList();

        var withdrawn = hard ? BuildWithdrawnProbes() : [];
        if (hard) fx.Results.WithdrawnReachabilityProbes = withdrawn.Count;

        if (hard)
        {
            fx.Results.RankingPreferences = preferences.Select(p => new RankingPreferenceInfo
            {
                Name = p.Name,
                PreferCurrentEdition = p.Options.PreferCurrentEdition,
                CandidateMultiplier = p.Options.CurrentEditionCandidateMultiplier,
                SupersededPenalty = p.Options.SupersededDistancePenalty,
            }).ToList();
        }

        // Diagnostic only: QUALITY_REPEAT=N replays the whole variant/question sweep N times in one process
        // (results rows are recorded once). Unset means 1, i.e. unchanged behaviour.
        var repeat = int.TryParse(Environment.GetEnvironmentVariable("QUALITY_REPEAT"), out var rp) && rp > 1 ? rp : 1;
        for (var rep = 0; rep < repeat; rep++)
        foreach (var (name, ds) in variants)
        foreach (var (prefName, prefOptions) in preferences)
        {
            var baseline = prefName == preferences[0].Name;
            var store = new NpgsqlChunkStore(ds);
            var service = ServiceFactory.Create(store, fx.Embeddings, new RecordingChatProvider(), prefOptions);
            foreach (var k in new[] { 5, 10 })
            {
                var recalls = new List<double>();
                var rrs = new List<double>();
                var byCategory = new Dictionary<string, List<(double Recall, double Rr, double Complete, string Text, double SupShare)>>();
                var aboveProbes = 0;
                var aboveLeaks = 0;
                var aboveFull = 0;

                foreach (var q in probes)
                {
                    var visible = fx.Oracle.VisibleChunks(q.TenantSlug, q.Role);
                    var caller = fx.Oracle.CallerFor(q.TenantSlug, q.Role);
                    var result = await service.QueryAsync(caller, q.Text, k);

                    // Same security checks as the fake-embedding quality run: quality is not allowed to cost isolation.
                    // They run for EVERY preference: a ranking preference may reorder what the caller can see, never add to it.
                    Assert.All(result.RetrievedChunkIds, id => Assert.Contains(id, visible));
                    if (Math.Min(k, visible.Count) != result.RetrievedChunkIds.Count)
                    {
                        // Assertion strength unchanged; on failure we additionally capture why (plan, GUCs, vector sanity).
                        var diag = await DiagnoseAsync(fx, ds, caller, q.Text, k, rep);
                        Assert.Fail(
                            $"returned == min(k, visible) violated on [{name}] preference=[{prefName}] k={k} question={q.Id} caller={q.TenantSlug}/{q.Role}: " +
                            $"expected {Math.Min(k, visible.Count)}, got {result.RetrievedChunkIds.Count} (visible={visible.Count}).\n{diag}");
                    }

                    if (!q.Permitted)
                    {
                        // Answer is above this caller's role: the labelled gold must be invisible, and none of it may be returned.
                        Assert.All(q.AllGold, id => Assert.DoesNotContain(id, visible));
                        var leaked = result.RetrievedChunkIds.Count(q.AllGold.Contains);
                        aboveProbes++;
                        aboveLeaks += leaked;
                        if (result.RetrievedChunkIds.Count == Math.Min(k, visible.Count)) aboveFull++;
                        Assert.Equal(0, leaked);
                        continue;
                    }

                    Assert.All(q.Expected, id => Assert.Contains(id, visible));

                    var recall = Stats.RecallAtK(result.RetrievedChunkIds, q.Expected, k);
                    var rr = Stats.ReciprocalRank(result.RetrievedChunkIds, q.Expected, k);
                    recalls.Add(recall);
                    rrs.Add(rr);

                    var topK = result.RetrievedChunkIds.Take(k).ToHashSet();
                    var complete = q.Groups.All(grp => grp.Any(topK.Contains)) ? 1.0 : 0.0;
                    var supShare = result.RetrievedChunkIds.Count == 0 ? 0.0 : result.RetrievedChunkIds.Count(IsSuperseded) / (double)result.RetrievedChunkIds.Count;
                    if (!byCategory.TryGetValue(q.Category, out var list)) byCategory[q.Category] = list = [];
                    list.Add((recall, rr, complete, q.Text, supShare));
                }

                Assert.Equal(fx.Corpus.Questions.Count, recalls.Count);

                // Extra probe set (hard profile, first repeat only): can a user still reach WITHDRAWN material? Same
                // isolation assertions; the category is recorded in RankingComparison only, never in PerCategory.
                if (hard && rep == 0)
                {
                    foreach (var w in withdrawn)
                    {
                        var visible = fx.Oracle.VisibleChunks(w.TenantSlug, w.Role);
                        var caller = fx.Oracle.CallerFor(w.TenantSlug, w.Role);
                        var result = await service.QueryAsync(caller, w.Text, k);
                        Assert.All(result.RetrievedChunkIds, id => Assert.Contains(id, visible));
                        Assert.Equal(Math.Min(k, visible.Count), result.RetrievedChunkIds.Count);
                        Assert.All(w.Expected, id => Assert.Contains(id, visible));

                        var rr = Stats.ReciprocalRank(result.RetrievedChunkIds, w.Expected, k);
                        var hit = result.RetrievedChunkIds.Take(k).Any(w.Expected.Contains) ? 1.0 : 0.0;
                        var supShare = result.RetrievedChunkIds.Count == 0 ? 0.0 : result.RetrievedChunkIds.Count(IsSuperseded) / (double)result.RetrievedChunkIds.Count;
                        if (!byCategory.TryGetValue(WithdrawnCategory, out var list)) byCategory[WithdrawnCategory] = list = [];
                        // recall@k column holds hit@k (any withdrawn gold chunk in the top k) for this category.
                        list.Add((hit, rr, hit, w.Text, supShare));
                    }
                }

                if (rep == 0 && baseline)
                {
                    fx.Results.Quality.Add(new QualityRow
                    {
                        SearchPath = name,
                        K = k,
                        Questions = recalls.Count,
                        RecallAtK = recalls.Average(),
                        Mrr = rrs.Average(),
                    });
                }

                if (rep == 0 && hard)
                {
                    if (baseline)
                    {
                        foreach (var cat in HardCategories.All.Where(byCategory.ContainsKey))
                        {
                            var l = byCategory[cat];
                            fx.Results.PerCategory!.Add(new CategoryRow
                            {
                                SearchPath = name,
                                K = k,
                                Category = cat,
                                Questions = l.Count,
                                DistinctQuestionTexts = l.Select(x => x.Text).Distinct().Count(),
                                RecallAtK = l.Average(x => x.Recall),
                                Mrr = l.Average(x => x.Rr),
                                CompleteAtK = l.Average(x => x.Complete),
                            });
                        }

                        fx.Results.AboveRole!.Add(new AboveRoleRow
                        {
                            SearchPath = name,
                            K = k,
                            Probes = aboveProbes,
                            ForbiddenGoldReturned = aboveLeaks,
                            ProbesWithFullResultSet = aboveFull,
                        });
                    }

                    // Before/after rows: every preference (the baseline included), every category, plus an overall row.
                    var allAnswerable = HardCategories.All.Where(byCategory.ContainsKey).SelectMany(c => byCategory[c]).ToList();
                    void AddRow(string category, List<(double Recall, double Rr, double Complete, string Text, double SupShare)> l) =>
                        fx.Results.RankingComparison!.Add(new RankingRow
                        {
                            Preference = prefName,
                            SearchPath = name,
                            K = k,
                            Category = category,
                            Questions = l.Count,
                            RecallAtK = l.Average(x => x.Recall),
                            Mrr = l.Average(x => x.Rr),
                            CompleteAtK = l.Average(x => x.Complete),
                            SupersededShareInTopK = l.Average(x => x.SupShare),
                        });
                    foreach (var cat in HardCategories.All.Where(byCategory.ContainsKey)) AddRow(cat, byCategory[cat]);
                    if (allAnswerable.Count > 0) AddRow(AllCategory, allAnswerable);
                    if (byCategory.TryGetValue(WithdrawnCategory, out var wl)) AddRow(WithdrawnCategory, wl);

                    fx.Results.RankingAboveRole!.Add(new RankingAboveRoleRow
                    {
                        Preference = prefName,
                        SearchPath = name,
                        K = k,
                        Probes = aboveProbes,
                        ForbiddenGoldReturned = aboveLeaks,
                        ProbesWithFullResultSet = aboveFull,
                    });
                }
            }

            // A silent fallback must be visible in the results file. Summed over repeats; never asserted on here.
            var fired = store.IndexFallbackCount;
            fx.Results.IndexFallbackCount += fired;
            fx.Results.IndexFallbackByPath[name] = fx.Results.IndexFallbackByPath.GetValueOrDefault(name) + fired;
            if (fx.Results.IndexFallbackByPathAndPreference is { } byPref)
            {
                var key = $"{name} | {prefName}";
                byPref[key] = byPref.GetValueOrDefault(key) + fired;
            }
        }
    }

    /// <summary>
    /// Questions that ask about the WITHDRAWN edition of a versioned policy: the near-duplicate question with its
    /// "As things stand today" cue replaced by "For the withdrawn earlier edition". Gold is the fact chunk of any
    /// superseded edition of the same topic: the answer sentence has the same template in every edition, so the
    /// chunks of the superseded documents that match the current answer sentence with its number replaced by a
    /// wildcard are exactly those. This is a reachability probe (is withdrawn material still returned?), not a
    /// claim about which withdrawn edition the model should pick, and it is built here, not in the corpus gold.
    /// </summary>
    private List<Probe> BuildWithdrawnProbes()
    {
        var hard = fx.Corpus.Hard!;
        var meta = hard.DocMeta.ToDictionary(m => m.DocId);
        var chunksByDoc = fx.Corpus.Chunks.GroupBy(c => c.DocId).ToDictionary(g => g.Key, g => g.ToList());
        var probes = new List<Probe>();

        foreach (var g in hard.Gold.Where(x => x.Category == HardCategories.NearDup && x.AnswerPermitted && x.AnswerNeedles.Count > 0))
        {
            if (!g.Text.StartsWith(AsThingsStand, StringComparison.Ordinal)) continue;
            var current = meta[g.DocId];
            var visible = fx.Oracle.VisibleChunks(g.TenantSlug, g.Role);

            var pattern = System.Text.RegularExpressions.Regex.Escape(
                System.Text.RegularExpressions.Regex.Replace(g.AnswerNeedles[0], @"\d+", "@@NUM@@")).Replace("@@NUM@@", @"\d+");
            var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            var gold = hard.DocMeta
                .Where(m => m.Kind == HardDocKinds.Superseded && m.TenantSlug == current.TenantSlug && m.TopicSlug == current.TopicSlug)
                .SelectMany(m => chunksByDoc[m.DocId])
                .Where(c => re.IsMatch(c.Text) && visible.Contains(c.Id))
                .Select(c => c.Id)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)
                .ToList();
            if (gold.Count == 0) continue;

            probes.Add(new Probe(
                g.Id + "-withdrawn", g.TenantSlug, g.Role, WithdrawnCategory,
                "For the withdrawn earlier edition, " + g.Text[AsThingsStand.Length..],
                true, gold, gold, [gold]));
        }
        return probes;
    }

    private sealed record Probe(
        string Id, string TenantSlug, string Role, string Category, string Text, bool Permitted,
        IReadOnlyList<Guid> Expected, IReadOnlyList<Guid> AllGold, IReadOnlyList<IReadOnlyList<Guid>> Groups);

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

            // Graph reachability: can an unfiltered, exhaustive iterative HNSW scan even SEE every row? Owner connection (no RLS).
            await using (var oc = await fx.OwnerDs.OpenConnectionAsync())
            {
                async Task<string> Reach(string label)
                {
                    await using var tx = await oc.BeginTransactionAsync();
                    foreach (var s in new[] { "SET LOCAL enable_seqscan=off", "SET LOCAL enable_sort=off", "SET LOCAL hnsw.iterative_scan='strict_order'", "SET LOCAL hnsw.max_scan_tuples=1000000" })
                        await using (var c0 = new NpgsqlCommand(s, oc, tx)) await c0.ExecuteNonQueryAsync();
                    await using var cmd = new NpgsqlCommand(
                        "WITH r AS (SELECT id FROM chunks ORDER BY embedding <=> @q LIMIT 1000000) " +
                        "SELECT (SELECT count(*) FROM r), (SELECT count(*) FROM chunks), " +
                        "(SELECT count(*) FROM chunks c WHERE c.tenant_id=@t AND c.id IN (SELECT id FROM r)), (SELECT count(*) FROM chunks c WHERE c.tenant_id=@t), " +
                        "(SELECT count(*) FROM chunks c WHERE c.id NOT IN (SELECT id FROM r) AND c.tenant_id NOT IN (SELECT id FROM tenants WHERE slug LIKE 'hostile-%')), " +
                        "(SELECT count(*) FROM chunks c WHERE c.id NOT IN (SELECT id FROM r) AND c.tenant_id IN (SELECT id FROM tenants WHERE slug LIKE 'hostile-%')), " +
                        "(SELECT count(*) FROM chunks), (SELECT count(DISTINCT embedding) FROM chunks)", oc, tx);
                    cmd.Parameters.AddWithValue("q", new Pgvector.Vector(v));
                    cmd.Parameters.AddWithValue("t", caller.TenantId);
                    await using var r = await cmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    var res = $"DIAG reach[{label}]: index_returned={r.GetValue(0)} of total={r.GetValue(1)}; caller_tenant {r.GetValue(2)} of {r.GetValue(3)} reachable; unreachable corpus rows={r.GetValue(4)}, unreachable hostile rows={r.GetValue(5)}; distinct embeddings={r.GetValue(7)} of {r.GetValue(6)}";
                    await r.CloseAsync();
                    await tx.RollbackAsync();
                    return res;
                }
                L(await Reach("as built; read-only"));
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
