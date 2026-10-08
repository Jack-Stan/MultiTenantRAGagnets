using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Tests.Support;

namespace MultiTenantRAGagnets.Core.Tests;

public class RetrievalServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid User = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private const string HashKey = "test-only-hash-key-0123456789abcdef";

    private sealed class Rig
    {
        public InMemoryChunkStore Store { get; } = new();
        public RecordingChatProvider Chat { get; } = new();
        public RetrievalOptions Options { get; } = new();
        public IQueryHasher Hasher { get; } = new HmacQueryHasher(Options2());
        public RetrievalService Service => new(new FakeEmbeddingProvider(), Chat, Store, Hasher, Microsoft.Extensions.Options.Options.Create(Options));

        private static IOptions<AuditOptions> Options2() => Microsoft.Extensions.Options.Options.Create(new AuditOptions { HashKey = HashKey });

        // Chunk ids are fixed so assertions can name them.
        public static readonly Guid A_Emp = Guid.Parse("11111111-0000-0000-0000-00000000000a");
        public static readonly Guid A_Mgr = Guid.Parse("11111111-0000-0000-0000-00000000000b");
        public static readonly Guid A_Hr = Guid.Parse("11111111-0000-0000-0000-00000000000c");
        public static readonly Guid B_Emp = Guid.Parse("22222222-0000-0000-0000-00000000000a");
        public static readonly Guid B_Hr = Guid.Parse("22222222-0000-0000-0000-00000000000c");

        public Rig()
        {
            // Near-identical text across tenants on purpose: only the walls can tell them apart.
            Store.AddChunk(TenantA, 1, "A-employee: holiday policy is 25 days", A_Emp);
            Store.AddChunk(TenantA, 2, "A-manager: team budget is 40k", A_Mgr);
            Store.AddChunk(TenantA, 3, "A-hr: salary band secret is 90k", A_Hr);
            Store.AddChunk(TenantB, 1, "B-employee: holiday policy is 25 days", B_Emp);
            Store.AddChunk(TenantB, 3, "B-hr: salary band secret is 120k", B_Hr);
        }
    }

    private static CallerContext Caller(string role, Guid? tenant = null) => new(tenant ?? TenantA, User, role);

    [Fact]
    public async Task Employee_only_gets_own_tenant_and_own_level_in_the_prompt()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("employee"), "holiday policy", k: 10);

        Assert.Equal([Rig.A_Emp], result.SentChunkIds);
        Assert.Equal([Rig.A_Emp], result.RetrievedChunkIds);
        var prompt = Assert.Single(rig.Chat.Requests);
        Assert.Equal([Rig.A_Emp.ToString("D")], prompt.Context.Select(c => c.Id));
        Assert.DoesNotContain(rig.Chat.SentTexts, t => t.StartsWith("B-") || t.Contains("secret") || t.Contains("budget"));
    }

    [Fact]
    public async Task Hr_admin_inherits_manager_and_employee_but_never_another_tenant()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("hr-admin"), "anything", k: 10);

        Assert.Equal(new HashSet<Guid> { Rig.A_Emp, Rig.A_Mgr, Rig.A_Hr }, result.SentChunkIds.ToHashSet());
        Assert.DoesNotContain(rig.Chat.SentTexts, t => t.StartsWith("B-"));
    }

    [Fact]
    public async Task Manager_sees_levels_one_and_two_only()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("manager"), "anything", k: 10);

        Assert.Equal(new HashSet<Guid> { Rig.A_Emp, Rig.A_Mgr }, result.SentChunkIds.ToHashSet());
    }

    [Fact]
    public async Task Tenant_B_hr_admin_sees_only_tenant_B()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("hr-admin", TenantB), "salary band secret", k: 10);

        Assert.Equal(new HashSet<Guid> { Rig.B_Emp, Rig.B_Hr }, result.SentChunkIds.ToHashSet());
        Assert.DoesNotContain(rig.Chat.SentTexts, t => t.StartsWith("A-"));
    }

    [Fact]
    public async Task Unknown_role_fails_closed_the_llm_is_never_called_and_the_query_is_still_audited()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("intern"), "salary", k: 10);

        Assert.Empty(result.SentChunkIds);
        Assert.Empty(rig.Chat.Requests);
        Assert.Equal(new RetrievalOptions().NoContextAnswer, result.Answer);
        var row = Assert.Single(rig.Store.AuditRows);
        Assert.Empty(row.RetrievedChunkIds);
        Assert.Empty(row.SentChunkIds);
    }

    [Fact]
    public async Task K_limits_the_number_of_results()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("hr-admin"), "anything", k: 2);

        Assert.Equal(2, result.RetrievedChunkIds.Count);
    }

    [Fact]
    public async Task Nearest_chunk_ranks_first()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("hr-admin"), "A-manager: team budget is 40k", k: 3);

        Assert.Equal(Rig.A_Mgr, result.RetrievedChunkIds[0]);
    }

    [Fact]
    public async Task The_store_is_asked_with_the_callers_tenant_and_role_from_the_token_context()
    {
        var rig = new Rig();
        var caller = Caller("manager");

        await rig.Service.QueryAsync(caller, "x");

        Assert.Equal(caller, Assert.Single(rig.Store.BeginCalls));
    }

    [Fact]
    public async Task Citations_are_restricted_to_chunks_that_were_actually_sent()
    {
        var rig = new Rig();
        rig.Chat.ExtraCitations.Add(Rig.B_Hr.ToString("D")); // model "cites" a foreign chunk
        rig.Chat.ExtraCitations.Add(Guid.NewGuid().ToString("D")); // and a made-up one
        rig.Chat.ExtraCitations.Add("not-a-guid");

        var result = await rig.Service.QueryAsync(Caller("employee"), "holiday", k: 10);

        Assert.Equal([Rig.A_Emp], result.CitedChunkIds);
    }

    [Fact]
    public async Task Prompt_budget_trims_what_is_sent_but_retrieved_and_sent_are_audited_separately()
    {
        var rig = new Rig();
        rig.Options.MaxContextChars = 50; // fits roughly one chunk

        var result = await rig.Service.QueryAsync(Caller("hr-admin"), "A-hr: salary band secret is 90k", k: 10);

        Assert.True(result.RetrievedChunkIds.Count > result.SentChunkIds.Count);
        Assert.NotEmpty(result.SentChunkIds);
        Assert.All(result.SentChunkIds, id => Assert.Contains(id, result.RetrievedChunkIds));
        var row = Assert.Single(rig.Store.AuditRows);
        Assert.Equal(result.RetrievedChunkIds, row.RetrievedChunkIds);
        Assert.Equal(result.SentChunkIds, row.SentChunkIds);
        Assert.Equal(result.SentChunkIds, rig.Chat.Requests.Single().Context.Select(c => Guid.Parse(c.Id)));
    }

    [Fact]
    public async Task Exactly_one_audit_row_per_query_with_who_hash_model_and_latency_and_no_raw_text()
    {
        var rig = new Rig();
        const string question = "what is the secret salary band?";

        var result = await rig.Service.QueryAsync(Caller("manager"), question, k: 10);

        var row = Assert.Single(rig.Store.AuditRows);
        Assert.Equal(TenantA, row.TenantId);
        Assert.Equal(User, row.UserId);
        Assert.Equal("manager", row.Role);
        Assert.Equal(rig.Hasher.Hash(question), row.QueryHash);
        Assert.Equal(rig.Chat.ModelName, row.Model);
        Assert.True(row.LatencyMs >= 0);
        Assert.Equal(result.LatencyMs, row.LatencyMs);
        Assert.NotEqual(default, row.OccurredAt);
        // Nothing in the row (the AuditRecord type has no query-text field at all) contains the raw question.
        Assert.DoesNotContain(question, row.ToString());
        Assert.DoesNotContain(typeof(AuditRecord).GetProperties(), p => p.Name.Contains("QueryText", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(AuditEntry).GetProperties(), p => p.Name.Contains("QueryText", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Audit_integrity_cited_is_a_subset_of_sent_which_is_a_subset_of_retrieved_all_matching_the_row()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryAsync(Caller("hr-admin"), "holiday", k: 10);

        var row = Assert.Single(rig.Store.AuditRows);
        Assert.All(result.CitedChunkIds, c => Assert.Contains(c, row.SentChunkIds));
        Assert.All(row.SentChunkIds, s => Assert.Contains(s, row.RetrievedChunkIds));
    }

    [Fact]
    public async Task A_foreign_tenant_chunk_on_the_filtered_path_aborts_before_the_llm_and_writes_nothing()
    {
        var rig = new Rig();
        rig.Store.IgnoreTenantFilterEvenWhenAsked = true; // misbehaving store
        rig.Store.RlsActive = false;

        await Assert.ThrowsAsync<TenantBoundaryViolationException>(() =>
            rig.Service.QueryAsync(Caller("hr-admin"), "holiday policy", k: 10));

        Assert.Empty(rig.Chat.Requests);
        Assert.Empty(rig.Store.AuditRows);
        Assert.Equal(0, rig.Store.CommitCount);
    }

    [Fact]
    public async Task App_filter_alone_holds_when_the_rls_policy_has_been_dropped()
    {
        var rig = new Rig();
        rig.Store.RlsActive = false;

        var result = await rig.Service.QueryAsync(Caller("employee"), "holiday policy", k: 10);

        Assert.Equal([Rig.A_Emp], result.SentChunkIds);
    }

    [Fact]
    public async Task RlsOnly_seam_with_rls_active_still_leaks_nothing()
    {
        var rig = new Rig();

        var result = await rig.Service.QueryWithoutAppFilterForTestingAsync(Caller("employee"), "holiday policy", k: 10);

        Assert.Equal([Rig.A_Emp], result.SentChunkIds);
    }

    [Fact]
    public async Task RlsOnly_seam_with_the_policy_dropped_DOES_leak_proving_the_negative_control_can_fail()
    {
        var rig = new Rig();
        rig.Store.RlsActive = false; // the deliberately-dropped-policy run

        var result = await rig.Service.QueryWithoutAppFilterForTestingAsync(Caller("employee"), "holiday policy", k: 10);

        Assert.Contains(Rig.B_Emp, result.SentChunkIds);
        Assert.Contains(Rig.A_Hr, result.SentChunkIds);
    }

    [Fact]
    public async Task Chat_failure_is_audited_then_rethrown()
    {
        var rig = new Rig();
        rig.Chat.ThrowOnComplete = new HttpRequestException("boom");

        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Service.QueryAsync(Caller("employee"), "holiday"));

        var row = Assert.Single(rig.Store.AuditRows);
        Assert.Equal([Rig.A_Emp], row.SentChunkIds);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_and_writes_no_audit()
    {
        var rig = new Rig();
        rig.Chat.ThrowOnComplete = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Service.QueryAsync(Caller("employee"), "holiday"));

        Assert.Empty(rig.Store.AuditRows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_question_is_rejected(string question)
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.QueryAsync(Caller("employee"), question));
        Assert.Empty(rig.Store.BeginCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-1)]
    public async Task K_out_of_range_is_rejected(int k)
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Service.QueryAsync(Caller("employee"), "x", k));
    }

    [Fact]
    public async Task Overlong_question_is_rejected()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.QueryAsync(Caller("employee"), new string('x', 2001)));
    }

    [Fact]
    public async Task Empty_tenant_in_the_caller_is_rejected()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.QueryAsync(new CallerContext(Guid.Empty, User, "employee"), "x"));
    }

    [Fact]
    public void Filtered_sql_uses_the_shared_rls_functions_and_the_tenant_predicate()
    {
        var sql = RetrievalSql.FilteredSearch;
        Assert.Contains("rag_level_allows(", sql);
        Assert.Contains("rag_role_level(", sql);
        Assert.Contains("c.tenant_id = @tenant", sql);
        Assert.Contains("JOIN documents d", sql);
        Assert.Contains("ORDER BY c.embedding <=> @q", sql);
        Assert.Contains("LIMIT @k", sql);
    }

    [Fact]
    public void RlsOnly_sql_has_no_where_clause()
    {
        Assert.DoesNotContain("WHERE", RetrievalSql.RlsOnlySearch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rag_level_allows", RetrievalSql.RlsOnlySearch);
    }

    [Fact]
    public void Datasource_factory_refuses_no_reset_on_close_and_empty_strings()
    {
        Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.Create(""));
        Assert.Throws<InvalidOperationException>(() =>
            NpgsqlDataSourceFactory.Create("Host=localhost;Database=x;Username=app_user;No Reset On Close=true"));
        using var ok = NpgsqlDataSourceFactory.Create("Host=localhost;Database=x;Username=app_user"); // builds lazily, never connects
        Assert.NotNull(ok);
    }
}
