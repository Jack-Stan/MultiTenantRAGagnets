using Npgsql;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// IMPLEMENTATION_PLAN step 12 / TESTING.md section 7. The ACL lives on documents and is joined at query time, so a
/// level change must show up on the NEXT query with no re-embed. Each scenario:
///   1. proves the starting visibility (so the test is not hollow),
///   2. changes documents.required_level AS app_user (hr-admin context, committed),
///   3. queries through the real RetrievalService as every role, in BOTH modes (app filter + RLS, and RLS only),
///   4. proves the chunk rows are byte-identical (same ctid, same text, same embedding): nothing was re-embedded or rewritten.
/// The seeded level is always put back in a finally block (as owner), and checked.
/// UNVERIFIED locally: needs Postgres.
/// </summary>
[Collection(LeakageCollection.Name)]
public class AccessChangeTests(LeakageFixture fx)
{
    private const string Tenant = "acme";

    private CorpusDocument PickDoc(string tenantSlug, int level) =>
        fx.Corpus.Documents.First(d => d.TenantSlug == tenantSlug && d.RequiredLevel == level && !d.IsInjection && d.ChunkIds.Count > 0);

    /// <summary>Queries with the chunk's own text (distance 0, so it would be rank 1 if permitted) and reports what the caller got.</summary>
    private async Task<(bool Retrieved, bool Sent, bool ReachedLlm)> ProbeAsync(Pipeline p, bool rlsOnly, string tenantSlug, string role, CorpusChunk chunk)
    {
        p.Chat.Reset();
        var caller = fx.Oracle.CallerFor(tenantSlug, role);
        var result = rlsOnly
            ? await p.Service.QueryWithoutAppFilterForTestingAsync(caller, chunk.Text, LeakageFixture.K)
            : await p.Service.QueryAsync(caller, chunk.Text, LeakageFixture.K);
        var reachedLlm = p.Chat.Requests.SelectMany(r => r.Context).Any(c => c.Id == chunk.Id.ToString("D"));
        return (result.RetrievedChunkIds.Contains(chunk.Id), result.SentChunkIds.Contains(chunk.Id), reachedLlm);
    }

    /// <summary>Every role, both modes, every chunk of the document: visible exactly when role level >= current required level.</summary>
    private async Task AssertVisibilityAsync(string phase, string tenantSlug, CorpusDocument doc, int currentLevel)
    {
        foreach (var rlsOnly in new[] { false, true })
        {
            var pipeline = fx.NewPipeline(fx.AppDs);
            foreach (var role in Roles.All)
            foreach (var chunkId in doc.ChunkIds)
            {
                var chunk = fx.Oracle.ChunkById[chunkId];
                var expected = Roles.CanSee(role, currentLevel);
                var got = await ProbeAsync(pipeline, rlsOnly, tenantSlug, role, chunk);
                var where = $"[{phase}] doc '{doc.Title}' now level {currentLevel}, {tenantSlug}/{role}, {(rlsOnly ? "RLS-only" : "app+RLS")}, chunk #{chunk.ChunkIndex}";
                Assert.True(got.Retrieved == expected,
                    $"{where}: expected retrieved={expected} but got {got.Retrieved}. " +
                    (expected ? "The chunk went missing after an ACL change (or never showed)." : "STALE ACL: the chunk is still retrievable after access was withdrawn."));
                Assert.True(got.Sent == expected, $"{where}: expected sent-to-LLM={expected} but got {got.Sent}");
                Assert.True(got.ReachedLlm == expected, $"{where}: the recording LLM saw the chunk = {got.ReachedLlm}, expected {expected}");
            }
        }
    }

