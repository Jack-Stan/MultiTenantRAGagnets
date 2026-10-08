using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MultiTenantRAGagnets.Core.Providers;

namespace MultiTenantRAGagnets.Core.Tests;

public class ProviderRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection().AddRagProviders(config).BuildServiceProvider();
    }

    [Fact]
    public void Defaults_to_the_keyless_fakes()
    {
        using var sp = Build();

        Assert.IsType<FakeEmbeddingProvider>(sp.GetRequiredService<IEmbeddingProvider>());
        Assert.IsType<FakeChatProvider>(sp.GetRequiredService<IChatProvider>());
    }

    [Fact]
    public void Ollama_kind_wires_the_ollama_provider()
    {
        using var sp = Build(("Providers:Kind", "Ollama"), ("Providers:Ollama:BaseUrl", "http://ollama:11434"));

        Assert.IsType<OllamaProvider>(sp.GetRequiredService<IEmbeddingProvider>());
        Assert.IsType<OllamaProvider>(sp.GetRequiredService<IChatProvider>());
    }

    [Fact]
    public void Unknown_kind_fails_fast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(("Providers:Kind", "Bogus")));

        Assert.Contains("Bogus", ex.Message);
    }
}
