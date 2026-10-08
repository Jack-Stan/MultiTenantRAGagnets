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
    IReadOnlyList<AdversarialQuery> AdversarialQueries);
