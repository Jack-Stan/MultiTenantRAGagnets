using System.Diagnostics.Metrics;

namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>
/// Process-wide retrieval telemetry. Meter <see cref="MeterName"/>, instrument <see cref="IndexFallbackInstrument"/>:
/// a monotonically increasing count of searches where the HNSW index path returned fewer than k rows and the store
/// had to repeat the search as one exact scan. A non-zero rate means the index is silently under-returning.
/// No tags: tenant ids would be unbounded cardinality, and query/chunk text is never recorded.
/// </summary>
public static class RetrievalMetrics
{
    public const string MeterName = "MultiTenantRAGagnets.Retrieval";
    public const string IndexFallbackInstrument = "retrieval.index_fallback";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> IndexFallback = Meter.CreateCounter<long>(
        IndexFallbackInstrument, unit: "{fallback}",
        description: "Searches where the index path returned fewer than k rows and an exact scan was run instead.");

    internal static void RecordIndexFallback() => IndexFallback.Add(1);
}
