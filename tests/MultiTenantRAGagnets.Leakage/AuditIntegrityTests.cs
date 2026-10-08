using Npgsql;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// IMPLEMENTATION_PLAN step 13 / TESTING.md section 5, service logic only. No database: an in-memory store behind a
/// spy that records exactly what the service asked it to audit. These RUN on a laptop. They prove the
/// service's behaviour; the same rules against the real audit_log table are in <see cref="AuditIntegrityDbTests"/>.
/// </summary>
public class AuditLogicTests
{
    private const int K = LeakageFixture.K;
    private static readonly SyntheticCorpus Corpus = CorpusGenerator.Generate();
    private static readonly CorpusOracle Oracle = new(Corpus);

    private static (SpyStore Spy, RecordingChatProvider Chat, RetrievalService Service, string Key) Wire(Action<RetrievalOptions>? configure = null)
    {
        var spy = new SpyStore(new ModelStore(Corpus));
        var chat = new RecordingChatProvider();
        var key = AuditChecks.NewKey();
        return (spy, chat, AuditChecks.BuildService(spy, chat, key, configure), key);
    }

    [Fact]
    public void Independent_hash_helper_agrees_with_the_production_hasher_and_is_keyed()
    {
        var key = AuditChecks.NewKey();
        var hasher = new HmacQueryHasher(Microsoft.Extensions.Options.Options.Create(new AuditOptions { HashKey = key }));
        var other = new HmacQueryHasher(Microsoft.Extensions.Options.Options.Create(new AuditOptions { HashKey = AuditChecks.NewKey() }));

        Assert.Equal(AuditChecks.ExpectedHash(key, "how many days of leave"), hasher.Hash("how many days of leave"));
        Assert.NotEqual(hasher.Hash("how many days of leave"), other.Hash("how many days of leave"));          // keyed
        Assert.NotEqual(AuditChecks.PlainSha256("how many days of leave"), hasher.Hash("how many days of leave")); // not plain SHA-256
        Assert.NotEqual(hasher.Hash("a"), hasher.Hash("A"));                                                    // no normalisation
    }

