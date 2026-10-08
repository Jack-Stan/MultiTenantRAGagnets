using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MultiTenantRAGagnets.Core.Providers;

public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the embedding and chat providers. <c>Providers:Kind</c> is "Fake"
    /// (default; keyless, no network) or "Ollama".
    /// </summary>
    public static IServiceCollection AddRagProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var kind = configuration["Providers:Kind"] ?? "Fake";

        if (string.Equals(kind, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var section = configuration.GetSection("Providers:Ollama");
            services.Configure<OllamaOptions>(section);
            var options = section.Get<OllamaOptions>() ?? new OllamaOptions();

            services.AddHttpClient<OllamaProvider>(client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });
            services.AddTransient<IEmbeddingProvider>(sp => sp.GetRequiredService<OllamaProvider>());
            services.AddTransient<IChatProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        }
        else if (string.Equals(kind, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmbeddingProvider, FakeEmbeddingProvider>();
            services.AddSingleton<IChatProvider, FakeChatProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Providers:Kind '{kind}'. Use 'Fake' or 'Ollama'.");
        }

        return services;
    }
}
