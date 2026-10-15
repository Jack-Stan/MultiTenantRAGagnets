using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Tests.Support;

namespace MultiTenantRAGagnets.Core.Tests;

/// <summary>
/// The current-edition ranking preference (RankingPreference / RetrievalOptions.PreferCurrentEdition).
/// The contract under test: the preference reorders the caller's PERMITTED chunks and can never admit a row, it
/// demotes superseded chunks rather than hiding them, and it keeps returned == min(k, visible).
/// </summary>
public class CurrentEditionRankingTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid User = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    /// <summary>Always returns the same unit vector, so test chunks can be placed at exact cosine distances from the query.</summary>
    private sealed class FixedEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelName => "fixed";
        public int Dimension => EmbeddingDefaults.Dimension;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(Unit(0, 0));
    }

    /// <summary>A unit vector at cosine distance <paramref name="distance"/> from the query (which is e0). salt picks a different orthogonal axis.</summary>
    private static float[] AtDistance(double distance, int salt)
    {
        var v = new float[EmbeddingDefaults.Dimension];
        var cos = 1.0 - distance;
        v[0] = (float)cos;
        v[1 + salt % 700] = (float)Math.Sqrt(Math.Max(0, 1 - cos * cos));
        return v;
    }

    private static float[] Unit(int index, int _)
    {
        var v = new float[EmbeddingDefaults.Dimension];
        v[index] = 1f;
        return v;
    }

    private sealed class Rig
    {
        public InMemoryChunkStore Store { get; } = new();
        public RetrievalOptions Options { get; } = new();
        private readonly RecordingChatProvider _chat = new();

        public RetrievalService Service => new(
            new FixedEmbeddingProvider(), _chat, Store,
            new HmacQueryHasher(Microsoft.Extensions.Options.Options.Create(new AuditOptions { HashKey = "test-only-hash-key-0123456789abcdef" })),
            Microsoft.Extensions.Options.Options.Create(Options));
    }

    private static CallerContext Caller(string role, Guid? tenant = null) => new(tenant ?? TenantA, User, role);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    // ---- pure ordering ----------------------------------------------------------------------------------------

    private static RetrievedChunk C(int n, double distance, bool superseded) =>
        new(Id(n), Id(1000 + n), TenantA, $"chunk {n}", distance, superseded);

    [Fact]
    public void Preference_off_is_plain_nearest_first_and_keeps_the_pool_at_k()
    {
        var pref = RankingPreference.None;
        Assert.Equal(5, pref.CandidateCount(5));
        var nearest = new[] { C(1, 0.1, true), C(2, 0.2, false), C(3, 0.3, false) };
        Assert.Equal([Id(1), Id(2)], pref.Rerank(nearest, 2).Select(c => c.Id));
    }

    [Fact]
    public void Strict_preference_puts_every_current_candidate_before_any_superseded()
    {
        var pref = new RankingPreference { PreferCurrentEdition = true };
        var nearest = new[] { C(1, 0.10, true), C(2, 0.11, true), C(3, 0.12, false), C(4, 0.30, false), C(5, 0.31, true) };
        Assert.Equal([Id(3), Id(4), Id(1), Id(2), Id(5)], pref.Rerank(nearest, 5).Select(c => c.Id));
    }

    [Fact]
    public void Superseded_is_demoted_not_hidden_when_there_are_fewer_than_k_current_candidates()
    {
        var pref = new RankingPreference { PreferCurrentEdition = true };
        var nearest = new[] { C(1, 0.10, true), C(2, 0.20, false), C(3, 0.30, true) };
        var top = pref.Rerank(nearest, 3);
        Assert.Equal(3, top.Count);
        Assert.Equal(Id(2), top[0].Id);
        Assert.Contains(top, c => c.Superseded);
    }

    [Fact]
    public void Soft_penalty_lets_a_much_closer_withdrawn_edition_still_win()
    {
        var pref = new RankingPreference { PreferCurrentEdition = true, SupersededPenalty = 0.1 };
        // superseded at 0.05 scores 0.15; current at 0.20 scores 0.20 -> the withdrawn edition stays first.
        var nearest = new[] { C(1, 0.05, true), C(2, 0.20, false), C(3, 0.12, true) };
        Assert.Equal([Id(1), Id(2), Id(3)], pref.Rerank(nearest.OrderBy(c => c.Distance).ToList(), 3).Select(c => c.Id));

        // ...but a current chunk within the penalty of it overtakes it.
        var close = new[] { C(1, 0.05, true), C(2, 0.10, false) };
        Assert.Equal(Id(2), pref.Rerank(close, 2)[0].Id);
    }

    [Fact]
    public void Rerank_never_adds_a_row_to_the_pool()
    {
        var pref = new RankingPreference { PreferCurrentEdition = true };
        var pool = Enumerable.Range(1, 8).Select(n => C(n, n / 10.0, n % 2 == 0)).ToList();
        var ids = pool.Select(c => c.Id).ToHashSet();
        Assert.All(pref.Rerank(pool, 5), c => Assert.Contains(c.Id, ids));
        Assert.Equal(5, pref.Rerank(pool, 5).Count);
    }

    [Fact]
    public void Candidate_pool_is_k_times_multiplier_and_capped()
    {
        var pref = new RankingPreference { PreferCurrentEdition = true, CandidateMultiplier = 4 };
        Assert.Equal(20, pref.CandidateCount(5));
        Assert.Equal(RankingPreference.MaxCandidates, pref.CandidateCount(100));
        Assert.Equal(5, new RankingPreference { PreferCurrentEdition = true, CandidateMultiplier = 1 }.CandidateCount(5));
    }

    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(51, 2.0)]
    [InlineData(4, -0.1)]
    [InlineData(4, double.NaN)]
    public void Invalid_settings_are_rejected(int multiplier, double penalty)
    {
        var pref = new RankingPreference { PreferCurrentEdition = true, CandidateMultiplier = multiplier, SupersededPenalty = penalty };
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => pref.CandidateCount(5));
    }

    [Fact]
    public void Options_default_is_off_and_maps_to_the_none_preference()
    {
        var o = new RetrievalOptions();
        Assert.False(o.PreferCurrentEdition);
        Assert.Same(RankingPreference.None, o.ToRankingPreference());
        o.PreferCurrentEdition = true;
        o.CurrentEditionCandidateMultiplier = 3;
        var p = o.ToRankingPreference();
        Assert.True(p.PreferCurrentEdition);
        Assert.Equal(3, p.CandidateMultiplier);
    }

    // ---- through the service and the in-memory store ------------------------------------------------------------

    /// <summary>Tenant A: 3 withdrawn editions nearest to the query, 1 current edition behind them. Plus walls.</summary>
    private static Rig NearDuplicateRig()
    {
        var rig = new Rig();
        rig.Store.AddChunkWithEmbedding(TenantA, 1, "old 1", AtDistance(0.10, 1), superseded: true, id: Id(1));
        rig.Store.AddChunkWithEmbedding(TenantA, 1, "old 2", AtDistance(0.11, 2), superseded: true, id: Id(2));
        rig.Store.AddChunkWithEmbedding(TenantA, 1, "old 3", AtDistance(0.12, 3), superseded: true, id: Id(3));
        rig.Store.AddChunkWithEmbedding(TenantA, 1, "current", AtDistance(0.13, 4), superseded: false, id: Id(4));
        // The walls: a CURRENT, closer chunk of another tenant and one above the employee's role. Neither may ever
        // appear, preference or not, however attractive a current-edition boost would make them.
        rig.Store.AddChunkWithEmbedding(TenantB, 1, "other tenant current", AtDistance(0.01, 5), superseded: false, id: Id(90));
        rig.Store.AddChunkWithEmbedding(TenantA, 3, "hr only current", AtDistance(0.02, 6), superseded: false, id: Id(91));
        return rig;
    }

    [Fact]
    public async Task Preference_off_returns_the_stale_edition_first_and_on_returns_the_current_edition_first()
    {
        var rig = NearDuplicateRig();
        var off = await rig.Service.QueryAsync(Caller("employee"), "q", k: 2);
        Assert.Equal([Id(1), Id(2)], off.RetrievedChunkIds);

        rig.Options.PreferCurrentEdition = true;
        var on = await rig.Service.QueryAsync(Caller("employee"), "q", k: 2);
        Assert.Equal(Id(4), on.RetrievedChunkIds[0]);
        Assert.Equal(2, on.RetrievedChunkIds.Count);
    }

    [Fact]
    public async Task Preference_never_changes_the_permitted_set()
    {
        var rig = NearDuplicateRig();
        var visible = new HashSet<Guid> { Id(1), Id(2), Id(3), Id(4) }; // what an employee of tenant A may see

        foreach (var role in new[] { "employee", "manager", "hr-admin" })
        {
            foreach (var prefer in new[] { false, true })
            {
                rig.Options.PreferCurrentEdition = prefer;
                // k large enough to drain everything permitted, so the returned set IS the permitted set.
                var all = await rig.Service.QueryAsync(Caller(role), "q", k: 20);
                var expected = role == "hr-admin" ? visible.Append(Id(91)).ToHashSet() : visible;
                Assert.Equal(expected, all.RetrievedChunkIds.ToHashSet());
                Assert.DoesNotContain(Id(90), all.RetrievedChunkIds); // other tenant, even though current and nearest
            }
        }

        // And the employee never gets the above-role chunk even though, being current and close, a preference "wants" it.
        rig.Options.PreferCurrentEdition = true;
        var emp = await rig.Service.QueryAsync(Caller("employee"), "q", k: 20);
        Assert.DoesNotContain(Id(91), emp.RetrievedChunkIds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(10)]
    public async Task Preference_keeps_returned_equal_to_min_k_visible(int k)
    {
        var rig = NearDuplicateRig();
        rig.Options.PreferCurrentEdition = true;
        var result = await rig.Service.QueryAsync(Caller("employee"), "q", k);
        Assert.Equal(Math.Min(k, 4), result.RetrievedChunkIds.Count);
        Assert.Equal(result.RetrievedChunkIds.Count, result.RetrievedChunkIds.Distinct().Count());
    }

    [Fact]
    public async Task A_current_edition_deeper_than_the_pool_is_not_rescued_but_nothing_is_lost()
    {
        // Pool is k * multiplier. With multiplier 1 the preference can only reorder the top k.
        var rig = NearDuplicateRig();
        rig.Options.PreferCurrentEdition = true;
        rig.Options.CurrentEditionCandidateMultiplier = 1;
        var result = await rig.Service.QueryAsync(Caller("employee"), "q", k: 2);
        Assert.Equal([Id(1), Id(2)], result.RetrievedChunkIds); // current is at rank 4, outside a pool of 2
    }

    [Fact]
    public async Task Superseded_only_tenant_still_answers_with_the_superseded_chunks()
    {
        var rig = new Rig();
        rig.Store.AddChunkWithEmbedding(TenantA, 1, "only old", AtDistance(0.1, 1), superseded: true, id: Id(1));
        rig.Options.PreferCurrentEdition = true;
        var result = await rig.Service.QueryAsync(Caller("employee"), "q", k: 5);
        Assert.Equal([Id(1)], result.RetrievedChunkIds);
    }

    [Fact]
    public async Task Ingesting_a_document_as_superseded_marks_its_chunks_for_ranking()
    {
        var rig = new Rig();
        var caller = Caller("hr-admin");
        var accessor = new CallerAccessor();
        accessor.Set(caller);
        var writer = new StoreBackedChunkWriter(rig.Store, accessor);
        var vec = AtDistance(0.1, 1);
        var oldDoc = new MultiTenantRAGagnets.Core.Ingestion.DocumentRecord(Id(500), TenantA, "Policy (superseded)", 1, 1, Superseded: true);
        var newDoc = new MultiTenantRAGagnets.Core.Ingestion.DocumentRecord(Id(501), TenantA, "Policy", 1, 1);
        await writer.StoreAsync(oldDoc, [new(Id(1), Id(500), TenantA, 0, "old", vec, 1)]);
        await writer.StoreAsync(newDoc, [new(Id(2), Id(501), TenantA, 0, "new", AtDistance(0.3, 2), 1)]);

        rig.Options.PreferCurrentEdition = true;
        var result = await rig.Service.QueryAsync(caller, "q", k: 1);
        Assert.Equal([Id(2)], result.RetrievedChunkIds);
    }

    // ---- the SQL, pinned like RetrievalSql.FilteredSearch ----------------------------------------------------------

    [Fact]
    public void Preference_sql_keeps_the_same_predicate_and_index_ordering_inside_the_candidate_query()
    {
        var sql = RetrievalSql.FilteredSearchPreferCurrent;
        Assert.Contains("rag_level_allows(rag_role_level(@role), d.required_level)", sql);
        Assert.Contains("c.tenant_id = @tenant", sql);
        Assert.Contains("JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id", sql);
        Assert.Contains("ORDER BY c.embedding <=> @q", sql);
        Assert.Contains("LIMIT @n", sql);
        Assert.Contains("LIMIT @k", sql);

        // The outer query reads only the candidate set: exactly one FROM chunks and one JOIN documents, both inside.
        Assert.Equal(1, Count(sql, "FROM chunks"));
        Assert.Equal(1, Count(sql, "JOIN documents"));
        Assert.True(sql.IndexOf("FROM chunks", StringComparison.Ordinal) > sql.IndexOf("FROM (", StringComparison.Ordinal));
    }

    [Fact]
    public void Preference_rls_only_sql_adds_no_tenant_or_role_predicate()
    {
        var sql = RetrievalSql.RlsOnlySearchPreferCurrent;
        Assert.DoesNotContain("rag_level_allows", sql);
        Assert.DoesNotContain("tenant_id =", sql);
        Assert.DoesNotContain("@tenant", sql);
        Assert.DoesNotContain("@role", sql);
    }

    [Fact]
    public void Plain_sql_is_unchanged_by_the_preference_work()
    {
        // Off must be byte-for-byte the previous query, so "preference off" is the old behaviour by construction.
        Assert.DoesNotContain("superseded", RetrievalSql.FilteredSearch);
        Assert.DoesNotContain("superseded", RetrievalSql.RlsOnlySearch);
        Assert.Contains("LIMIT @k", RetrievalSql.FilteredSearch);
        Assert.DoesNotContain("@n", RetrievalSql.FilteredSearch);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
