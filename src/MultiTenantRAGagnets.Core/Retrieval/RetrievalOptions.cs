namespace MultiTenantRAGagnets.Core.Retrieval;

public sealed class RetrievalOptions
{
    public int DefaultK { get; set; } = 5;
    public int MaxK { get; set; } = 20;
    public int MaxQuestionLength { get; set; } = 2000;

    /// <summary>
    /// Prompt budget in characters. Retrieved chunks beyond it are NOT sent to the LLM; that is exactly
    /// why the audit log records retrieved and sent ids separately.
    /// </summary>
    public int MaxContextChars { get; set; } = 8000;

    /// <summary>
    /// Rank chunks of a current document ahead of chunks of a withdrawn edition (documents.superseded_at) among the
    /// chunks the caller may already see. Demotes, never hides, and never changes what is permitted. See
    /// <see cref="RankingPreference"/>. Default off; see docs/TRD.md (ranking preference) for the measured effect.
    /// </summary>
    public bool PreferCurrentEdition { get; set; }

    /// <summary>Over-fetch factor for <see cref="PreferCurrentEdition"/>: the pool is k times this.</summary>
    public int CurrentEditionCandidateMultiplier { get; set; } = 4;

    /// <summary>Distance added to a superseded chunk. 2.0 or more is strict (cosine distance is at most 2).</summary>
    public double SupersededDistancePenalty { get; set; } = 2.0;

    /// <summary>The ranking preference this configuration asks the store for.</summary>
    public RankingPreference ToRankingPreference() => PreferCurrentEdition
        ? new RankingPreference
        {
            PreferCurrentEdition = true,
            CandidateMultiplier = CurrentEditionCandidateMultiplier,
            SupersededPenalty = SupersededDistancePenalty,
        }
        : RankingPreference.None;

    /// <summary>The answer when no permitted chunk exists. The LLM is not called in that case.</summary>
    public string NoContextAnswer { get; set; } = "I could not find anything you are permitted to see that answers that.";
}