    [Fact]
    public async Task The_consistency_checker_is_fallible_each_kind_of_tampering_is_caught()
    {
        // A checker that never fails proves nothing: feed it deliberately corrupted audit records.
        var (spy, chat, service, key) = Wire();
        var caller = Oracle.CallerFor("acme", Roles.Manager);
        var question = $"{AuditChecks.NewMarker()} annual leave";
        var result = await service.QueryAsync(caller, question, K);
        var good = AuditView.From(Assert.Single(spy.Entries));
        AuditChecks.AssertConsistent("pristine", caller, question, key, result, good, chat.Requests); // control: passes

        var stray = Guid.NewGuid();
        var tampered = new Dictionary<string, AuditView>
        {
            ["wrong tenant"] = good with { TenantId = Guid.NewGuid() },
            ["wrong user"] = good with { UserId = Guid.NewGuid() },
            ["wrong role"] = good with { Role = Roles.Employee },
            ["wrong hash"] = good with { QueryHash = new string('0', 64) },
            ["unkeyed hash"] = good with { QueryHash = AuditChecks.PlainSha256(question) },
            ["sent not retrieved"] = good with { Sent = good.Sent.Append(stray).ToArray() },
            ["retrieved differs from response"] = good with { Retrieved = good.Retrieved.Reverse().ToArray() },
            ["sent differs from what the LLM saw"] = good with { Sent = good.Sent.Take(good.Sent.Length - 1).ToArray() },
            ["wrong model"] = good with { Model = "other" },
        };
        foreach (var (name, bad) in tampered)
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AuditChecks.AssertConsistent(name, caller, question, key, result, bad, chat.Requests));
        }

        // A response that cites something never sent.
        var forged = result with { CitedChunkIds = [.. result.CitedChunkIds, stray] };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AuditChecks.AssertConsistent("cited not sent", caller, question, key, forged, good, chat.Requests));
    }

    [Fact]
    public async Task Every_query_by_every_tenant_and_role_appends_exactly_one_consistent_entry()
    {
        var (spy, chat, service, key) = Wire();

        foreach (var tenant in Corpus.Tenants)
        foreach (var role in Roles.All)
        {
            var caller = Oracle.CallerFor(tenant.Slug, role);
            foreach (var question in new[]
            {
                $"{AuditChecks.NewMarker()} how many days of annual leave",
                // pull towards a chunk this caller may not be allowed to see
                $"{AuditChecks.NewMarker()} {Corpus.Chunks.First(c => c.TenantSlug == tenant.Slug && c.RequiredLevel == 3).Text}",
            })
            {
                var entriesBefore = spy.Entries.Count;
                var commitsBefore = spy.Commits;
                chat.Reset();

                var result = await service.QueryAsync(caller, question, K);

                var label = $"{tenant.Slug}/{role}";
                Assert.True(spy.Entries.Count - entriesBefore == 1, $"{label}: expected exactly one audit entry per query");
                Assert.True(spy.Commits - commitsBefore == 1, $"{label}: the audit entry must be committed with the query");
                var audit = AuditView.From(spy.Entries[^1]);

                AuditChecks.AssertConsistent(label, caller, question, key, result, audit, chat.Requests);
                Assert.Equal(K, audit.Retrieved.Length);

                var visible = Oracle.VisibleChunks(tenant.Slug, role);
                Assert.True(audit.Retrieved.All(visible.Contains), $"{label}: audit records a retrieved chunk the caller may not see");
            }
        }
    }

    [Fact]
    public async Task Audit_entry_never_carries_the_query_text()
    {
        var (spy, _, service, _) = Wire();
        var marker = AuditChecks.NewMarker();
        await service.QueryAsync(Oracle.CallerFor("acme", Roles.Employee), $"{marker} annual leave", K);

        var entry = Assert.Single(spy.Entries);
        Assert.DoesNotContain(marker, System.Text.Json.JsonSerializer.Serialize(entry));
        Assert.DoesNotContain(typeof(AuditEntry).GetProperties(), p => p.Name.Contains("Text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_the_context_budget_trims_the_prompt_retrieved_is_strictly_larger_than_sent()
    {
        var question = $"{AuditChecks.NewMarker()} how many days of annual leave";
        var caller = Oracle.CallerFor("acme", Roles.Manager);

        // 1. Untrimmed reference run.
        var (spy1, chat1, service1, key1) = Wire();
        var full = await service1.QueryAsync(caller, question, K);
        var fullAudit = AuditView.From(Assert.Single(spy1.Entries));
        AuditChecks.AssertConsistent("untrimmed", caller, question, key1, full, fullAudit, chat1.Requests);
        Assert.Equal(K, fullAudit.Sent.Length);
        Assert.Equal(fullAudit.Retrieved, fullAudit.Sent);

        // 2. Budget for exactly the first two chunks.
        var texts = chat1.Requests[0].Context.Select(c => c.Text.Length).ToArray();
        var (spy2, chat2, service2, key2) = Wire(o => o.MaxContextChars = texts[0] + texts[1]);
        var two = await service2.QueryAsync(caller, question, K);
        var twoAudit = AuditView.From(Assert.Single(spy2.Entries));
        AuditChecks.AssertConsistent("trimmed-to-2", caller, question, key2, two, twoAudit, chat2.Requests);
        Assert.Equal(fullAudit.Retrieved, twoAudit.Retrieved);          // same search
        Assert.Equal(2, twoAudit.Sent.Length);
        Assert.True(twoAudit.Retrieved.Length > twoAudit.Sent.Length, "trimmed case: retrieved must be strictly larger than sent");
        Assert.Equal(fullAudit.Retrieved.Take(2), twoAudit.Sent);       // the top-ranked two survive
        var trimmedOff = twoAudit.Retrieved.Except(twoAudit.Sent).ToArray();
        Assert.Equal(K - 2, trimmedOff.Length);
        Assert.All(chat2.Requests[0].Context, c => Assert.DoesNotContain(Guid.Parse(c.Id), trimmedOff));
        Assert.All(two.CitedChunkIds, c => Assert.DoesNotContain(c, trimmedOff));
        Assert.True(two.CitedChunkIds.Count < twoAudit.Retrieved.Length);

        // 3. Smallest budget: the top chunk is always kept, never an empty prompt.
        var (spy3, chat3, service3, key3) = Wire(o => o.MaxContextChars = 1);
        var one = await service3.QueryAsync(caller, question, K);
        var oneAudit = AuditView.From(Assert.Single(spy3.Entries));
        AuditChecks.AssertConsistent("trimmed-to-1", caller, question, key3, one, oneAudit, chat3.Requests);
        Assert.Single(oneAudit.Sent);
        Assert.Equal(fullAudit.Retrieved[0], oneAudit.Sent[0]);
        Assert.Equal(K, oneAudit.Retrieved.Length);
    }

    [Fact]
    public async Task A_citation_the_model_invented_is_dropped_and_never_reaches_the_response()
    {
        var spy = new SpyStore(new ModelStore(Corpus));
        var chat = new InventingChatProvider();
        var service = AuditChecks.BuildService(spy, chat, AuditChecks.NewKey());

        var result = await service.QueryAsync(Oracle.CallerFor("acme", Roles.Employee), $"{AuditChecks.NewMarker()} annual leave", K);

        Assert.DoesNotContain(chat.InventedId, result.CitedChunkIds);
        var entry = Assert.Single(spy.Entries);
        Assert.Single(result.CitedChunkIds);
        Assert.Contains(result.CitedChunkIds[0], entry.SentChunkIds);
    }

    [Fact]
    public async Task A_failing_llm_call_still_leaves_one_committed_audit_entry_and_the_error_reaches_the_caller()
    {
        var spy = new SpyStore(new ModelStore(Corpus));
        var chat = new ThrowingChatProvider();
        var key = AuditChecks.NewKey();
        var service = AuditChecks.BuildService(spy, chat, key);
        var caller = Oracle.CallerFor("globex", Roles.Manager);
        var question = $"{AuditChecks.NewMarker()} annual leave";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(caller, question, K));
        Assert.Equal("simulated LLM outage", ex.Message);

        Assert.Single(chat.Requests); // the LLM really was attempted
        var entry = Assert.Single(spy.Entries);
        Assert.Equal(1, spy.Commits);
        var audit = AuditView.From(entry);
        Assert.Equal(caller.TenantId, audit.TenantId);
        Assert.Equal(caller.UserId, audit.UserId);
        Assert.Equal(AuditChecks.ExpectedHash(key, question), audit.QueryHash);
        Assert.Equal(K, audit.Retrieved.Length);
        Assert.NotEmpty(audit.Sent);
        Assert.Equal(ThrowingChatProvider.Name, audit.Model);
        Assert.True(audit.Sent.All(audit.Retrieved.Contains));
    }

    [Fact]
    public async Task When_the_audit_insert_fails_there_is_no_answer_and_no_commit()
    {
        var spy = new SpyStore(new ModelStore(Corpus)) { FailAppendWith = new InvalidOperationException("simulated audit store outage") };
        var chat = new RecordingChatProvider();
        var service = AuditChecks.BuildService(spy, chat, AuditChecks.NewKey());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryAsync(Oracle.CallerFor("acme", Roles.HrAdmin), $"{AuditChecks.NewMarker()} annual leave", K));

        Assert.Equal("simulated audit store outage", ex.Message); // the caller gets an error, not an unaudited answer
        Assert.Equal(0, spy.Commits);                              // nothing committed
        Assert.Equal(1, spy.Rollbacks);                            // the transaction was rolled back, not left open
        Assert.Empty(spy.Entries);
    }

    [Fact]
    public async Task A_query_with_nothing_permitted_is_still_audited_and_never_calls_the_llm()
    {
        var (spy, chat, service, key) = Wire();
        var stranger = new CallerContext(Guid.NewGuid(), Guid.NewGuid(), Roles.HrAdmin); // a tenant with no data
        var question = $"{AuditChecks.NewMarker()} annual leave";

        var result = await service.QueryAsync(stranger, question, K);

        Assert.Equal(new RetrievalOptions().NoContextAnswer, result.Answer);
        Assert.Empty(result.RetrievedChunkIds);
        Assert.Empty(result.CitedChunkIds);
        Assert.Empty(chat.Requests);
        var audit = AuditView.From(Assert.Single(spy.Entries));
        AuditChecks.AssertConsistent("empty", stranger, question, key, result, audit, chat.Requests);
        Assert.Empty(audit.Retrieved);
        Assert.Empty(audit.Sent);
    }
}

