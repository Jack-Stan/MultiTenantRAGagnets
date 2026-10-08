using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTenantRAGagnets.Leakage.Harness;

public sealed class RunSummary
{
    public string Label { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool BaitOnQueryPoint { get; set; }
    public int K { get; set; }
    public int Queries { get; set; }
    public int ReturnedChunks { get; set; }
    public int Leaks { get; set; }
    public int LeakedRetrievedChunks { get; set; }
    public int LeakedSentToLlmChunks { get; set; }
    public int SecretMarkersInPrompt { get; set; }
    public int QueriesWithLeak { get; set; }
    public int CrossTenantLeakedChunks { get; set; }
    public int CrossRoleLeakedChunks { get; set; }
    public int Errors { get; set; }
    public int QueriesWithWrongRowCount { get; set; }
    public int BaitRetrievedQueries { get; set; }

    public static RunSummary From(LeakReport r) => new()
    {
        Label = r.Label,
        Mode = r.Mode.ToString(),
        BaitOnQueryPoint = r.BaitOnQueryPoint,
        K = r.K,
        Queries = r.Queries,
        ReturnedChunks = r.TotalReturned,
        Leaks = r.TotalLeaks,
        LeakedRetrievedChunks = r.LeakedRetrievedChunks,
        LeakedSentToLlmChunks = r.LeakedSentChunks,
        SecretMarkersInPrompt = r.SecretMarkerHits,
        QueriesWithLeak = r.QueriesWithLeak,
        CrossTenantLeakedChunks = r.CrossTenantLeakedChunks,
        CrossRoleLeakedChunks = r.CrossRoleLeakedChunks,
        Errors = r.Errors,
        QueriesWithWrongRowCount = r.WrongCount,
        BaitRetrievedQueries = r.BaitRetrievedQueries,
    };
}

public sealed class NegativeControlSummary
{
    public string Broken { get; set; } = "";
    public RunSummary Run { get; set; } = new();
    public bool DetectedLeaks { get; set; }
    public string Expectation { get; set; } = "";
}

public sealed class UnderReturnSummary
{
    public string Scenario { get; set; } = "";
    public int NoiseRowsOtherTenant { get; set; }
    public int PermittedRowsVisibleToCaller { get; set; }
    public int ForbiddenRowsOnQueryPoint { get; set; }
    public string IndexPathEvidence { get; set; } = "";
    public List<UnderReturnCase> Cases { get; set; } = [];
    public int ReturnedWithIterativeScanOff { get; set; }
    public int KForIterativeOffControl { get; set; }
}

public sealed class UnderReturnCase
{
    public string Path { get; set; } = "";
    public string Caller { get; set; } = "";
    public int K { get; set; }
    public int Available { get; set; }
    public int Returned { get; set; }
}

public sealed class QualityRow
{
    public string SearchPath { get; set; } = "";
    public int K { get; set; }
    public int Questions { get; set; }
    public double RecallAtK { get; set; }
    public double Mrr { get; set; }
}

public sealed class LatencyRow
{
    public string Measure { get; set; } = "";
    public string PlannerPath { get; set; } = "";
    public int Samples { get; set; }
    public int K { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double MeanMs { get; set; }
}

public sealed class EnvironmentInfo
{
    public string Os { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int LogicalCores { get; set; }
    public double TotalMemoryGiB { get; set; }
    public string Runtime { get; set; } = "";
    public string Postgres { get; set; } = "";
    public string Pgvector { get; set; } = "";
    public string DatabaseLocation { get; set; } = "";
    public bool Ci { get; set; }

    public static EnvironmentInfo Capture() => new()
    {
        Os = RuntimeInformation.OSDescription,
        Cpu = CpuName(),
        LogicalCores = Environment.ProcessorCount,
        TotalMemoryGiB = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
        Runtime = RuntimeInformation.FrameworkDescription,
        Ci = Infrastructure.DbEnv.IsCi,
    };

    private static string CpuName()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null) return line[(line.IndexOf(':') + 1)..].Trim();
            }
        }
        catch (IOException) { /* fall through */ }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }
}

