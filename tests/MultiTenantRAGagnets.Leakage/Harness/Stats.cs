namespace MultiTenantRAGagnets.Leakage.Harness;

public static class Stats
{
    /// <summary>Nearest-rank percentile (p in 0..100). Same definition every time, stated in the results file.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double p)
    {
        if (samples.Count == 0) throw new ArgumentException("No samples.", nameof(samples));
        if (p is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(p));
        var sorted = samples.OrderBy(x => x).ToArray();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>|expected intersect top-k| / |expected|. Expected must be non-empty.</summary>
    public static double RecallAtK(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> expected, int k)
    {
        if (expected.Count == 0) throw new ArgumentException("A labelled question needs at least one expected chunk.", nameof(expected));
        var topK = ranked.Take(k).ToHashSet();
        return expected.Count(topK.Contains) / (double)expected.Count;
    }

    /// <summary>1/rank of the first expected chunk within the top k, else 0 (MRR@k).</summary>
    public static double ReciprocalRank(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> expected, int k)
    {
        var wanted = expected.ToHashSet();
        var top = ranked.Take(k).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            if (wanted.Contains(top[i])) return 1.0 / (i + 1);
        }
        return 0;
    }
}
