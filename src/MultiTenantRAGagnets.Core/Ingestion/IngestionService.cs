using MultiTenantRAGagnets.Core.Chunking;
using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Ingestion;

public sealed record IngestRequest(Guid DocumentId, Guid TenantId, string Title, int RequiredLevel, int Version, string Text);

public sealed record IngestResult(Guid DocumentId, int ChunkCount, IReadOnlyList<Guid> ChunkIds, string ChunkingParameters);

/// <summary>
/// Ingest orchestration: validate, chunk (fixed parameters), embed each chunk, then hand the
/// whole document to <see cref="IChunkWriter"/> in a single call. Embedding happens BEFORE any
/// write, so an embedding failure stores nothing.
/// </summary>
public sealed class IngestionService
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly IChunkWriter _writer;

    public IngestionService(IEmbeddingProvider embeddings, IChunkWriter writer)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId == Guid.Empty) throw new ArgumentException("TenantId is required.", nameof(request));
        if (request.DocumentId == Guid.Empty) throw new ArgumentException("DocumentId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Title is required.", nameof(request));
        if (request.RequiredLevel < 1) throw new ArgumentException("RequiredLevel must be >= 1 (no default; forgetting it must not mean public).", nameof(request));
        if (request.Version < 1) throw new ArgumentException("Version must be >= 1.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is empty.", nameof(request));

        var textChunks = Chunker.Chunk(request.Text);
        var records = new List<ChunkRecord>(textChunks.Count);

        foreach (var chunk in textChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = await _embeddings.EmbedAsync(chunk.Text, cancellationToken).ConfigureAwait(false);
            if (vector.Length != _embeddings.Dimension)
            {
                throw new InvalidOperationException(
                    $"Embedding provider '{_embeddings.ModelName}' returned {vector.Length} dims, expected {_embeddings.Dimension}.");
            }

            records.Add(new ChunkRecord(
                ChunkIdentity.For(request.DocumentId, request.Version, chunk.Index),
                request.DocumentId,
                request.TenantId,
                chunk.Index,
                chunk.Text,
                vector,
                request.Version));
        }

        var document = new DocumentRecord(request.DocumentId, request.TenantId, request.Title, request.RequiredLevel, request.Version);
        await _writer.StoreAsync(document, records, cancellationToken).ConfigureAwait(false);

        return new IngestResult(request.DocumentId, records.Count, records.Select(r => r.Id).ToList(), ChunkingParameters.Describe());
    }
}
