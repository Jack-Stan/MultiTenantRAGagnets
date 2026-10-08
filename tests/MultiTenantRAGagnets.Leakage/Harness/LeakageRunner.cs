using System.Text;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;

namespace MultiTenantRAGagnets.Leakage.Harness;

public enum RunMode { AppLayer, RlsOnly }

/// <summary>Thrown when a negative control reports zero leaks: the harness cannot see leaks, so no green means anything.</summary>
public sealed class HollowHarnessException(string message) : Exception(message);

public sealed record QueryOutcome(
    string QueryId,
    string Kind,
    string Tenant,
    string Role,
    int Returned,
    int ExpectedReturned,
    IReadOnlyList<Guid> LeakedRetrieved,
    IReadOnlyList<Guid> LeakedSent,
    IReadOnlyList<string> SecretMarkersInPrompt,
    int PromptTextMismatches,
    bool SentIdsDisagree,
    bool BaitRetrieved,
    string? Error);

public sealed class LeakReport
{
    public required string Label { get; init; }
    public required RunMode Mode { get; init; }
    public required bool BaitOnQueryPoint { get; init; }
    public required int K { get; init; }
    public required List<QueryOutcome> Outcomes { get; init; }

    public int Queries => Outcomes.Count;
    public int TotalReturned => Outcomes.Sum(o => o.Returned);
    public int LeakedRetrievedChunks => Outcomes.Sum(o => o.LeakedRetrieved.Count);
    public int LeakedSentChunks => Outcomes.Sum(o => o.LeakedSent.Count);
    public int SecretMarkerHits => Outcomes.Sum(o => o.SecretMarkersInPrompt.Count);
    public int QueriesWithLeak => Outcomes.Count(o => o.LeakedRetrieved.Count > 0 || o.LeakedSent.Count > 0 || o.SecretMarkersInPrompt.Count > 0);
    public int Errors => Outcomes.Count(o => o.Error is not null);
    public int WrongCount => Outcomes.Count(o => o.Error is null && o.Returned != o.ExpectedReturned);
    public int PromptIntegrityFailures => Outcomes.Count(o => o.PromptTextMismatches > 0 || o.SentIdsDisagree);
    public int BaitRetrievedQueries => Outcomes.Count(o => o.BaitRetrieved);
    public int NonEmptyQueries => Outcomes.Count(o => o.Returned > 0);
    public int CrossTenantLeakedChunks { get; set; }
    public int CrossRoleLeakedChunks { get; set; }

    /// <summary>The headline number: leaked chunks in what was retrieved OR what was put in the prompt, plus secret markers found in the prompt.</summary>
    public int TotalLeaks => LeakedRetrievedChunks + LeakedSentChunks + SecretMarkerHits;

    public string Summary =>
        $"[{Label}] mode={Mode} baitOnQueryPoint={BaitOnQueryPoint} k={K} queries={Queries} returnedChunks={TotalReturned} " +
        $"LEAKS={TotalLeaks} (retrieved={LeakedRetrievedChunks}, sentToLlm={LeakedSentChunks}, secretMarkersInPrompt={SecretMarkerHits}) " +
        $"queriesWithLeak={QueriesWithLeak} crossTenant={CrossTenantLeakedChunks} crossRole={CrossRoleLeakedChunks} " +
        $"errors={Errors} wrongCount={WrongCount} promptIntegrityFailures={PromptIntegrityFailures} " +
        $"baitRetrievedQueries={BaitRetrievedQueries} nonEmptyQueries={NonEmptyQueries}";

    /// <summary>
    /// The pass condition for a REAL run (A or B): zero leaks, zero errors, every query returned exactly
    /// min(k, visible) chunks (so "0 leaks" cannot be a side effect of "0 rows"), prompt agrees with the oracle.
    /// </summary>
    public void AssertClean()
    {
        var problems = new List<string>();
        if (TotalLeaks > 0) problems.Add($"{TotalLeaks} leaked item(s) across {QueriesWithLeak} queries");
        if (Errors > 0) problems.Add($"{Errors} query error(s)");
        if (WrongCount > 0) problems.Add($"{WrongCount} queries returned a count other than min(k, visible): empty or short results make '0 leaks' meaningless");
        if (PromptIntegrityFailures > 0) problems.Add($"{PromptIntegrityFailures} queries where the prompt text or ids disagree with what was retrieved");
        if (Queries == 0) problems.Add("no queries were run");
        if (problems.Count == 0) return;

        var sb = new StringBuilder().AppendLine(Summary);
        foreach (var o in Outcomes.Where(o => o.LeakedRetrieved.Count + o.LeakedSent.Count + o.SecretMarkersInPrompt.Count > 0 || o.Error is not null || o.Returned != o.ExpectedReturned).Take(10))
        {
            sb.AppendLine($"  {o.QueryId} ({o.Kind}, {o.Tenant}/{o.Role}): returned={o.Returned} expected={o.ExpectedReturned} " +
                          $"leakedRetrieved=[{string.Join(",", o.LeakedRetrieved)}] leakedSent=[{string.Join(",", o.LeakedSent)}] " +
                          $"markers=[{string.Join(",", o.SecretMarkersInPrompt)}] error={o.Error}");
        }
        throw new Xunit.Sdk.XunitException($"{Label}: {string.Join("; ", problems)}.{Environment.NewLine}{sb}");
    }
}

