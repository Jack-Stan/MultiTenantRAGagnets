namespace MultiTenantRAGagnets.Core.Ingestion;

/// <summary>The document row. Carries the ACL (required_level); chunks never do.</summary>
public sealed record DocumentRecord(Guid Id, Guid TenantId, string Title, int RequiredLevel, int Version);

/// <summary>One embedded chunk, ready to store. Note: no ACL field, by design (TRD section 4).</summary>
public sealed record ChunkRecord(
    Guid Id,
    Guid DocId,
    Guid TenantId,
    int ChunkIndex,
    string Text,
    float[] Embedding,
    int Version);

/// <summary>
/// Storage seam for ingestion. The Npgsql implementation is written elsewhere; tests use an
/// in-memory fake. Contract for implementers:
/// <list type="bullet">
/// <item>Store the document and all its chunks in ONE transaction (all or nothing).</item>
/// <item>Idempotent: re-storing the same (document id, version) replaces, never duplicates
/// (chunk ids are deterministic, see <c>ChunkIdentity</c>).</item>
/// <item>Run as the locked-down app role with tenant/role context set, so RLS WITH CHECK applies.</item>
/// </list>
/// </summary>
public interface IChunkWriter
{
    Task StoreAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default);
}