/// <summary>
/// IMPLEMENTATION_PLAN step 13 / TESTING.md section 5 against the REAL audit_log table, through the real
/// RetrievalService and NpgsqlChunkStore as app_user. UNVERIFIED locally (no Postgres): skipped here, must run in CI.
/// Audit rows are READ as the owner so the test sees what is truly stored.
/// </summary>
[Collection(LeakageCollection.Name)]
public class AuditIntegrityDbTests(LeakageFixture fx)
{
    private const int K = LeakageFixture.K;

    private (RetrievalService Service, RecordingChatProvider Chat, string Key) Wire(Action<RetrievalOptions>? configure = null)
    {
        var chat = new RecordingChatProvider();
        var key = AuditChecks.NewKey();
        return (AuditChecks.BuildService(new NpgsqlChunkStore(fx.AppDs), chat, key, configure), chat, key);
    }

    [DbFact]
    public async Task One_audit_row_per_query_for_all_roles_and_tenants_and_it_matches_the_caller_the_response_and_the_llm()
    {
        var (service, chat, key) = Wire();

        foreach (var tenant in fx.Corpus.Tenants)
        foreach (var role in Roles.All)
        {
            var caller = fx.Oracle.CallerFor(tenant.Slug, role);
            var hrChunk = fx.Corpus.Chunks.First(c => c.TenantSlug == tenant.Slug && c.RequiredLevel == 3);
            foreach (var withBait in new[] { false, true })
            {
                var marker = AuditChecks.NewMarker();
                var question = withBait ? $"{marker} {hrChunk.Text}" : $"{marker} how many days of annual leave";
                chat.Reset();

                var (result, row) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, service, caller, question, marker, K, key);

                var label = $"{tenant.Slug}/{role}/{(withBait ? "bait" : "natural")}";
                AuditChecks.AssertConsistent(label, caller, question, key, result, row.View, chat.Requests);
                Assert.Equal(K, row.View.Retrieved.Length);

                var visible = fx.Oracle.VisibleChunks(tenant.Slug, role);
                Assert.True(row.View.Retrieved.All(visible.Contains), $"{label}: the stored audit row lists a retrieved chunk this caller may not see");
                if (withBait && Roles.LevelOf(role) < 3)
                    Assert.DoesNotContain(hrChunk.Id, row.View.Retrieved);
            }
        }
    }

    [DbFact]
    public async Task Two_identical_questions_make_two_rows_with_the_same_hash_and_different_ids()
    {
        var (service, _, key) = Wire();
        var caller = fx.Oracle.CallerFor("acme", Roles.Employee);
        var marker = AuditChecks.NewMarker();
        var question = $"{marker} annual leave";

        var before = await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId);
        await service.QueryAsync(caller, question, K);
        await service.QueryAsync(caller, question, K);

        Assert.Equal(before + 2, await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId));
        var rows = await AuditDb.RowsByHashAsync(fx.OwnerDs, caller.TenantId, AuditChecks.ExpectedHash(key, question));
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].Id, rows[1].Id);
    }

    [DbFact]
    public async Task Trimmed_context_shows_retrieved_strictly_larger_than_sent_in_the_stored_row()
    {
        var caller = fx.Oracle.CallerFor("acme", Roles.Manager);
        var marker = AuditChecks.NewMarker();
        var question = $"{marker} how many days of annual leave";

        var (fullService, fullChat, fullKey) = Wire();
        var (fullResult, fullRow) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, fullService, caller, question, marker, K, fullKey);
        AuditChecks.AssertConsistent("untrimmed", caller, question, fullKey, fullResult, fullRow.View, fullChat.Requests);
        Assert.Equal(K, fullRow.View.Sent.Length);

        var sizes = fullChat.Requests[0].Context.Select(c => c.Text.Length).ToArray();
        var (trimService, trimChat, trimKey) = Wire(o => o.MaxContextChars = sizes[0] + sizes[1]);
        var (trimResult, trimRow) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, trimService, caller, question, marker, K, trimKey);
        AuditChecks.AssertConsistent("trimmed", caller, question, trimKey, trimResult, trimRow.View, trimChat.Requests);

        Assert.Equal(fullRow.View.Retrieved, trimRow.View.Retrieved); // same search, same order
        Assert.Equal(2, trimRow.View.Sent.Length);
        Assert.True(trimRow.View.Retrieved.Length > trimRow.View.Sent.Length, "retrieved must be strictly larger than sent when the budget trims");
        Assert.Equal(trimRow.View.Retrieved.Take(2), trimRow.View.Sent);
        var trimmedOff = trimRow.View.Retrieved.Except(trimRow.View.Sent).ToArray();
        Assert.All(trimChat.Requests[0].Context, c => Assert.DoesNotContain(Guid.Parse(c.Id), trimmedOff));
        Assert.All(trimResult.CitedChunkIds, c => Assert.DoesNotContain(c, trimmedOff));

        var (oneService, oneChat, oneKey) = Wire(o => o.MaxContextChars = 1);
        var (oneResult, oneRow) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, oneService, caller, question, marker, K, oneKey);
        AuditChecks.AssertConsistent("trimmed-to-1", caller, question, oneKey, oneResult, oneRow.View, oneChat.Requests);
        Assert.Single(oneRow.View.Sent);
        Assert.Equal(K, oneRow.View.Retrieved.Length);
    }

    [DbFact]
    public async Task Query_with_no_permitted_chunks_is_audited_with_empty_arrays_and_the_llm_is_not_called()
    {
        // A brand-new tenant with a user and no documents. Created and removed as the owner; app_user cannot write users/tenants.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var c = await fx.OwnerDs.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO tenants (id, slug, name) VALUES (@t, @slug, 'audit empty');
                INSERT INTO users (id, tenant_id, email, role) VALUES (@u, @t, @email, 'employee');
                """, c);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("slug", "audit-empty-" + tenantId.ToString("N")[..8]);
            cmd.Parameters.AddWithValue("email", $"employee@{tenantId:N}.test");
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var (service, chat, key) = Wire();
            var caller = new CallerContext(tenantId, userId, Roles.Employee);
            var marker = AuditChecks.NewMarker();
            var question = $"{marker} annual leave";

            var (result, row) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, service, caller, question, marker, K, key);

            Assert.Equal(new RetrievalOptions().NoContextAnswer, result.Answer);
            Assert.Empty(chat.Requests);
            AuditChecks.AssertConsistent("empty-tenant", caller, question, key, result, row.View, chat.Requests);
            Assert.Empty(row.View.Retrieved);
            Assert.Empty(row.View.Sent);
        }
        finally
        {
            await using var c = await fx.OwnerDs.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                """
                DELETE FROM audit_log WHERE tenant_id = @t;
                DELETE FROM users WHERE tenant_id = @t;
                DELETE FROM tenants WHERE id = @t;
                """, c);
            cmd.Parameters.AddWithValue("t", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [DbFact]
    public async Task A_failing_llm_call_still_leaves_exactly_one_audit_row_in_the_database()
    {
        var chat = new ThrowingChatProvider();
        var key = AuditChecks.NewKey();
        var service = AuditChecks.BuildService(new NpgsqlChunkStore(fx.AppDs), chat, key);
        var caller = fx.Oracle.CallerFor("acme", Roles.Employee);
        var marker = AuditChecks.NewMarker();
        var question = $"{marker} annual leave";

        var before = await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(caller, question, K));
        Assert.Equal("simulated LLM outage", ex.Message);

        Assert.Equal(before + 1, await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId));
        var row = Assert.Single(await AuditDb.RowsByHashAsync(fx.OwnerDs, caller.TenantId, AuditChecks.ExpectedHash(key, question)));
        Assert.Single(chat.Requests);
        Assert.Equal(caller.UserId, row.View.UserId);
        Assert.Equal(caller.Role, row.View.Role);
        Assert.Equal(ThrowingChatProvider.Name, row.View.Model);
        Assert.Equal(K, row.View.Retrieved.Length);
        Assert.NotEmpty(row.View.Sent);
        Assert.True(row.View.Sent.All(row.View.Retrieved.Contains));
        Assert.Null(row.QueryText);
        Assert.DoesNotContain(marker, row.Json);
    }

    [DbFact]
    public async Task When_the_audit_insert_is_rejected_by_the_database_the_caller_gets_an_error_and_no_row_exists()
    {
        // A real constraint failure: the caller's user id is not a user of that tenant, so the composite FK
        // audit_log(user_id, tenant_id) -> users(id, tenant_id) rejects the INSERT.
        var chat = new RecordingChatProvider();
        var key = AuditChecks.NewKey();
        var service = AuditChecks.BuildService(new NpgsqlChunkStore(fx.AppDs), chat, key);
        var real = fx.Oracle.CallerFor("acme", Roles.Employee);
        var ghost = real with { UserId = Guid.NewGuid() };
        var question = $"{AuditChecks.NewMarker()} annual leave";

        var before = await AuditDb.CountAsync(fx.OwnerDs, ghost.TenantId);
        QueryResult? answer = null;
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => answer = await service.QueryAsync(ghost, question, K));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Null(answer);
        Assert.Equal(before, await AuditDb.CountAsync(fx.OwnerDs, ghost.TenantId));
        Assert.Empty(await AuditDb.RowsByHashAsync(fx.OwnerDs, ghost.TenantId, AuditChecks.ExpectedHash(key, question)));
    }

    [DbFact]
    public async Task When_the_audit_append_throws_nothing_is_committed_and_no_row_exists()
    {
        var spy = new SpyStore(new NpgsqlChunkStore(fx.AppDs)) { FailAppendWith = new InvalidOperationException("simulated audit store outage") };
        var chat = new RecordingChatProvider();
        var key = AuditChecks.NewKey();
        var service = AuditChecks.BuildService(spy, chat, key);
        var caller = fx.Oracle.CallerFor("globex", Roles.HrAdmin);
        var question = $"{AuditChecks.NewMarker()} annual leave";

        var before = await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(caller, question, K));

        Assert.Equal("simulated audit store outage", ex.Message);
        Assert.Equal(0, spy.Commits);
        Assert.Equal(1, spy.Rollbacks);
        Assert.Equal(before, await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId));
        Assert.Empty(await AuditDb.RowsByHashAsync(fx.OwnerDs, caller.TenantId, AuditChecks.ExpectedHash(key, question)));
    }
}

/// <summary>
/// TESTING.md section 5, "Append-only": UPDATE or DELETE on audit_log as the app role is rejected because the role has
/// no such grant, not because the app happens to avoid it. Plus the database-side invariants on forged rows.
/// UNVERIFIED locally: needs Postgres.
/// </summary>
[Collection(LeakageCollection.Name)]
public class AuditAppendOnlyDbTests(LeakageFixture fx)
{
    private async Task<(CallerContext Caller, long RowId)> MakeOneAuditRowAsync(string tenantSlug = "acme", string role = Roles.HrAdmin)
    {
        var chat = new RecordingChatProvider();
        var key = AuditChecks.NewKey();
        var service = AuditChecks.BuildService(new NpgsqlChunkStore(fx.AppDs), chat, key);
        var caller = fx.Oracle.CallerFor(tenantSlug, role);
        var marker = AuditChecks.NewMarker();
        var (_, row) = await AuditDb.QueryAndReadAuditAsync(fx.OwnerDs, service, caller, $"{marker} annual leave", marker, LeakageFixture.K, key);
        return (caller, row.Id);
    }

    private static async Task AssertPermissionDeniedAsync(NpgsqlDataSource app, Guid tenantId, string role, string sql)
    {
        await using var raw = await RawTx.BeginAsync(app, tenantId, role);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => raw.ExecAsync(sql));
        Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege && ex.MessageText.Contains("permission denied", StringComparison.OrdinalIgnoreCase),
            $"'{sql}' as {role}: expected 42501 permission denied (no grant), got {ex.SqlState}: {ex.MessageText}");
    }

    [DbFact]
    public async Task App_user_has_no_update_delete_or_truncate_grant_on_audit_log()
    {
        await using var c = await fx.OwnerDs.OpenConnectionAsync();
        async Task<bool> Has(string priv)
        {
            await using var cmd = new NpgsqlCommand($"SELECT has_table_privilege('app_user', 'public.audit_log', '{priv}')", c);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.True(await Has("INSERT"), "app_user must be able to append");
        Assert.True(await Has("SELECT"), "app_user must be able to read its tenant's rows");
        Assert.False(await Has("UPDATE"), "app_user must NOT have UPDATE on audit_log");
        Assert.False(await Has("DELETE"), "app_user must NOT have DELETE on audit_log");
        Assert.False(await Has("TRUNCATE"), "app_user must NOT have TRUNCATE on audit_log");

        await using var any = new NpgsqlCommand("SELECT has_any_column_privilege('app_user', 'public.audit_log', 'UPDATE')", c);
        Assert.False((bool)(await any.ExecuteScalarAsync())!, "app_user must not hold a column-level UPDATE on any audit_log column");
    }

    [DbFact]
    public async Task App_user_update_delete_and_truncate_on_audit_log_are_rejected_for_every_role_and_the_row_survives_untouched()
    {
        foreach (var role in Roles.All)
        {
            var (caller, id) = await MakeOneAuditRowAsync("acme", role);
            var before = await AuditDb.RowJsonAsync(fx.OwnerDs, id);
            var countBefore = await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId);

            // The row is visible to this caller under RLS, so a "0 rows affected" would be the symptom of RLS hiding it;
            // permission denied is the symptom of the missing grant. We require the latter.
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, "UPDATE audit_log SET model = 'tampered'");
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, $"UPDATE audit_log SET sent_chunk_ids = '{{}}', retrieved_chunk_ids = '{{}}' WHERE id = {id}");
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, $"UPDATE audit_log SET query_text = 'sneaky' WHERE id = {id}");
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, "DELETE FROM audit_log");
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, $"DELETE FROM audit_log WHERE id = {id}");
            await AssertPermissionDeniedAsync(fx.AppDs, caller.TenantId, role, "TRUNCATE audit_log");

            Assert.Equal(before, await AuditDb.RowJsonAsync(fx.OwnerDs, id));
            Assert.Equal(countBefore, await AuditDb.CountAsync(fx.OwnerDs, caller.TenantId));
        }
    }

    [DbFact]
    public async Task App_user_cannot_forge_or_corrupt_audit_rows_the_database_rejects_them()
    {
        var acme = fx.Oracle.CallerFor("acme", Roles.HrAdmin);
        var globex = fx.Oracle.CallerFor("globex", Roles.HrAdmin);
        var someChunk = fx.Corpus.Chunks.First(c => c.TenantSlug == "acme").Id;
        var otherChunk = fx.Corpus.Chunks.Last(c => c.TenantSlug == "acme").Id;

        string Insert(Guid tenant, Guid user, string retrieved, string sent) =>
            $"INSERT INTO audit_log (tenant_id, user_id, role, query_hash, retrieved_chunk_ids, sent_chunk_ids, model, latency_ms) " +
            $"VALUES ('{tenant}', '{user}', 'hr-admin', 'forged', {retrieved}, {sent}, 'm', 1)";
        string Arr(params Guid[] ids) => ids.Length == 0 ? "'{}'::uuid[]" : "ARRAY[" + string.Join(",", ids.Select(i => $"'{i}'")) + "]::uuid[]";

        // 0. Control: a well-formed row for the caller's own tenant and user IS accepted (so the denials below are not just a broken statement).
        await using (var ok = await RawTx.BeginAsync(fx.AppDs, acme.TenantId, acme.Role))
        {
            await ok.ExecAsync(Insert(acme.TenantId, acme.UserId, Arr(someChunk), Arr(someChunk)));
        }

        // 1. A row stamped with another tenant: RLS WITH CHECK.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme.TenantId, acme.Role))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => raw.ExecAsync(Insert(globex.TenantId, globex.UserId, Arr(), Arr())));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            Assert.Contains("row-level security", ex.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Own tenant but another tenant's user: composite FK.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme.TenantId, acme.Role))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => raw.ExecAsync(Insert(acme.TenantId, globex.UserId, Arr(), Arr())));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }

        // 3. Sent something that was never retrieved: the table CHECK.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme.TenantId, acme.Role))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => raw.ExecAsync(Insert(acme.TenantId, acme.UserId, Arr(someChunk), Arr(otherChunk))));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [DbFact]
    public async Task App_user_cannot_read_another_tenants_audit_rows()
    {
        var (acme, _) = await MakeOneAuditRowAsync("acme", Roles.HrAdmin);
        var (globex, _) = await MakeOneAuditRowAsync("globex", Roles.HrAdmin);
        Assert.True(await AuditDb.CountAsync(fx.OwnerDs, globex.TenantId) > 0, "precondition: globex has audit rows");

        await using var raw = await RawTx.BeginAsync(fx.AppDs, acme.TenantId, acme.Role);
        Assert.Equal("0", await raw.ScalarStringAsync($"SELECT count(*) FROM audit_log WHERE tenant_id = '{globex.TenantId}'"));
        var own = long.Parse(await raw.ScalarStringAsync("SELECT count(*) FROM audit_log"));
        Assert.Equal(await AuditDb.CountAsync(fx.OwnerDs, acme.TenantId), own);

        // And through the product path (what GET /audit uses).
        await using var session = await new NpgsqlChunkStore(fx.AppDs).BeginAsync(acme);
        var listed = await session.ListAuditAsync(1000);
        Assert.NotEmpty(listed);
        Assert.All(listed, r => Assert.Equal(acme.TenantId, r.TenantId));
        await session.CommitAsync();
    }
}