    /// <summary>Runs a sequence of level changes on one document, asserting visibility and byte-identical chunks after each one.</summary>
    private async Task RunScenarioAsync(string scenario, string tenantSlug, CorpusDocument doc, int[] levelSequence)
    {
        var tenantId = fx.Oracle.TenantId(tenantSlug);
        var originalLevel = doc.RequiredLevel;
        var chunkRowsBefore = await AclDb.ChunkRowsAsync(fx.OwnerDs, doc.Id);
        var totalChunksBefore = await fx.CountChunksAsync();
        Assert.Equal(doc.ChunkIds.Count, chunkRowsBefore.Count);

        // The globex twin of this document (same template, other tenant) must be completely unaffected by acme's change.
        var twin = fx.Corpus.Documents.First(d => d.TenantSlug != tenantSlug && d.TemplateSlug == doc.TemplateSlug);
        var twinLevel = twin.RequiredLevel;

        try
        {
            Assert.Equal(originalLevel, await AclDb.OwnerLevelAsync(fx.OwnerDs, doc.Id)); // starting state really is the seeded one
            await AssertVisibilityAsync($"{scenario}: before any change", tenantSlug, doc, originalLevel);

            var current = originalLevel;
            foreach (var next in levelSequence)
            {
                // The change, through the real application role. hr-admin is the only role meant to do this.
                var rows = await AclDb.SetLevelAsAppUserAsync(fx.AppDs, tenantId, Roles.HrAdmin, doc.Id, next);
                Assert.True(rows == 1, $"hr-admin could not change '{doc.Title}' from level {current} to {next} (rows affected: {rows})");
                current = next;

                await AssertVisibilityAsync($"{scenario}: after moving to level {next}", tenantSlug, doc, current);

                // Nothing was re-embedded or rewritten: same physical rows, same text, same embedding vectors.
                var chunkRowsNow = await AclDb.ChunkRowsAsync(fx.OwnerDs, doc.Id);
                Assert.True(chunkRowsBefore.SequenceEqual(chunkRowsNow),
                    $"[{scenario}] chunk rows of '{doc.Title}' changed after an ACL-only update to level {next}: that means a rewrite / re-embed happened.");
                Assert.Equal(totalChunksBefore, await fx.CountChunksAsync());

                // The other tenant's twin document did not move.
                Assert.Equal(twinLevel, await AclDb.OwnerLevelAsync(fx.OwnerDs, twin.Id));
            }

            await AssertVisibilityAsync($"{scenario}: other tenant's twin unaffected", twin.TenantSlug, twin, twinLevel);
        }
        finally
        {
            await AclDb.OwnerRestoreLevelAsync(fx.OwnerDs, doc.Id, originalLevel);
        }

        // Restored for real: the seeded visibility is back, and the rows still never moved.
        await AssertVisibilityAsync($"{scenario}: after restore", tenantSlug, doc, originalLevel);
        Assert.True(chunkRowsBefore.SequenceEqual(await AclDb.ChunkRowsAsync(fx.OwnerDs, doc.Id)), $"[{scenario}] chunk rows differ after restore");
    }

    [DbFact]
    public Task Revoke_a_manager_document_up_to_hr_admin_only_hides_it_from_managers_on_the_next_query() =>
        RunScenarioAsync("revoke", Tenant, PickDoc(Tenant, 2), [3]);

    [DbFact]
    public Task Grant_an_hr_admin_document_to_managers_then_to_everyone_shows_it_on_the_next_query() =>
        RunScenarioAsync("grant", Tenant, PickDoc(Tenant, 3), [2, 1]);

    [DbFact]
    public Task Moving_a_document_up_and_down_the_levels_tracks_every_step() =>
        RunScenarioAsync("move", Tenant, PickDoc(Tenant, 1), [2, 3, 1, 3, 2, 1]);

    [DbFact]
    public Task Access_changes_apply_to_the_other_tenant_too_and_stay_inside_it() =>
        RunScenarioAsync("globex", "globex", PickDoc("globex", 2), [1, 3]);

    [DbFact]
    public async Task The_change_is_seen_by_a_connection_that_was_already_used_for_an_earlier_query()
    {
        // One pipeline (one pool) for the whole test: a cached/stale session or plan would show the old answer.
        var doc = PickDoc(Tenant, 2);
        var chunk = fx.Oracle.ChunkById[doc.ChunkIds[0]];
        var pipeline = fx.NewPipeline(fx.AppDs);
        try
        {
            Assert.True((await ProbeAsync(pipeline, false, Tenant, Roles.Manager, chunk)).Retrieved, "precondition: manager sees a level 2 document");
            Assert.True((await ProbeAsync(pipeline, false, Tenant, Roles.Manager, chunk)).Retrieved, "precondition, again, on a warm pool");

            Assert.Equal(1, await AclDb.SetLevelAsAppUserAsync(fx.AppDs, fx.Oracle.TenantId(Tenant), Roles.HrAdmin, doc.Id, 3));
            Assert.False((await ProbeAsync(pipeline, false, Tenant, Roles.Manager, chunk)).Retrieved, "STALE ACL on a warm pool");
            Assert.False((await ProbeAsync(pipeline, true, Tenant, Roles.Manager, chunk)).Retrieved, "STALE ACL on a warm pool (RLS-only)");

            Assert.Equal(1, await AclDb.SetLevelAsAppUserAsync(fx.AppDs, fx.Oracle.TenantId(Tenant), Roles.HrAdmin, doc.Id, 2));
            Assert.True((await ProbeAsync(pipeline, false, Tenant, Roles.Manager, chunk)).Retrieved, "re-granted access not visible on a warm pool");
        }
        finally
        {
            await AclDb.OwnerRestoreLevelAsync(fx.OwnerDs, doc.Id, doc.RequiredLevel);
        }
    }
}

