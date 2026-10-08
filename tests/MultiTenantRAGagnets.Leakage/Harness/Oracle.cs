using System.Security.Cryptography;
using System.Text;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;

namespace MultiTenantRAGagnets.Leakage.Harness;

public static class Ids
{
    /// <summary>Stable name-based UUID, so seeded rows keep the same ids run after run.</summary>
    public static Guid Name(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var b = hash.AsSpan(0, 16).ToArray();
        b[7] = (byte)((b[7] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b);
    }

    public static Guid User(string tenantSlug, string role) => Name($"leakage-user|{tenantSlug}|{role}");
}

/// <summary>
/// The oracle: what each (tenant, role) caller may see, taken from the generator's own manifest (not from the
/// database, not from the code under test). The fixture separately proves the database agrees with it.
/// </summary>
public sealed class CorpusOracle
{
    private readonly Dictionary<(string, string), HashSet<Guid>> _visible;
    private readonly Dictionary<(string, string), string[]> _forbiddenSecrets;
    private readonly Dictionary<string, Guid> _tenantIds;

    public CorpusOracle(SyntheticCorpus corpus)
    {
        Corpus = corpus;
        ChunkById = corpus.Chunks.ToDictionary(c => c.Id);
        _tenantIds = corpus.Tenants.ToDictionary(t => t.Slug, t => t.Id);
        _visible = corpus.Manifest.ToDictionary(m => (m.TenantSlug, m.Role), m => m.VisibleChunkIds.ToHashSet());
        _forbiddenSecrets = new Dictionary<(string, string), string[]>();
        foreach (var m in corpus.Manifest)
        {
            var visibleDocs = m.VisibleDocIds.ToHashSet();
            // Every secret marker whose document this caller may NOT see (other tenants' and higher levels').
            _forbiddenSecrets[(m.TenantSlug, m.Role)] = corpus.Documents
                .Where(d => !visibleDocs.Contains(d.Id))
                .Select(d => d.SecretMarker)
                .ToArray();
        }
    }

    public SyntheticCorpus Corpus { get; }
    public IReadOnlyDictionary<Guid, CorpusChunk> ChunkById { get; }

    public IReadOnlySet<Guid> VisibleChunks(string tenantSlug, string role) => _visible[(tenantSlug, role)];

    public IReadOnlyList<string> ForbiddenSecrets(string tenantSlug, string role) => _forbiddenSecrets[(tenantSlug, role)];

    public Guid TenantId(string slug) => _tenantIds[slug];

    public CallerContext CallerFor(string tenantSlug, string role) =>
        new(TenantId(tenantSlug), Ids.User(tenantSlug, role), role);
}
