namespace MultiTenantRAGagnets.Core.Audit;

/// <summary>
/// One audit_log row to append. Columns match db/migrations/001_schema.sql exactly:
/// tenant_id, user_id, role, query_hash, retrieved_chunk_ids, sent_chunk_ids, model, latency_ms.
/// occurred_at is stamped by the database (DEFAULT now()). query_text is never written: only the hash.
/// </summary>
public sealed record AuditEntry(
    Guid TenantId,
    Guid UserId,
    string Role,
    string QueryHash,
    IReadOnlyList<Guid> RetrievedChunkIds,
    IReadOnlyList<Guid> SentChunkIds,
    string Model,
    int LatencyMs)
{
    /// <summary>Mirrors the table CHECK (sent_chunk_ids &lt;@ retrieved_chunk_ids) so a bug fails here, loudly.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueryHash)) throw new ArgumentException("QueryHash is mandatory.");
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Model is mandatory.");
        if (LatencyMs < 0) throw new ArgumentOutOfRangeException(nameof(LatencyMs));
        var retrieved = RetrievedChunkIds.ToHashSet();
        if (!SentChunkIds.All(retrieved.Contains))
            throw new InvalidOperationException("Audit invariant broken: a chunk was sent to the LLM that was never retrieved.");
    }
}

/// <summary>An audit_log row as read back (for GET /audit). Deliberately has no query text field.</summary>
public sealed record AuditRecord(
    long Id,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid UserId,
    string Role,
    string QueryHash,
    IReadOnlyList<Guid> RetrievedChunkIds,
    IReadOnlyList<Guid> SentChunkIds,
    string Model,
    int LatencyMs);