/// <summary>
/// SECURITY PROBE (step 12): who is allowed to change documents.required_level, and can a document be moved to another
/// tenant? These assert the INTENDED behaviour, not the current one:
///   - only hr-admin may change an ACL (Program.cs: hr-admin is "the only role allowed to ingest"),
///   - nobody may move a row across tenants (WITH CHECK).
/// Every attempt runs in a transaction that is rolled back, and the test checks the committed state is untouched.
/// Tests marked Trait Finding assert intended behaviour that the CURRENT policy may violate; a red result is a real defect,
/// not a flaky test, and must not be "fixed" by loosening the assertion.
/// UNVERIFIED locally: needs Postgres.
/// </summary>
[Collection(LeakageCollection.Name)]
public class AclWritePolicyTests(LeakageFixture fx)
{
    private CorpusDocument PickDoc(string tenantSlug, int level) =>
        fx.Corpus.Documents.First(d => d.TenantSlug == tenantSlug && d.RequiredLevel == level && !d.IsInjection);

    private IEnumerable<Guid> SuiteTenants => fx.Corpus.Tenants.Select(t => t.Id);

    /// <summary>One attempt, own transaction, always rolled back. Allowed = the statement ran and changed at least one row.</summary>
    private async Task<(bool Changed, string Detail)> TryAsync(Guid tenantId, string role, string sql, params (string Name, NpgsqlTypes.NpgsqlDbType Type, object Value)[] ps)
    {
        await using var raw = await RawTx.BeginAsync(fx.AppDs, tenantId, role);
        try
        {
            await using var cmd = new NpgsqlCommand(sql, raw.Conn, raw.Tx);
            foreach (var (name, type, value) in ps) cmd.Parameters.AddWithValue(name, type, value);
            var n = await cmd.ExecuteNonQueryAsync();
            return (n > 0, $"{n} row(s) affected");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return (false, $"denied (42501): {ex.MessageText}");
        }
    }

    private static (string, NpgsqlTypes.NpgsqlDbType, object) Level(int l) => ("lvl", NpgsqlTypes.NpgsqlDbType.Smallint, (short)l);
    private static (string, NpgsqlTypes.NpgsqlDbType, object) Id(Guid g) => ("id", NpgsqlTypes.NpgsqlDbType.Uuid, g);

    [DbFact]
    public async Task Control_hr_admin_can_change_any_document_to_any_level()
    {
        // If this fails the policy is tighter than intended and every "denied" below proves nothing.
        var tenant = fx.Oracle.TenantId("acme");
        foreach (var from in new[] { 1, 2, 3 })
        foreach (var to in new[] { 1, 2, 3 }.Where(l => l != from))
        {
            var doc = PickDoc("acme", from);
            var (changed, detail) = await TryAsync(tenant, Roles.HrAdmin, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(to), Id(doc.Id));
            Assert.True(changed, $"hr-admin must be able to move '{doc.Title}' from level {from} to {to}: {detail}");
        }
    }

