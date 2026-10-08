using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Tests;

public class FakeEmbeddingProviderTests
{
    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    [Fact]
    public async Task Default_dimension_is_the_single_shared_constant()
    {
        var provider = new FakeEmbeddingProvider();

        var vector = await provider.EmbedAsync("hello");

        Assert.Equal(768, EmbeddingDefaults.Dimension);
        Assert.Equal(EmbeddingDefaults.Dimension, provider.Dimension);
        Assert.Equal(EmbeddingDefaults.Dimension, vector.Length);
    }

    [Fact]
    public async Task Same_text_gives_identical_vector_across_calls_and_instances()
    {
        var a = await new FakeEmbeddingProvider().EmbedAsync("Holiday policy: 25 days.");
        var b = await new FakeEmbeddingProvider().EmbedAsync("Holiday policy: 25 days.");

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Different_text_gives_different_vector()
    {
        var provider = new FakeEmbeddingProvider();

        var a = await provider.EmbedAsync("tenant A secret");
        var b = await provider.EmbedAsync("tenant B secret");

        Assert.NotEqual(a, b);
        // Not near-duplicates either: random unit vectors in 768-d are almost orthogonal.
        Assert.True(Math.Abs(Dot(a, b)) < 0.3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a much longer piece of text that spans well past one hash block of input")]
    public async Task Vectors_are_unit_length_and_finite(string text)
    {
        var vector = await new FakeEmbeddingProvider().EmbedAsync(text);

        Assert.All(vector, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(1.0, Math.Sqrt(Dot(vector, vector)), precision: 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1024)]
    public async Task Custom_dimension_is_honoured(int dimension)
    {
        var provider = new FakeEmbeddingProvider(dimension);

        var vector = await provider.EmbedAsync("x");

        Assert.Equal(dimension, provider.Dimension);
        Assert.Equal(dimension, vector.Length);
    }

    [Fact]
    public void Non_positive_dimension_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeEmbeddingProvider(0));
    }

    [Fact]
    public async Task Null_text_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new FakeEmbeddingProvider().EmbedAsync(null!));
    }

    [Fact]
    public async Task Cancelled_token_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FakeEmbeddingProvider().EmbedAsync("x", cts.Token));
    }
}
