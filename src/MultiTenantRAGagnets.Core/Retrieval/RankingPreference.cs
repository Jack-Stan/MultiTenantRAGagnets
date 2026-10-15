namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>
/// How a search orders the chunks the caller may ALREADY see. It can reorder; it can never admit a row: the
/// candidate pool is produced by the same tenant + role predicate (and RLS) as the plain search, and the
/// preference is applied only to that pool. Off (<see cref="None"/>) is byte-for-byte the previous behaviour.
///
/// Mechanism (current-edition preference): over-fetch <c>k * CandidateMultiplier</c> nearest candidates through the
/// normal index path, then order them by <c>distance + (superseded ? SupersededPenalty : 0)</c> and keep k.
/// Cosine distance lies in [0, 2], so a penalty of 2 or more is strict "every current candidate before any
/// superseded one"; a smaller penalty is a soft demotion that a much closer withdrawn edition can still beat.
/// Superseded chunks are demoted, never hidden: if fewer than k current candidates exist they fill the rest.
/// </summary>
public sealed record RankingPreference
{
    /// <summary>Hard ceiling on the candidate pool so a large k times a large multiplier cannot become an unbounded scan.</summary>
    public const int MaxCandidates = 200;

    public static RankingPreference None { get; } = new();

    public bool PreferCurrentEdition { get; init; }

    /// <summary>Pool size is k times this (1 = no over-fetch, so the preference can only reorder the top k). Capped by <see cref="MaxCandidates"/>.</summary>
    public int CandidateMultiplier { get; init; } = 4;

    /// <summary>Added to the cosine distance of a superseded chunk before ordering. 2.0 or more = strict partition.</summary>
    public double SupersededPenalty { get; init; } = 2.0;

    public void Validate()
    {
        if (CandidateMultiplier is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(CandidateMultiplier), "CandidateMultiplier must be between 1 and 50.");
        if (double.IsNaN(SupersededPenalty) || SupersededPenalty < 0)
            throw new ArgumentOutOfRangeException(nameof(SupersededPenalty), "SupersededPenalty must be >= 0.");
    }

    /// <summary>How many nearest candidates to fetch for a request of k results.</summary>
    public int CandidateCount(int k)
    {
        if (!PreferCurrentEdition) return k;
        Validate();
        return (int)Math.Min((long)k * CandidateMultiplier, Math.Max(k, MaxCandidates));
    }

    /// <summary>
    /// The same ordering the SQL applies, for in-memory stores and tests. <paramref name="nearest"/> must already be
    /// the caller's permitted candidates ordered by distance; the result is a reordered subset of it.
    /// </summary>
    public IReadOnlyList<RetrievedChunk> Rerank(IReadOnlyList<RetrievedChunk> nearest, int k)
    {
        if (!PreferCurrentEdition) return nearest.Take(k).ToList();
        Validate();
        return nearest
            .Select(c => (Chunk: c, Score: c.Distance + (c.Superseded ? SupersededPenalty : 0)))
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Chunk.Superseded)
            .ThenBy(x => x.Chunk.Distance)
            .ThenBy(x => x.Chunk.Id)
            .Take(k)
            .Select(x => x.Chunk)
            .ToList();
    }
}
