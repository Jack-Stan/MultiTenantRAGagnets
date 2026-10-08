namespace MultiTenantRAGagnets.Core.Providers;

/// <summary>A chunk handed to the LLM. <see cref="Id"/> is what the answer cites.</summary>
public sealed record ContextChunk(string Id, string Text);

/// <summary>A chat request: the question plus the permitted context chunks only.</summary>
public sealed record ChatRequest(string Question, IReadOnlyList<ContextChunk> Context);

/// <summary>The model's answer and the ids of the chunks it cited.</summary>
public sealed record ChatResponse(string Answer, IReadOnlyList<string> CitedChunkIds, string Model);

public interface IChatProvider
{
    string ModelName { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
