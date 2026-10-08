using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;

namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>A chunk returned by vector search. Carries its tenant so the service can assert the wall.</summary>
public sealed record RetrievedChunk(Guid Id, Guid DocumentId, Guid TenantId, string Text, double Distance);

/// <summary>How the vector search is filtered.</summary>
public enum RetrievalFilterMode
{
    /// <summary>Production path: app-layer tenant + role predicate in the WHERE, and RLS on top.</summary>
    AppAndRls = 0,

    /// <summary>
    /// TEST-ONLY: no app-layer predicate, RLS is the only wall. Exists for the RLS-only leakage run and
    /// the dropped-policy negative control (TESTING.md section 1). Reachable only through the internal
    /// <see cref="RetrievalService"/> seam; no public API or endpoint can request it.
    /// </summary>
    RlsOnly = 1,
}

/// <summary>
/// The database seam. A store hands out one <see cref="IChunkStoreSession"/> per request; the session
/// owns ONE transaction in which the tenant/role context is already applied (SET LOCAL semantics).
/// </summary>
public interface IChunkStore
{
    Task<IChunkStoreSession> BeginAsync(CallerContext caller, CancellationToken cancellationToken = default);
}

/// <summary>
/// One request's transaction. Disposing without <see cref="CommitAsync"/> rolls back. Nothing a session
/// applied survives its disposal, so a pooled connection cannot carry tenant context to the next borrower.
/// </summary>
public interface IChunkStoreSession : IAsyncDisposable
{
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding, int k, RetrievalFilterMode mode, CancellationToken cancellationToken = default);

    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a document and replaces its chunks, inside this session's transaction.
    /// Contract matches <see cref="IChunkWriter"/> (idempotent per document id + version).
    /// </summary>
    Task StoreDocumentAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IChunkWriter"/> backed by an <see cref="IChunkStore"/>: opens the caller's transaction
/// (tenant/role applied, so RLS WITH CHECK is live), stores, commits. Store-agnostic; works with the
/// Npgsql store and the in-memory fake alike.
/// </summary>
public sealed class StoreBackedChunkWriter : IChunkWriter
{
    private readonly IChunkStore _store;
    private readonly CallerAccessor _caller;

    public StoreBackedChunkWriter(IChunkStore store, CallerAccessor caller)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
    }

    public async Task StoreAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        var caller = _caller.Current;
        // The wall: a caller can only write into their own tenant. RLS WITH CHECK enforces it again.
        if (document.TenantId != caller.TenantId)
            throw new InvalidOperationException("Document tenant does not match the authenticated caller's tenant.");
        if (chunks.Any(c => c.TenantId != caller.TenantId || c.DocId != document.Id))
            throw new InvalidOperationException("Chunk tenant/document does not match the document being stored.");

        await using var session = await _store.BeginAsync(caller, cancellationToken).ConfigureAwait(false);
        await session.StoreDocumentAsync(document, chunks, cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
