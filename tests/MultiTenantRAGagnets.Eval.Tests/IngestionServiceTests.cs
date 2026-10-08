using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Eval.Tests;

public sealed class InMemoryChunkWriter : IChunkWriter
{
    public Dictionary<Guid, DocumentRecord> Documents { get; } = new();
    public Dictionary<Guid, ChunkRecord> Chunks { get; } = new();
    public int StoreCalls { get; private set; }

    public Task StoreAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        StoreCalls++;
        Documents[document.Id] = document;
        foreach (var stale in Chunks.Values.Where(c => c.DocId == document.Id && c.Version == document.Version).ToList())
        {
            Chunks.Remove(stale.Id);
        }

        foreach (var c in chunks)
        {
            Chunks[c.Id] = c;
        }

        return Task.CompletedTask;
    }
}

public class IngestionServiceTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-5222-8222-222222222222");
    private static readonly Guid Doc = Guid.Parse("33333333-3333-5333-8333-333333333333");

    private static IngestRequest Request(string text, int level = 2) =>
        new(Doc, Tenant, "Title", level, 1, text);

    [Fact]
    public async Task Ingest_chunks_embeds_and_stores_with_document_acl_but_none_on_chunks()
    {
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new FakeEmbeddingProvider(), writer);
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i:D4}"));

        var result = await svc.IngestAsync(Request(text));

        Assert.True(result.ChunkCount > 1);
        Assert.Equal(result.ChunkCount, writer.Chunks.Count);
        Assert.Equal(2, writer.Documents[Doc].RequiredLevel);
        Assert.All(writer.Chunks.Values, c =>
        {
            Assert.Equal(Tenant, c.TenantId);
            Assert.Equal(Doc, c.DocId);
            Assert.Equal(EmbeddingDefaults.Dimension, c.Embedding.Length);
        });
        Assert.Contains("size=500", result.ChunkingParameters);
        Assert.Equal(1, writer.StoreCalls);
    }

    [Fact]
    public async Task Reingesting_the_same_version_is_idempotent()
    {
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new FakeEmbeddingProvider(), writer);
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i:D4}"));

        var first = await svc.IngestAsync(Request(text));
        var second = await svc.IngestAsync(Request(text));

        Assert.Equal(first.ChunkIds, second.ChunkIds);
        Assert.Equal(first.ChunkCount, writer.Chunks.Count);
    }

    [Fact]
    public async Task Invalid_requests_store_nothing()
    {
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new FakeEmbeddingProvider(), writer);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.IngestAsync(Request("text", level: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IngestAsync(Request("   ")));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.IngestAsync(new IngestRequest(Doc, Guid.Empty, "t", 1, 1, "x")));
        Assert.Equal(0, writer.StoreCalls);
    }

    [Fact]
    public async Task Embedding_failure_stores_nothing()
    {
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new ThrowingEmbeddings(), writer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IngestAsync(Request("some text to embed")));
        Assert.Equal(0, writer.StoreCalls);
    }

    [Fact]
    public async Task Wrong_dimension_from_provider_is_rejected()
    {
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new WrongDimEmbeddings(), writer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IngestAsync(Request("some text to embed")));
        Assert.Equal(0, writer.StoreCalls);
    }

    [Fact]
    public async Task Ingesting_the_synthetic_corpus_yields_exactly_the_manifest_chunk_ids()
    {
        var corpus = CorpusGenerator.Generate();
        var writer = new InMemoryChunkWriter();
        var svc = new IngestionService(new FakeEmbeddingProvider(), writer);

        foreach (var d in corpus.Documents)
        {
            await svc.IngestAsync(new IngestRequest(d.Id, d.TenantId, d.Title, d.RequiredLevel, d.Version, d.Text));
        }

        Assert.Equal(
            corpus.Chunks.Select(c => c.Id).OrderBy(g => g).ToList(),
            writer.Chunks.Keys.OrderBy(g => g).ToList());
        Assert.Equal(
            corpus.Chunks.ToDictionary(c => c.Id, c => c.Text),
            writer.Chunks.ToDictionary(kv => kv.Key, kv => kv.Value.Text));
    }

    private sealed class ThrowingEmbeddings : IEmbeddingProvider
    {
        public string ModelName => "throwing";
        public int Dimension => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class WrongDimEmbeddings : IEmbeddingProvider
    {
        public string ModelName => "wrong-dim";
        public int Dimension => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new float[3]);
    }
}
