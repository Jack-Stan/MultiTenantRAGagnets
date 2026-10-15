using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;

namespace MultiTenantRAGagnets.Leakage.Harness;

/// <summary>
/// In-memory model of the database over the generated corpus. It exists ONLY so the harness logic (runner,
/// oracle checks, verdicts) can be exercised on a laptop with no Postgres: a correct model must report 0 leaks,
/// a deliberately broken one must report leaks. It proves nothing about Postgres or RLS.
/// </summary>
public sealed class ModelStore : IChunkStore
{
    private readonly SyntheticCorpus _corpus;
    private readonly List<(CorpusChunk Chunk, Guid TenantId, float[] Vector)> _rows;
    private readonly HashSet<Guid> _superseded;

    /// <summary>false = the "RLS policy" is gone (negative control).</summary>
    public bool RlsActive { get; init; } = true;

    /// <summary>true = the app-layer filter ignores the tenant even when asked to apply it.</summary>
    public bool AppFilterBroken { get; init; }

    /// <summary>true = pretend the store silently returns fewer rows than it should (under-return bug).</summary>
    public int? CapReturnedRows { get; init; }

    public ModelStore(SyntheticCorpus corpus)
    {
        _corpus = corpus;
        _superseded = corpus.Hard is null
            ? []
            : corpus.Hard.DocMeta.Where(m => m.Kind == HardDocKinds.Superseded).Select(m => m.DocId).ToHashSet();
        var tenantIds = corpus.Tenants.ToDictionary(t => t.Slug, t => t.Id);
        _rows = corpus.Chunks
            .Select(c => (c, tenantIds[c.TenantSlug], FakeEmbeddingProvider.Embed(c.Text)))
            .ToList();
    }

    public Task<IChunkStoreSession> BeginAsync(CallerContext caller, CancellationToken cancellationToken = default) =>
        Task.FromResult<IChunkStoreSession>(new Session(this, caller));

    private sealed class Session(ModelStore store, CallerContext caller) : IChunkStoreSession
    {
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] q, int k, RetrievalFilterMode mode, RankingPreference ranking, CancellationToken ct = default)
        {
            IEnumerable<(CorpusChunk Chunk, Guid TenantId, float[] Vector)> rows = store._rows;
            if (store.RlsActive)
            {
                rows = rows.Where(r => r.TenantId == caller.TenantId && Roles.CanSee(caller.Role, r.Chunk.RequiredLevel));
            }
            if (mode == RetrievalFilterMode.AppAndRls && !store.AppFilterBroken)
            {
                rows = rows.Where(r => r.TenantId == caller.TenantId && Roles.CanSee(caller.Role, r.Chunk.RequiredLevel));
            }

            var take = store.CapReturnedRows is { } cap ? Math.Min(k, cap) : k;
            var pool = rows
                .Select(r => (r, d: CosineDistance(q, r.Vector)))
                .OrderBy(x => x.d)
                .Take(ranking.CandidateCount(take))
                .Select(x => new RetrievedChunk(x.r.Chunk.Id, x.r.Chunk.DocId, x.r.TenantId, x.r.Chunk.Text, x.d, store._superseded.Contains(x.r.Chunk.DocId)))
                .ToList();
            return Task.FromResult(ranking.Rerank(pool, take));
        }

        public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);

        public Task StoreDocumentAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * (double)b[i];
        return 1.0 - dot; // both are unit vectors
    }
}
