using Npgsql;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// The facts that make "0 leaks" mean something. If any of these is false the leakage numbers are hollow:
/// a superuser connection ignores RLS, an empty result set trivially has no leaks, a query that cannot reach
/// its bait proves nothing, a pooled connection can carry the previous tenant.
/// </summary>
[Collection(LeakageCollection.Name)]
public class PreconditionTests(LeakageFixture fx)
{
    [DbFact]
    public async Task App_role_is_locked_down_and_chunks_rls_is_enabled_forced_and_intact()
    {
        await using var raw = await RawTx.BeginBareAsync(fx.AppDs);
        Assert.Equal("app_user", await raw.ScalarStringAsync("SELECT current_user"));
        Assert.Equal("False", await raw.ScalarStringAsync("SELECT rolsuper FROM pg_roles WHERE rolname = current_user"));
        Assert.Equal("False", await raw.ScalarStringAsync("SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user"));
        Assert.Equal("strict_order", await raw.ScalarStringAsync("SHOW hnsw.iterative_scan"));

        var snap = await RlsSnapshot.CaptureAsync(fx.OwnerDs);
        Assert.True(snap.Enabled && snap.Forced, "RLS must be ENABLED and FORCED on chunks. " + snap.Describe());
        RlsExpectations.AssertChunkPolicySet(snap);

        await using var ownerConn = await fx.OwnerDs.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", ownerConn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!, "OWNER_URL must be a superuser (it seeds through FORCE RLS and breaks policies).");
    }

