using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;

namespace MultiTenantRAGagnets.Leakage.Harness;

/// <summary>Records exactly what was sent to the "LLM", then answers like the real fake (FakeChatProvider).</summary>
public sealed class RecordingChatProvider : IChatProvider
{
    private readonly FakeChatProvider _inner = new();
    public string ModelName => _inner.ModelName;
    public List<ChatRequest> Requests { get; } = [];

    public void Reset() => Requests.Clear();

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return _inner.CompleteAsync(request, cancellationToken);
    }
}

/// <summary>
/// Embeds like <see cref="FakeEmbeddingProvider"/> unless an override vector is assigned. The override is how a
/// query is put EXACTLY on top of a forbidden chunk (distance 0), so similarity search genuinely wants to
/// return it. With plain hash embeddings a query only lands near its bait by luck.
/// The runner is sequential, so a single mutable override is safe.
/// </summary>
public sealed class BaitEmbeddingProvider : IEmbeddingProvider
{
    private readonly FakeEmbeddingProvider _inner = new();
    public float[]? Override { get; set; }
    public string ModelName => _inner.ModelName;
    public int Dimension => _inner.Dimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Override is { } v ? Task.FromResult(v) : _inner.EmbedAsync(text, cancellationToken);
}

public static class ServiceFactory
{
    public static RetrievalService Create(IChunkStore store, IEmbeddingProvider embeddings, IChatProvider chat)
    {
        // Test-only HMAC key, random per process, not a secret and never persisted.
        var audit = Options.Create(new AuditOptions { HashKey = "leakage-test-" + Guid.NewGuid().ToString("N") });
        return new RetrievalService(embeddings, chat, store, new HmacQueryHasher(audit),
            Options.Create(new RetrievalOptions()));
    }
}
