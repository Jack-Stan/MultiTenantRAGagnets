using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Tests.Support;

/// <summary>Captures exactly what was sent to the "LLM" so tests can assert on the prompt context.</summary>
public sealed class RecordingChatProvider : IChatProvider
{
    public string ModelName => "recording-chat";
    public List<ChatRequest> Requests { get; } = [];

    /// <summary>Extra ids the "model" claims to have cited (hallucinated / foreign).</summary>
    public List<string> ExtraCitations { get; } = [];
    public Exception? ThrowOnComplete { get; set; }

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (ThrowOnComplete is not null) throw ThrowOnComplete;
        var cited = request.Context.Select(c => c.Id).Concat(ExtraCitations).ToList();
        return Task.FromResult(new ChatResponse("answer", cited, ModelName));
    }

    public IEnumerable<string> SentTexts => Requests.SelectMany(r => r.Context).Select(c => c.Text);
}