    [DbFact]
    public async Task Seeded_hnsw_index_exists()
    {
        await using var raw = await RawTx.BeginBareAsync(fx.OwnerDs);
        Assert.Equal("1", await raw.ScalarStringAsync(
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'chunks' AND indexname = 'chunks_embedding_hnsw'"));
    }

    [DbFact]
    public async Task What_app_user_can_see_equals_the_manifest_exactly_for_every_tenant_and_role()
    {
        // Equality, not just "no extras": a policy that returned nothing would pass a leak check and fail this.
        foreach (var m in fx.Corpus.Manifest)
        {
            var tenant = fx.Oracle.TenantId(m.TenantSlug);
            await using var raw = await RawTx.BeginAsync(fx.AppDs, tenant, m.Role);

            var chunks = await raw.IdsAsync("SELECT id FROM chunks");
            Assert.True(chunks.SetEquals(m.VisibleChunkIds),
                $"{m.TenantSlug}/{m.Role}: RLS shows {chunks.Count} chunks, manifest says {m.VisibleChunkIds.Count}. " +
                $"extra={chunks.Except(m.VisibleChunkIds).Count()} missing={m.VisibleChunkIds.Except(chunks).Count()}");

            var docs = await raw.IdsAsync("SELECT id FROM documents");
            Assert.True(docs.SetEquals(m.VisibleDocIds), $"{m.TenantSlug}/{m.Role}: visible documents differ from the manifest.");
        }
    }

    [DbFact]
    public async Task No_context_means_no_rows_fail_closed()
    {
        await using var raw = await RawTx.BeginBareAsync(fx.AppDs);
        Assert.Equal(0, await raw.CountAsync("chunks"));
        Assert.Equal(0, await raw.CountAsync("documents"));
        Assert.Equal(0, await raw.CountAsync("users"));
        Assert.Equal(0, await raw.CountAsync("tenants"));
    }

    [DbFact]
    public async Task Connection_reuse_without_reset_never_hands_the_previous_tenant_to_the_next_borrower()
    {
        // TESTING.md 1b trap two. Pool size 1 forces every borrower onto the SAME physical connection, and the
        // backend pid proves it. A session-level SET (instead of SET LOCAL) would survive and show acme's rows.
        var cs = new NpgsqlConnectionStringBuilder(fx.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var ds = NpgsqlDataSourceFactory.Create(cs);
        var store = new NpgsqlChunkStore(ds);
        var acme = fx.Oracle.CallerFor("acme", Roles.HrAdmin);
        var globex = fx.Oracle.CallerFor("globex", Roles.Employee);

        string pidBefore;
        await using (var first = await RawTx.BeginBareAsync(ds))
        {
            pidBefore = await first.ScalarStringAsync("SELECT pg_backend_pid()");
        }

        // 1. A real store session for acme/hr-admin, committed.
        await using (var session = await store.BeginAsync(acme))
        {
            var rows = await session.SearchAsync(FakeEmbeddingProvider.Embed("annual leave"), 5, RetrievalFilterMode.AppAndRls);
            Assert.Equal(5, rows.Count); // not hollow: the first borrower really saw data
            await session.CommitAsync();
        }

        // 2. The next borrower, same physical connection, sets NOTHING.
        await using (var bare = await RawTx.BeginBareAsync(ds))
        {
            Assert.Equal(pidBefore, await bare.ScalarStringAsync("SELECT pg_backend_pid()"));
            Assert.Equal(0, await bare.CountAsync("chunks"));
            Assert.Equal(0, await bare.CountAsync("documents"));
            var leftover = await bare.ScalarStringAsync("SELECT coalesce(current_setting('app.tenant_id', true), '')");
            Assert.Equal("", leftover);
        }

        // 3. A different tenant through the store on that same connection sees only its own rows.
        await using (var session = await store.BeginAsync(globex))
        {
            var rows = await session.SearchAsync(FakeEmbeddingProvider.Embed("annual leave"), 5, RetrievalFilterMode.RlsOnly);
            await session.CommitAsync();
            Assert.Equal(5, rows.Count);
            var globexChunks = fx.Oracle.VisibleChunks("globex", Roles.Employee);
            Assert.All(rows, r => Assert.Contains(r.Id, globexChunks));
        }

        // 4. A rolled-back (uncommitted) session must not leave its context behind either.
        await using (var session = await store.BeginAsync(acme)) { /* disposed without commit */ }
        await using (var bare = await RawTx.BeginBareAsync(ds))
        {
            Assert.Equal(pidBefore, await bare.ScalarStringAsync("SELECT pg_backend_pid()"));
            Assert.Equal(0, await bare.CountAsync("chunks"));
        }
    }

    [DbFact]
    public async Task Adversarial_queries_are_genuinely_dangerous_the_bait_is_reachable_without_protection()
    {
        // As the OWNER (superuser, bypasses RLS) and with exact search: with bait on the query point, the forbidden
        // bait chunk must be the nearest neighbour of ALL 200 queries. If it is not, "0 leaks" cannot be a result.
        await using var raw = await RawTx.BeginBareAsync(fx.OwnerDs);
        await raw.ExecAsync("SET LOCAL enable_indexscan = off");
        var baitFound = 0;
        var naturalExposed = 0;
        foreach (var q in fx.Corpus.AdversarialQueries)
        {
            var visible = fx.Oracle.VisibleChunks(q.TenantSlug, q.Role);

            var bait = await raw.IdsAsync(
                $"SELECT id FROM chunks ORDER BY embedding <=> {RawTx.VectorLiteral(FakeEmbeddingProvider.Embed(fx.Oracle.ChunkById[q.BaitChunkId].Text))} LIMIT {LeakageFixture.K}");
            if (bait.Contains(q.BaitChunkId)) baitFound++;

            var natural = await raw.IdsAsync(
                $"SELECT id FROM chunks ORDER BY embedding <=> {RawTx.VectorLiteral(FakeEmbeddingProvider.Embed(q.Text))} LIMIT {LeakageFixture.K}");
            if (natural.Any(id => !visible.Contains(id))) naturalExposed++;
        }

        Assert.Equal(fx.Corpus.AdversarialQueries.Count, baitFound);
        Assert.True(naturalExposed > 0,
            "No natural query has a forbidden chunk in its unprotected top-k; the natural run could not leak even if RLS were gone.");
    }
}
