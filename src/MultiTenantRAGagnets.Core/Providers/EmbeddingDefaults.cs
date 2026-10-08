namespace MultiTenantRAGagnets.Core.Providers;

/// <summary>
/// The single source of truth for the embedding dimension. The real model
/// (nomic-embed-text) produces 768-dimensional vectors, and the pgvector column
/// and HNSW index are sized from the same number (see docs/TRD.md section 3).
/// Change it here and nowhere else; it is sticky once an index exists.
/// </summary>
public static class EmbeddingDefaults
{
    public const int Dimension = 768;
}
