using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Tests;

public class FakeChatProviderTests
{
    private static readonly ChatRequest Request = new(
        "How many holiday days?",
        new[]
        {
            new ContextChunk("c1", "Employees get 25 days."),
            new ContextChunk("c2", "Carry-over is capped at 5 days."),
        });

    [Fact]
    public async Task Same_request_gives_same_response()
    {
        var provider = new FakeChatProvider();

        var first = await provider.CompleteAsync(Request);
        var second = await provider.CompleteAsync(Request);

        Assert.Equal(first.Answer, second.Answer);
        Assert.Equal(first.CitedChunkIds, second.CitedChunkIds);
    }

    [Fact]
    public async Task Cites_exactly_the_chunks_it_was_sent_in_order()
    {
        var response = await new FakeChatProvider().CompleteAsync(Request);

        Assert.Equal(new[] { "c1", "c2" }, response.CitedChunkIds);
        Assert.Equal(FakeChatProvider.Name, response.Model);
    }

    [Fact]
    public async Task Answer_is_built_only_from_the_supplied_context()
    {
        var response = await new FakeChatProvider().CompleteAsync(Request);

        Assert.Contains("Employees get 25 days.", response.Answer);
        Assert.Contains("[c2]", response.Answer);
    }

    [Fact]
    public async Task Empty_context_gives_a_no_context_answer_and_no_citations()
    {
        var response = await new FakeChatProvider().CompleteAsync(new ChatRequest("anything", Array.Empty<ContextChunk>()));

        Assert.Empty(response.CitedChunkIds);
        Assert.Contains("No permitted context", response.Answer);
    }

    [Fact]
    public async Task Null_request_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new FakeChatProvider().CompleteAsync(null!));
    }
}
