namespace MultiTenantRAGagnets.Core.Providers;

public interface IEmbeddingProvider
{
    /// <summary>Identifier of the model, recorded in the audit log.</summary>
    string ModelName { get; }

    /// <summary>Length of every vector this provider returns.</summary>
    int Dimension { get; }

    /// <summary>Embeds one text into a vector of length <see cref="Dimension"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