    // FINDING 12-A (expected RED against the current 002_rls.sql):
    // The documents policy is ONE combined FOR ALL policy: USING and WITH CHECK are both
    //   tenant_id = rag_current_tenant() AND rag_level_allows(rag_current_level(), required_level).
    // That says "you may write any row you may read, as long as the NEW level is also <= your own level". It never looks at
    // WHO you are, so:
    //   - a manager can LOWER a level-2 (manager) document to 1: every employee in the tenant can now read it (privilege widening);
    //   - a manager can RAISE a level-1 document to 2: employees lose a document they should have (availability/tamper).
    // The intended rule is "only hr-admin may change an ACL". Employees are already safe (their own level is 1, so WITH CHECK
    // blocks any raise and there is nothing lower than 1). Fix belongs in db/migrations (RÓISÍN): split the policy into a
    // read policy and a write policy gated on rag_current_level() >= 3 (or a hr-admin-only UPDATE of required_level).
    [DbFact]
    [Trait("Finding", "12-A")]
    public async Task Only_hr_admin_may_change_required_level_lower_roles_are_denied()
    {
        var tenant = fx.Oracle.TenantId("acme");
        var aclsBefore = await AclDb.DocumentAclsAsync(fx.OwnerDs, SuiteTenants);
        var violations = new List<string>();

        foreach (var role in new[] { Roles.Employee, Roles.Manager })
        foreach (var from in new[] { 1, 2, 3 })
        {
            var doc = PickDoc("acme", from);
            foreach (var to in new[] { 1, 2, 3 }.Where(l => l != from))
            {
                var (changed, detail) = await TryAsync(tenant, role, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(to), Id(doc.Id));
                if (changed)
                    violations.Add($"{role} changed '{doc.Title}' from level {from} to {to} ({detail})");
            }
        }

        var aclsAfter = await AclDb.DocumentAclsAsync(fx.OwnerDs, SuiteTenants);
        Assert.True(aclsAfter.SequenceEqual(aclsBefore), "test hygiene: a probe leaked into the committed state");
        Assert.True(violations.Count == 0,
            "FINDING 12-A: roles below hr-admin can change a document's ACL under the current RLS policy:\n  - " + string.Join("\n  - ", violations));
    }

    [DbFact]
    public async Task Lower_roles_cannot_touch_documents_they_cannot_see()
    {
        var tenant = fx.Oracle.TenantId("acme");
        var hrDoc = PickDoc("acme", 3);
        var mgrDoc = PickDoc("acme", 2);

        foreach (var (role, doc) in new[] { (Roles.Employee, hrDoc), (Roles.Employee, mgrDoc), (Roles.Manager, hrDoc) })
        foreach (var to in new[] { 1, 2, 3 }.Where(l => l != doc.RequiredLevel))
        {
            var (changed, detail) = await TryAsync(tenant, role, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(to), Id(doc.Id));
            Assert.False(changed, $"{role} changed the level of '{doc.Title}' (level {doc.RequiredLevel}, above them) to {to}: {detail}");
        }

        // Rows hidden by RLS are not errors, they are simply not there: the statement matches 0 rows.
        var (hidden, hiddenDetail) = await TryAsync(tenant, Roles.Employee, "UPDATE documents SET title = 'x' WHERE id = @id", Id(hrDoc.Id));
        Assert.False(hidden, hiddenDetail);
    }

    [DbFact]
    public async Task Raising_a_document_above_your_own_level_is_denied_by_with_check()
    {
        // The WITH CHECK half: even a role that can see a document cannot push it to a level it could not read itself.
        // (The employee may not "hide" a doc from itself; a manager may not mint an hr-admin document.)
        var tenant = fx.Oracle.TenantId("acme");
        var empDoc = PickDoc("acme", 1);
        var mgrDoc = PickDoc("acme", 2);

        var (c1, d1) = await TryAsync(tenant, Roles.Employee, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(3), Id(empDoc.Id));
        Assert.False(c1, $"employee raised a level-1 document to hr-admin-only: {d1}");
        var (c2, d2) = await TryAsync(tenant, Roles.Employee, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(2), Id(empDoc.Id));
        Assert.False(c2, $"employee raised a level-1 document to manager-only: {d2}");
        var (c3, d3) = await TryAsync(tenant, Roles.Manager, "UPDATE documents SET required_level = @lvl WHERE id = @id", Level(3), Id(mgrDoc.Id));
        Assert.False(c3, $"manager raised a level-2 document to hr-admin-only: {d3}");
    }

