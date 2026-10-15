namespace MultiTenantRAGagnets.Eval;

/// <summary>The three roles and their integer levels. Mirrors db/migrations/001_schema.sql (roles table).</summary>
public static class Roles
{
    public const string Employee = "employee";
    public const string Manager = "manager";
    public const string HrAdmin = "hr-admin";

    public static readonly IReadOnlyList<string> All = new[] { Employee, Manager, HrAdmin };

    public static int LevelOf(string role) => role switch
    {
        Employee => 1,
        Manager => 2,
        HrAdmin => 3,
        _ => throw new ArgumentException($"Unknown role '{role}'.", nameof(role)),
    };

    /// <summary>The ONE hierarchy rule, same as rag_level_allows in SQL: required_level &lt;= caller_level.</summary>
    public static bool CanSee(string callerRole, int requiredLevel) => requiredLevel <= LevelOf(callerRole);
}

public sealed record CorpusTenant(Guid Id, string Slug, string Name);

public sealed record CorpusDocument(
    Guid Id,
    string TenantSlug,
    Guid TenantId,
    string TemplateSlug,
    string Title,
    int RequiredLevel,
    int Version,
    bool IsInjection,
    string SecretMarker,
    string Text,
    IReadOnlyList<Guid> ChunkIds,
    IReadOnlyList<Guid> FactChunkIds,
    IReadOnlyList<Guid> SecretChunkIds);

public sealed record CorpusChunk(Guid Id, Guid DocId, string TenantSlug, int RequiredLevel, int ChunkIndex, string Text);

/// <summary>Labelled question for recall@k / MRR. Kind is "fact" or "reference".</summary>
public sealed record LabelledQuestion(
    string Id,
    string TenantSlug,
    string Role,
    string Kind,
    string Text,
    Guid DocId,
    IReadOnlyList<Guid> ExpectedChunkIds);

/// <summary>The leakage oracle: every chunk id a caller of (tenant, role) may see, and nothing else.</summary>
public sealed record ManifestEntry(
    string TenantSlug,
    string Role,
    int Level,
    IReadOnlyList<Guid> VisibleDocIds,
    IReadOnlyList<Guid> VisibleChunkIds);

public static class AdversarialKinds
{
    public const string VerbatimCrossTenant = "verbatim-cross-tenant";
    public const string VerbatimCrossRole = "verbatim-cross-role";
    public const string SecretProbeCrossTenant = "secret-probe-cross-tenant";
    public const string RoleEscalation = "role-escalation";
    public const string PromptInjectionCrossTenant = "prompt-injection-cross-tenant";

    public static readonly IReadOnlyList<string> All = new[]
    {
        VerbatimCrossTenant, VerbatimCrossRole, SecretProbeCrossTenant, RoleEscalation, PromptInjectionCrossTenant,
    };
}

/// <summary>
/// One adversarial query. The caller is (TenantSlug, Role); BaitChunkId is a chunk the caller
/// must NOT be able to see (guaranteed absent from the caller's manifest entry) that the query
/// is built to pull towards.
/// </summary>
public sealed record AdversarialQuery(
    string Id,
    string Kind,
    string TenantSlug,
    string Role,
    string Text,
    Guid BaitChunkId,
    Guid BaitDocId);

public sealed record SyntheticCorpus(
    int Seed,
    string ChunkingParameters,
    IReadOnlyList<CorpusTenant> Tenants,
    IReadOnlyList<CorpusDocument> Documents,
    IReadOnlyList<CorpusChunk> Chunks,
    IReadOnlyList<LabelledQuestion> Questions,
    IReadOnlyList<ManifestEntry> Manifest,
    IReadOnlyList<AdversarialQuery> AdversarialQueries,
    HardExtras? Hard = null)
{
    /// <summary>"standard" (the original template corpus) or "hard" (the additive harder corpus).</summary>
    public string Profile => Hard is null ? "standard" : "hard";
}

/// <summary>Which corpus to build. Standard is the original and the default everywhere; Hard is opt-in.</summary>
public enum CorpusProfile
{
    Standard,
    Hard,
}

/// <summary>Question categories of the hard profile (reporting buckets).</summary>
public static class HardCategories
{
    /// <summary>Plain topic, question shares keywords with the answer sentence.</summary>
    public const string Keyword = "keyword";

    /// <summary>Hand-written paraphrase that avoids the answer sentence's content words.</summary>
    public const string Paraphrase = "paraphrase";

    /// <summary>Answer is split over at least two chunks; recall needs all of them.</summary>
    public const string MultiChunk = "multi-chunk";

    /// <summary>The tenant also holds four superseded editions of the same policy; only the current edition is right.</summary>
    public const string NearDup = "near-dup";

    /// <summary>Documents that repeat the question's wording verbatim but never give the answer.</summary>
    public const string Distractor = "distractor";

    /// <summary>Reference-code lookup of the current edition among same-titled editions and guidance documents.</summary>
    public const string Reference = "reference";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Keyword, Paraphrase, MultiChunk, NearDup, Distractor, Reference,
    };
}

/// <summary>
/// One labelled question as asked by one caller. GoldChunkIds are the chunks that carry the answer (all of them are
/// needed for a multi-chunk answer) whoever asks; PermittedGoldChunkIds is the part this caller may see. When the
/// answer document is above the caller's role AnswerPermitted is false and PermittedGoldChunkIds is empty: the
/// correct outcome is that the permitted set does not contain the answer, and that is what the oracle states.
/// AnswerNeedles are the literal strings that make a chunk an answer chunk; tests use them to prove no other chunk
/// of the tenant carries the answer. GoldGroups has one group per needle (the chunks that wholly contain it): a
/// multi-chunk answer is complete only when every group is hit, whereas overlap duplicates inside one group are
/// interchangeable. GoldChunkIds is the union of the groups.
/// </summary>
public sealed record GoldLabel(
    string Id,
    string TenantSlug,
    string Role,
    string Category,
    string Kind,
    string Text,
    Guid DocId,
    IReadOnlyList<Guid> GoldChunkIds,
    IReadOnlyList<Guid> PermittedGoldChunkIds,
    bool AnswerPermitted,
    IReadOnlyList<string> AnswerNeedles,
    IReadOnlyList<IReadOnlyList<Guid>> GoldGroups);

/// <summary>What a hard-corpus document is: current, superseded, distractor, noise or injection (Edition is 1-based).</summary>
public sealed record DocMeta(Guid DocId, string TenantSlug, string TopicSlug, string Kind, int Edition);

public static class HardDocKinds
{
    public const string Current = "current";
    public const string Superseded = "superseded";
    public const string Distractor = "distractor";
    public const string Noise = "noise";
    public const string Injection = "injection";
}

/// <summary>Extra labels and metadata that only the hard profile carries. Absent (null) for the standard corpus.</summary>
public sealed record HardExtras(IReadOnlyList<GoldLabel> Gold, IReadOnlyList<DocMeta> DocMeta);
