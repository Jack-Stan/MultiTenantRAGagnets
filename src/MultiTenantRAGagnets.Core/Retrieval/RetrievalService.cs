using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>What a query returns. Retrieved vs sent ids are kept apart because the audit log keeps them apart.</summary>
public sealed record QueryResult(
    string Answer,
    IReadOnlyList<Guid> CitedChunkIds,
    IReadOnlyList<Guid> RetrievedChunkIds,
    IReadOnlyList<Guid> SentChunkIds,
    string Model,
    int LatencyMs);

/// <summary>Raised when the store returned a chunk from another tenant on the AppAndRls path. Never reaches the LLM.</summary>
public sealed class TenantBoundaryViolationException(string message) : Exception(message);

/// <summary>
/// The query flow of APP_FLOW.md section 2:
/// embed -> BEGIN (tenant/role context applied by the store) -> filtered vector search -> permitted
/// chunks only -> prompt context -> IChatProvider -> audit row -> COMMIT.
/// The filter happens BEFORE the LLM; nothing the store did not return as permitted can be in the prompt.
/// </summary>
public sealed class RetrievalService
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly IChatProvider _chat;
    private readonly IChunkStore _store;
    private readonly IQueryHasher _hasher;
    private readonly RetrievalOptions _options;

    public RetrievalService(
        IEmbeddingProvider embeddings,
        IChatProvider chat,
        IChunkStore store,
        IQueryHasher hasher,
        IOptions<RetrievalOptions> options)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The production entry point: app-layer filter AND RLS.</summary>
    public Task<QueryResult> QueryAsync(CallerContext caller, string question, int? k = null, CancellationToken cancellationToken = default) =>
        RunAsync(caller, question, k, RetrievalFilterMode.AppAndRls, cancellationToken);

    /// <summary>
    /// TEST-ONLY seam (internal on purpose): the same flow with the app-layer predicate removed, so RLS is
    /// the only wall. For the RLS-only leakage run and the dropped-policy negative control. Not wired to any
    /// endpoint; reachable only from assemblies granted InternalsVisibleTo.
    /// </summary>
    internal Task<QueryResult> QueryWithoutAppFilterForTestingAsync(CallerContext caller, string question, int? k = null, CancellationToken cancellationToken = default) =>
        RunAsync(caller, question, k, RetrievalFilterMode.RlsOnly, cancellationToken);

    private async Task<QueryResult> RunAsync(
        CallerContext caller, string question, int? requestedK, RetrievalFilterMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Validate();
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Question must not be empty.", nameof(question));
        if (question.Length > _options.MaxQuestionLength)
            throw new ArgumentException($"Question is longer than {_options.MaxQuestionLength} characters.", nameof(question));
        var k = requestedK ?? _options.DefaultK;
        if (k < 1 || k > _options.MaxK) throw new ArgumentOutOfRangeException(nameof(requestedK), $"k must be between 1 and {_options.MaxK}.");

        var started = Stopwatch.GetTimestamp();

        // Embed before opening the transaction so the connection is not held while the model runs.
        var embedding = await _embeddings.EmbedAsync(question, ct).ConfigureAwait(false);
        if (embedding.Length != EmbeddingDefaults.Dimension)
            throw new InvalidOperationException(
                $"Embedding provider returned {embedding.Length} dimensions; the chunks.embedding column is vector({EmbeddingDefaults.Dimension}).");

        await using var session = await _store.BeginAsync(caller, ct).ConfigureAwait(false);

        var retrieved = await session.SearchAsync(embedding, k, mode, ct).ConfigureAwait(false);

        if (mode == RetrievalFilterMode.AppAndRls)
        {
            // Belt on top of braces: the SQL already filters, but if a store ever hands back a foreign-tenant
            // chunk it dies here, before the prompt is built.
            var foreign = retrieved.FirstOrDefault(c => c.TenantId != caller.TenantId);
            if (foreign is not null)
                throw new TenantBoundaryViolationException(
                    $"Store returned chunk {foreign.Id} from a different tenant on the filtered path; aborting before the LLM.");
        }

        var sent = AssembleContext(retrieved);
        var query = question;

        ChatResponse? response = null;
        ExceptionDispatchInfo? chatFailure = null;
        if (sent.Count > 0)
        {
            try
            {
                var context = sent.Select(c => new ContextChunk(c.Id.ToString("D"), c.Text)).ToList();
                response = await _chat.CompleteAsync(new ChatRequest(query, context), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Still audited below: a query that retrieved chunks but whose LLM call failed is exactly
                // what an audit trail should show. The failure is rethrown after the row is written.
                chatFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        var sentIds = sent.Select(c => c.Id).ToList();
        var answer = response?.Answer ?? _options.NoContextAnswer;
        var model = response?.Model ?? _chat.ModelName;

        // Only ids that were actually in the prompt can be cited; anything else the model made up is dropped.
        var sentSet = sentIds.ToHashSet();
        var cited = (response?.CitedChunkIds ?? [])
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty && sentSet.Contains(g))
            .Distinct()
            .ToList();

        var latencyMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var entry = new AuditEntry(
            caller.TenantId, caller.UserId, caller.Role,
            _hasher.Hash(question),
            retrieved.Select(c => c.Id).ToList(),
            sentIds,
            model,
            latencyMs);
        entry.Validate();

        // No audit row, no answer: if this throws the transaction rolls back and the caller gets an error.
        await session.AppendAuditAsync(entry, ct).ConfigureAwait(false);
        await session.CommitAsync(ct).ConfigureAwait(false);

        chatFailure?.Throw();

        return new QueryResult(answer, cited, entry.RetrievedChunkIds, sentIds, model, latencyMs);
    }

    /// <summary>Takes retrieved chunks in rank order until the character budget is spent. Always keeps at least one.</summary>
    private List<RetrievedChunk> AssembleContext(IReadOnlyList<RetrievedChunk> retrieved)
    {
        var sent = new List<RetrievedChunk>(retrieved.Count);
        var used = 0;
        foreach (var chunk in retrieved)
        {
            if (sent.Count > 0 && used + chunk.Text.Length > _options.MaxContextChars) break;
            sent.Add(chunk);
            used += chunk.Text.Length;
        }
        return sent;
    }
}