public static class NegativeControlVerdict
{
    /// <summary>
    /// The control only passes when it LEAKS. Zero leaks here means the queries do not bait anything or the app
    /// filter is masking the database layer: the whole suite is hollow.
    /// </summary>
    public static void RequireLeaks(LeakReport report, string whatWasBroken)
    {
        if (report.TotalLeaks > 0) return;
        throw new HollowHarnessException(
            $"HARNESS IS HOLLOW: the negative control ({whatWasBroken}) reported 0 leaks. " +
            "With the protection removed the suite MUST leak; if it does not, a green result on the real runs proves nothing. " +
            report.Summary);
    }
}

/// <summary>
/// Runs the adversarial queries through a RetrievalService and checks every result against the oracle,
/// independently in three places: the ids retrieved, the ids and texts that reached the chat provider
/// (ground truth is the recording provider, not the service's own report), and forbidden secret markers
/// anywhere in the prompt text.
/// </summary>
public sealed class LeakageRunner(
    CorpusOracle oracle,
    RecordingChatProvider chat,
    BaitEmbeddingProvider embeddings,
    Func<CallerContext, string, int, Task<QueryResult>> invoke)
{
    public async Task<LeakReport> RunAsync(string label, RunMode mode, int k, bool baitOnQueryPoint)
    {
        var outcomes = new List<QueryOutcome>();
        int crossTenant = 0, crossRole = 0;

        foreach (var q in oracle.Corpus.AdversarialQueries)
        {
            var visible = oracle.VisibleChunks(q.TenantSlug, q.Role);
            var caller = oracle.CallerFor(q.TenantSlug, q.Role);
            chat.Reset();
            embeddings.Override = baitOnQueryPoint
                ? FakeEmbeddingProvider.Embed(oracle.ChunkById[q.BaitChunkId].Text)
                : null;

            QueryResult? result = null;
            string? error = null;
            try
            {
                result = await invoke(caller, q.Text, k).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                embeddings.Override = null;
            }

            var retrieved = result?.RetrievedChunkIds ?? [];
            var leakedRetrieved = retrieved.Where(id => !visible.Contains(id)).ToList();

            var promptChunks = chat.Requests.SelectMany(r => r.Context).ToList();
            var promptIds = new List<Guid>();
            var leakedSent = new List<Guid>();
            var mismatches = 0;
            var markers = new List<string>();
            var forbidden = oracle.ForbiddenSecrets(q.TenantSlug, q.Role);
            foreach (var pc in promptChunks)
            {
                if (!Guid.TryParse(pc.Id, out var id))
                {
                    mismatches++;
                    continue;
                }
                promptIds.Add(id);
                if (!visible.Contains(id)) leakedSent.Add(id);
                if (oracle.ChunkById.TryGetValue(id, out var known) && known.Text != pc.Text) mismatches++;
                foreach (var secret in forbidden)
                {
                    if (pc.Text.Contains(secret, StringComparison.Ordinal)) markers.Add(secret);
                }
            }

            // The answer handed back to the caller must not carry forbidden secrets either (the fake LLM echoes its context).
            if (result is not null)
            {
                foreach (var secret in forbidden)
                {
                    if (result.Answer.Contains(secret, StringComparison.Ordinal)) markers.Add(secret);
                }
            }

            // The service's own record of what it sent must equal what the provider actually received.
            var reportedSent = (result?.SentChunkIds ?? []).ToHashSet();
            var disagree = result is not null && !reportedSent.SetEquals(promptIds);

            foreach (var id in leakedRetrieved.Concat(leakedSent).Distinct())
            {
                if (oracle.ChunkById.TryGetValue(id, out var c) && c.TenantSlug == q.TenantSlug) crossRole++;
                else crossTenant++; // another tenant, or a row the corpus does not know (hostile/fixture tenants)
            }

            outcomes.Add(new QueryOutcome(
                q.Id, q.Kind, q.TenantSlug, q.Role,
                retrieved.Count,
                Math.Min(k, visible.Count),
                leakedRetrieved, leakedSent.Distinct().ToList(), markers.Distinct().ToList(),
                mismatches, disagree,
                retrieved.Contains(q.BaitChunkId),
                error));
        }

        return new LeakReport
        {
            Label = label,
            Mode = mode,
            BaitOnQueryPoint = baitOnQueryPoint,
            K = k,
            Outcomes = outcomes,
            CrossTenantLeakedChunks = crossTenant,
            CrossRoleLeakedChunks = crossRole,
        };
    }
}