    [DbFact]
    public async Task A_document_cannot_be_moved_to_another_tenant_even_by_hr_admin()
    {
        var acme = fx.Oracle.TenantId("acme");
        var globex = fx.Oracle.TenantId("globex");
        var aclsBefore = await AclDb.DocumentAclsAsync(fx.OwnerDs, SuiteTenants);
        var acmeDoc = PickDoc("acme", 1);
        var acmeChunk = fx.Oracle.ChunkById[acmeDoc.ChunkIds[0]];
        var globexDoc = PickDoc("globex", 1);

        // push: acme row -> globex. Must be refused by the RLS WITH CHECK (42501), not by a foreign key (23503) that merely happens to fire.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme, Roles.HrAdmin))
        {
            await using var cmd = new NpgsqlCommand("UPDATE documents SET tenant_id = @other WHERE id = @id", raw.Conn, raw.Tx);
            cmd.Parameters.AddWithValue("other", NpgsqlTypes.NpgsqlDbType.Uuid, globex);
            cmd.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, acmeDoc.Id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege && ex.MessageText.Contains("row-level security", StringComparison.OrdinalIgnoreCase),
                $"moving a document to another tenant must die on the RLS WITH CHECK, got {ex.SqlState}: {ex.MessageText}");
        }

        // push a chunk across: same wall on chunks.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme, Roles.HrAdmin))
        {
            await using var cmd = new NpgsqlCommand("UPDATE chunks SET tenant_id = @other WHERE id = @id", raw.Conn, raw.Tx);
            cmd.Parameters.AddWithValue("other", NpgsqlTypes.NpgsqlDbType.Uuid, globex);
            cmd.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, acmeChunk.Id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // pull: another tenant's row is invisible, so there is nothing to update (0 rows), never a silent steal.
        var (pulled, pulledDetail) = await TryAsync(acme, Roles.HrAdmin, "UPDATE documents SET tenant_id = @t WHERE id = @id",
            ("t", NpgsqlTypes.NpgsqlDbType.Uuid, acme), Id(globexDoc.Id));
        Assert.False(pulled, $"acme hr-admin pulled a globex document into acme: {pulledDetail}");

        // forge: insert a document straight into the other tenant.
        await using (var raw = await RawTx.BeginAsync(fx.AppDs, acme, Roles.HrAdmin))
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO documents (tenant_id, title, required_level) VALUES (@other, 'planted', 1)", raw.Conn, raw.Tx);
            cmd.Parameters.AddWithValue("other", NpgsqlTypes.NpgsqlDbType.Uuid, globex);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.True((await AclDb.DocumentAclsAsync(fx.OwnerDs, SuiteTenants)).SequenceEqual(aclsBefore), "a cross-tenant probe changed committed state");
    }

    // FINDING 12-B (related to 12-A; expected RED against the current policy; the intended rule is inferred, please confirm):
    // Program.cs says hr-admin is "the only role allowed to ingest", but at the database layer app_user holds full DML on
    // documents and chunks and the policy only checks tenant + "level <= mine". So an employee or manager connection
    // (e.g. through any future endpoint, or an app bug) can DELETE a document they can read (cascading its chunks), rewrite
    // its title, or INSERT a new document at their own level. The API gate is the only thing stopping it today.
    [DbFact]
    [Trait("Finding", "12-B")]
    public async Task Only_hr_admin_may_write_documents_lower_roles_cannot_delete_rename_or_insert()
    {
        var tenant = fx.Oracle.TenantId("acme");
        var violations = new List<string>();

        foreach (var (role, level) in new[] { (Roles.Employee, 1), (Roles.Manager, 2) })
        {
            var doc = PickDoc("acme", level);

            var (renamed, rd) = await TryAsync(tenant, role, "UPDATE documents SET title = 'tampered' WHERE id = @id", Id(doc.Id));
            if (renamed) violations.Add($"{role} renamed '{doc.Title}' ({rd})");

            var (deleted, dd) = await TryAsync(tenant, role, "DELETE FROM documents WHERE id = @id", Id(doc.Id));
            if (deleted) violations.Add($"{role} deleted '{doc.Title}' and (by cascade) its chunks ({dd})");

            var (inserted, idd) = await TryAsync(tenant, role,
                "INSERT INTO documents (tenant_id, title, required_level) VALUES (@t, 'planted by lower role', @lvl)",
                ("t", NpgsqlTypes.NpgsqlDbType.Uuid, tenant), Level(level));
            if (inserted) violations.Add($"{role} inserted a new level-{level} document ({idd})");
        }

        Assert.True(violations.Count == 0,
            "FINDING 12-B: roles below hr-admin can write documents at the database layer (API-only gate):\n  - " + string.Join("\n  - ", violations));
    }
}
