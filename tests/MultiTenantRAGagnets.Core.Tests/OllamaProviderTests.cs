using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Tests;

/// <summary>Exercises OllamaProvider against a stub HTTP handler: no Ollama needed.</summary>
public class OllamaProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? LastPath { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static OllamaProvider Create(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") },
            Options.Create(new OllamaOptions()));

    private static string EmbedBody(int dimension) =>
        JsonSerializer.Serialize(new { embeddings = new[] { Enumerable.Repeat(0.5f, dimension).ToArray() } });

    [Fact]
    public async Task Embed_returns_the_vector_and_posts_model_and_input()
    {
        var handler = new StubHandler(EmbedBody(EmbeddingDefaults.Dimension));

        var vector = await Create(handler).EmbedAsync("hello");

        Assert.Equal(EmbeddingDefaults.Dimension, vector.Length);
        Assert.Equal("/api/embed", handler.LastPath);
        Assert.Contains("\"model\":\"nomic-embed-text\"", handler.LastRequestBody);
        Assert.Contains("\"input\":\"hello\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task Embed_rejects_a_vector_of_the_wrong_dimension()
    {
        var provider = Create(new StubHandler(EmbedBody(1024)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync("hello"));

        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public async Task Embed_surfaces_http_errors()
    {
        var provider = Create(new StubHandler("model not found", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.EmbedAsync("hello"));

        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Chat_reports_only_ids_that_were_sent_and_cited()
    {
        var body = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content = "You get 25 days [c1]. Also see [ghost]." },
        });
        var handler = new StubHandler(body);
        var request = new ChatRequest("holiday?", new[] { new ContextChunk("c1", "25 days"), new ContextChunk("c2", "other") });

        var response = await Create(handler).CompleteAsync(request);

        Assert.Equal(new[] { "c1" }, response.CitedChunkIds);
        Assert.Equal("/api/chat", handler.LastPath);
        Assert.Contains("\"stream\":false", handler.LastRequestBody);
    }

    [Fact]
    public async Task Chat_without_message_content_is_an_error()
    {
        var provider = Create(new StubHandler("{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync(new ChatRequest("q", Array.Empty<ContextChunk>())));
    }

    [Fact]
    public void Interface_model_names_come_from_options()
    {
        var provider = Create(new StubHandler("{}"));

        Assert.Equal("nomic-embed-text", ((IEmbeddingProvider)provider).ModelName);
        Assert.Equal("llama3.2", ((IChatProvider)provider).ModelName);
    }
}
