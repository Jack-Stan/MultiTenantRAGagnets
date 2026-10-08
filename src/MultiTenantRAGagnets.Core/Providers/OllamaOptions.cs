namespace MultiTenantRAGagnets.Core.Providers;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Must produce <see cref="EmbeddingDefaults.Dimension"/>-length vectors.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public string ChatModel { get; set; } = "llama3.2";

    public int TimeoutSeconds { get; set; } = 120;
}
