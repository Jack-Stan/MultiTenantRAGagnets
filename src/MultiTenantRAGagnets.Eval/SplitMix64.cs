namespace MultiTenantRAGagnets.Eval;

/// <summary>
/// Own PRNG so output never depends on System.Random's implementation across runtimes.
/// SplitMix64: tiny, fast, fully specified.
/// </summary>
internal sealed class SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    public static SplitMix64 ForStream(int seed, string stream)
    {
        // Derive an independent stream per purpose (FNV-1a of the name, mixed with the seed).
        ulong h = 14695981039346656037UL;
        foreach (var c in stream)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        return new SplitMix64(h ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL));
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform int in [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>Uniform int in [minInclusive, maxInclusive].</summary>
    public int Between(int minInclusive, int maxInclusive) => minInclusive + Next(maxInclusive - minInclusive + 1);

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public List<T> Shuffled<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
