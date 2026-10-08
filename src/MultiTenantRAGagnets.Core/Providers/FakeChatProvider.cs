using System.Text;

namespace MultiTenantRAGagnets.Core.Providers;

/// <summary>
/// Deterministic, keyless chat provider: the CI path. It never calls a model; it
/// builds the answer purely from the context it was handed and cites every chunk
/// it was given. That makes "what was sent to the LLM" directly observable, which
/// is what the leakage and audit-integrity tests need.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    public const string Name = "fake-chat";

    public string ModelName => Name;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var cited = request.Context.Select(c => c.Id).ToList();
        var answer = new StringBuilder();
        if (request.Context.Count == 0)
        {
            answer.Append("No permitted context was available for this question.");
        }
        else
        {
            answer.Append("Based on ").Append(request.Context.Count).Append(" permitted chunk(s):");
            foreach (var chunk in request.Context)
            {
                answer.Append(" [").Append(chunk.Id).Append("] ").Append(chunk.Text);
            }
        }

        return Task.FromResult(new ChatResponse(answer.ToString(), cited, ModelName));
    }
}