public sealed class CorpusInfo
{
    public int Seed { get; set; }
    public int Tenants { get; set; }
    public int Documents { get; set; }
    public int BaseCorpusChunks { get; set; }
    public int ExtraHostileChunks { get; set; }
    public long TotalChunksInTableAtMeasurement { get; set; }
    public int AdversarialQueries { get; set; }
    public int LabelledQuestions { get; set; }
    public string ChunkingParameters { get; set; } = "";
    public string EmbeddingProviderKind { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimension { get; set; }
}

public sealed class LeakageResults
{
    public string Schema { get; set; } = "mtra-leakage-results/1";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string GitSha { get; set; } = "unknown";
    public string Disclaimer { get; set; } =
        "Leakage numbers are a security result. recall@k and MRR are HARNESS SMOKE TEST values only when EmbeddingProviderKind is Fake: " +
        "fake embeddings carry no semantics, so they say nothing about retrieval quality.";
    public EnvironmentInfo Environment { get; set; } = new();
    public CorpusInfo Corpus { get; set; } = new();
    public RunSummary? RunA_AppLayer_Natural { get; set; }
    public RunSummary? RunA_AppLayer_BaitOnQueryPoint { get; set; }
    public RunSummary? RunB_RlsOnly_Natural { get; set; }
    public RunSummary? RunB_RlsOnly_BaitOnQueryPoint { get; set; }
    public List<NegativeControlSummary> NegativeControls { get; set; } = [];
    public UnderReturnSummary? UnderReturn { get; set; }
    public string QualityLabel { get; set; } = "";
    public List<QualityRow> Quality { get; set; } = [];
    public string LatencyNote { get; set; } = "";
    public List<LatencyRow> Latency { get; set; } = [];
    public List<string> MissingSections { get; set; } = [];
}

public static class ResultsWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string FindResultsDirectory()
    {
        var overrideDir = System.Environment.GetEnvironmentVariable("RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiTenantRAGagnets.sln"))) return Path.Combine(dir.FullName, "results");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "results");
    }

    public static string? GitSha()
    {
        var fromEnv = System.Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && o.Length >= 7 ? o : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static (string JsonPath, string MdPath) Write(LeakageResults results, string directory)
    {
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "leakage-results.json");
        var mdPath = Path.Combine(directory, "leakage-results.md");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, Json) + "\n", utf8);
        File.WriteAllText(mdPath, Markdown(results), utf8);
        return (jsonPath, mdPath);
    }

    public static string Markdown(LeakageResults r)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string F(double v) => v.ToString("0.000", inv);
        string Row(RunSummary? s) => s is null
            ? "| (not run) | | | | | |"
            : $"| {s.Label} | {s.Queries} | {s.K} | {s.ReturnedChunks} | **{s.Leaks}** | {s.BaitRetrievedQueries} |";

        L("# Leakage results");
        L();
        L($"Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z, git `{r.GitSha}`, CI={r.Environment.Ci}.");
        L("Machine-readable copy: `leakage-results.json`. Produced by `tests/MultiTenantRAGagnets.Leakage`.");
        L();
        if (r.MissingSections.Count > 0)
        {
            L($"> **INCOMPLETE RUN.** These sections did not produce a result: {string.Join(", ", r.MissingSections)}. Do not quote this file.");
            L();
        }
        L("## Setup (stated with every number)");
        L();
        L($"- Corpus: {r.Corpus.Tenants} tenants, {r.Corpus.Documents} documents, {r.Corpus.BaseCorpusChunks} base chunks (seed {r.Corpus.Seed}); " +
          $"plus {r.Corpus.ExtraHostileChunks} extra rows for the `returned == k` test; {r.Corpus.TotalChunksInTableAtMeasurement} rows in `chunks` at measurement.");
        L($"- Adversarial queries: {r.Corpus.AdversarialQueries}. Labelled questions: {r.Corpus.LabelledQuestions}.");
        L($"- Chunking: {r.Corpus.ChunkingParameters}.");
        L($"- Embedding provider kind: **{r.Corpus.EmbeddingProviderKind}** (`{r.Corpus.EmbeddingModel}`, {r.Corpus.EmbeddingDimension} dims). Ollama was not used.");
        L($"- Hardware: {r.Environment.Cpu}, {r.Environment.LogicalCores} logical cores, {r.Environment.TotalMemoryGiB} GiB RAM, {r.Environment.Os}, {r.Environment.Runtime}.");
        L($"- Database: {r.Environment.Postgres}, pgvector {r.Environment.Pgvector}; {r.Environment.DatabaseLocation}.");
        L();
        L("## Leakage (security result)");
        L();
        L("| Run | Queries | k | Chunks returned | Leaks | Queries where the forbidden bait chunk was retrieved |");
        L("|---|---|---|---|---|---|");
        L(Row(r.RunA_AppLayer_Natural));
        L(Row(r.RunA_AppLayer_BaitOnQueryPoint));
        L(Row(r.RunB_RlsOnly_Natural));
        L(Row(r.RunB_RlsOnly_BaitOnQueryPoint));
        L();
        L("Leaks = forbidden chunk ids retrieved + forbidden chunk ids that reached the chat provider + forbidden secret markers found in the prompt text. " +
          "\"Bait on query point\" puts the query vector exactly on a chunk the caller must not see, so similarity search wants to return it. " +
          "Run A is the full service (app filter + RLS); run B removes the app filter so only RLS stands.");
        L();
        L("## Negative control (the suite must be able to fail)");
        L();
        L("| What was broken | Variant | Leaks | Cross-tenant | Cross-role | Detected |");
        L("|---|---|---|---|---|---|");
        foreach (var n in r.NegativeControls)
        {
            L($"| {n.Broken} | {n.Run.Label} | **{n.Run.Leaks}** | {n.Run.CrossTenantLeakedChunks} | {n.Run.CrossRoleLeakedChunks} | {(n.DetectedLeaks ? "yes" : "NO (harness hollow)")} |");
        }
        if (r.NegativeControls.Count == 0) L("| (not run) | | | | | |");
        L();
        L("## `returned == k` under a hostile neighbour distribution");
        L();
        if (r.UnderReturn is { } u)
        {
            L($"{u.Scenario} Noise rows from another tenant: {u.NoiseRowsOtherTenant}; permitted rows for the caller: {u.PermittedRowsVisibleToCaller}; " +
              $"forbidden same-tenant rows sitting on the query point: {u.ForbiddenRowsOnQueryPoint}.");
            L();
            L($"Index path evidence: {u.IndexPathEvidence}");
            L();
            L("| Path | Caller | k | Available | Returned |");
            L("|---|---|---|---|---|");
            foreach (var c in u.Cases) L($"| {c.Path} | {c.Caller} | {c.K} | {c.Available} | {c.Returned} |");
            L();
            L($"Control with `hnsw.iterative_scan = off` at k={u.KForIterativeOffControl}: returned **{u.ReturnedWithIterativeScanOff}** (the under-return bug this assertion guards against).");
        }
        else
        {
            L("(not run)");
        }
        L();
        L("## Retrieval quality");
        L();
        L($"**{r.QualityLabel}**");
        L();
        L("| Search path | k | Questions | recall@k | MRR@k |");
        L("|---|---|---|---|---|");
        foreach (var q in r.Quality) L($"| {q.SearchPath} | {q.K} | {q.Questions} | {F(q.RecallAtK)} | {F(q.Mrr)} |");
        if (r.Quality.Count == 0) L("| (not run) | | | | |");
        L();
        L("## Latency");
        L();
        L(r.LatencyNote);
        L();
        L("| Measure | Planner path | k | Samples | p50 ms | p95 ms | p99 ms | mean ms |");
        L("|---|---|---|---|---|---|---|---|");
        foreach (var x in r.Latency) L($"| {x.Measure} | {x.PlannerPath} | {x.K} | {x.Samples} | {F(x.P50Ms)} | **{F(x.P95Ms)}** | {F(x.P99Ms)} | {F(x.MeanMs)} |");
        if (r.Latency.Count == 0) L("| (not run) | | | | | | | |");
        L();
        L($"_{r.Disclaimer}_");
        return sb.ToString();
    }
}
