using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Retrieval;

namespace MultiTenantRAGagnets.Core.Tests.Support;

/// <summary>
/// In-memory stand-in for the Postgres store. It models the two walls separately so tests can switch one
/// off: the app-layer predicate (applied only in AppAndRls mode) and RLS (applied always unless
/// <see cref="RlsActive"/> is false, which simulates a dropped policy). Hierarchy mirrors 001_schema.sql
/// (employee 1, manager 2, hr-admin 3; allowed when required &lt;= caller; unknown role = no access).
/// Writes are buffered per session and only land on CommitAsync, like a transaction.
/// </summary>
public sealed class InMemoryChunkStore : IChunkStore
{
    private static readonly Dictionary<string, int> Levels = new() { ["employee"] = 1, ["manager"] = 2, ["hr-admin"] = 3 };

    private readonly List<(ChunkRecord Chunk, int RequiredLevel)> _chunks = [];
    private readonly List<AuditRecord> _audit = [];
    private long _auditId;

    /// <summary>false = pretend the RLS policy was dropped.</summary>
    public bool RlsActive { get; set; } = true;

    /// <summary>true = misbehaving store that ignores the tenant filter even on the filtered path.</summary>
    public bool IgnoreTenantFilterEvenWhenAsked { get; set; }

    public List<CallerContext> BeginCalls { get; } = [];
    public IReadOnlyList<AuditRecord> AuditRows => _audit;
    public int CommitCount { get; private set; }
    public IReadOnlyList<ChunkRecord> AllChunks => _chunks.Select(c => c.Chunk).ToList();

    public void AddChunk(Guid tenantId, int requiredLevel, string text, Guid? id = null, Guid? docId = null)
    {
        var doc = docId ?? Guid.NewGuid();
        _chunks.Add((new ChunkRecord(id ?? Guid.NewGuid(), doc, tenantId, 0, text, Providers.FakeEmbeddingProvider.Embed(text), 1), requiredLevel));
    }

    public Task<IChunkStoreSession> BeginAsync(CallerContext caller, CancellationToken cancellationToken = default)
    {
        BeginCalls.Add(caller);
        return Task.FromResult<IChunkStoreSession>(new Session(this, caller));
    }

    private static int? LevelOf(string role) => Levels.TryGetValue(role, out var l) ? l : null;

    private sealed class Session(InMemoryChunkStore store, CallerContext caller) : IChunkStoreSession
    {
        private readonly List<AuditEntry> _pendingAudit = [];
        private readonly List<(DocumentRecord Doc, IReadOnlyList<ChunkRecord> Chunks)> _pendingDocs = [];
        private bool _committed;

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryEmbedding, int k, RetrievalFilterMode mode, CancellationToken cancellationToken = default)
        {
            var callerLevel = LevelOf(caller.Role);
            IEnumerable<(ChunkRecord Chunk, int RequiredLevel)> rows = store._chunks;

            // RLS wall: tenant AND role, one combined policy. Fail-closed on unknown role.
            if (store.RlsActive)
                rows = rows.Where(r => r.Chunk.TenantId == caller.TenantId && callerLevel is { } cl && r.RequiredLevel <= cl);

            // App-layer wall: same predicate in the WHERE, only on the filtered path.
            if (mode == RetrievalFilterMode.AppAndRls && !store.IgnoreTenantFilterEvenWhenAsked)
                rows = rows.Where(r => r.Chunk.TenantId == caller.TenantId && callerLevel is { } cl && r.RequiredLevel <= cl);

            var result = rows
                .Select(r => new RetrievedChunk(r.Chunk.Id, r.Chunk.DocId, r.Chunk.TenantId, r.Chunk.Text, CosineDistance(queryEmbedding, r.Chunk.Embedding)))
                .OrderBy(c => c.Distance)
                .Take(k)
                .ToList();
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(result);
        }

        public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            entry.Validate();
            _pendingAudit.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>(
                store._audit.Where(a => a.TenantId == caller.TenantId).OrderByDescending(a => a.Id).Take(limit).ToList());

        public Task StoreDocumentAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
        {
            // RLS WITH CHECK: tenant pinned to the caller's, level no higher than the caller's.
            if (document.TenantId != caller.TenantId || LevelOf(caller.Role) is not { } cl || document.RequiredLevel > cl)
                throw new InvalidOperationException("RLS WITH CHECK violation (simulated).");
            _pendingDocs.Add((document, chunks));
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            foreach (var e in _pendingAudit)
            {
                store._audit.Add(new AuditRecord(++store._auditId, DateTimeOffset.UtcNow, e.TenantId, e.UserId, e.Role, e.QueryHash,
                    e.RetrievedChunkIds, e.SentChunkIds, e.Model, e.LatencyMs));
            }
            foreach (var (doc, chunks) in _pendingDocs)
            {
                store._chunks.RemoveAll(c => c.Chunk.DocId == doc.Id);
                store._chunks.AddRange(chunks.Select(c => (c, doc.RequiredLevel)));
            }
            store.CommitCount++;
            _committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // uncommitted buffers are simply dropped = rollback

        public bool Committed => _committed;
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
