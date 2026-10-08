using Npgsql;
using Pgvector;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Eval;
using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// TESTING.md section 2: a filtered HNSW search can silently return fewer than k rows when the index window fills
/// with rows the filter then strips. Dev-sized tables seq-scan and never show it.
///
/// Scenario (see <see cref="HostileScenario"/>): the query point is surrounded by 30 same-tenant rows the employee
/// may NOT see and 2500 rows of another tenant; the 60 rows the employee may see are far away. With
/// <c>hnsw.iterative_scan</c> off the index window (ef_search 40) is all stripped rows and 0 come back; with it on
/// the scan keeps going. The control run below proves the trap is real, so the assertion cannot be vacuous.
/// </summary>
[Collection(LeakageCollection.Name)]
public class UnderReturnTests(LeakageFixture fx)
{
    private static CallerContextFactory Caller(string slug, string role) =>
        new(HostileScenario.TenantId(slug), Ids.User(slug, role), role);

    private sealed record CallerContextFactory(Guid Tenant, Guid User, string Role)
    {
        public CallerContext ToContext() => new(Tenant, User, Role);
    }

    private async Task<int> AvailableAsync(Guid tenant, int level)
    {
        await using var raw = await RawTx.BeginBareAsync(fx.OwnerDs);
        return int.Parse(await raw.ScalarStringAsync(
            "SELECT count(*) FROM chunks c JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id " +
            $"WHERE c.tenant_id = '{tenant:D}' AND d.required_level <= {level}"));
    }

    private static HashSet<Guid> Permitted(string slug, int level)
    {
        var set = new HashSet<Guid>();
        switch (slug)
        {
            case HostileScenario.NoiseSlug:
                for (var i = 0; i < HostileScenario.NoiseRows; i++) set.Add(HostileScenario.ChunkId(slug, 1, i));
                break;
            case HostileScenario.VictimSlug:
                for (var i = 0; i < HostileScenario.VictimEmployeeRows; i++) set.Add(HostileScenario.ChunkId(slug, 1, i));
                if (level >= 3)
                    for (var i = 0; i < HostileScenario.VictimHrRows; i++) set.Add(HostileScenario.ChunkId(slug, 3, i));
                break;
            case HostileScenario.SmallSlug:
                for (var i = 0; i < HostileScenario.SmallRows; i++) set.Add(HostileScenario.ChunkId(slug, 1, i));
                break;
        }
        return set;
    }

    private async Task<int> RawSearchCountAsync(string iterativeScan, int k)
    {
        var victim = Caller(HostileScenario.VictimSlug, Roles.Employee);
        await using var raw = await RawTx.BeginAsync(fx.AppForcedHnswDs, victim.Tenant, victim.Role);
        await raw.ExecAsync($"SET LOCAL hnsw.iterative_scan = '{iterativeScan}'");
        await using var cmd = new NpgsqlCommand(RetrievalSql.RlsOnlySearch, raw.Conn, raw.Tx);
        cmd.Parameters.AddWithValue("q", new Vector(HostileScenario.QueryPoint));
        cmd.Parameters.AddWithValue("k", k);
        var n = 0;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) n++;
        return n;
    }

    private async Task<string> ExplainRlsOnlyAsync(CallerContextFactory caller, int k)
    {
        await using var raw = await RawTx.BeginAsync(fx.AppForcedHnswDs, caller.Tenant, caller.Role);
        var sql = "EXPLAIN (COSTS OFF) " + RetrievalSql.RlsOnlySearch
            .Replace("@q", RawTx.VectorLiteral(HostileScenario.QueryPoint), StringComparison.Ordinal)
            .Replace("@k", k.ToString(), StringComparison.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, raw.Conn, raw.Tx);
        var lines = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) lines.Add(r.GetString(0));
        return string.Join(" | ", lines.Select(l => l.Trim()));
    }

    [DbFact]
    public async Task Returned_equals_k_under_rls_and_hnsw_with_a_hostile_neighbour_distribution()
    {
        var summary = new UnderReturnSummary
        {
            Scenario = "Employee of a tenant whose permitted rows are far from the query point, while forbidden rows " +
                       "(same tenant, hr-only) and another tenant's rows sit right on it.",
            NoiseRowsOtherTenant = HostileScenario.NoiseRows,
            PermittedRowsVisibleToCaller = HostileScenario.VictimEmployeeRows,
            ForbiddenRowsOnQueryPoint = HostileScenario.VictimHrRows,
        };

        // 1. The HNSW index must really be the access path, or this is a seq scan and proves nothing.
        var plan = await ExplainRlsOnlyAsync(Caller(HostileScenario.VictimSlug, Roles.Employee), 5);
        summary.IndexPathEvidence = $"EXPLAIN (planner forced with {LeakageFixture.ForcedHnswOptions}): {plan}";
        Assert.True(plan.Contains("chunks_embedding_hnsw", StringComparison.Ordinal),
            "The forced plan does not use the HNSW index, so returned == k would be a seq-scan result, not an index-path result. Plan: " + plan);

        // 2. Control: same SQL, same connection settings, ONLY the iterative scan differs. The trap must be real.
        const int controlK = 5;
        var offCount = await RawSearchCountAsync("off", controlK);
        var onCount = await RawSearchCountAsync("strict_order", controlK);
        summary.KForIterativeOffControl = controlK;
        summary.ReturnedWithIterativeScanOff = offCount;
        fx.Results.UnderReturn = summary; // recorded even if an assertion below fails
        Assert.True(offCount < controlK,
            $"UNDER-RETURN TRAP NOT REPRODUCED: with iterative scan OFF the hostile scenario still returned {offCount}/{controlK}. " +
            "The scenario does not exercise the bug, so the returned == k assertion below would be hollow.");
        Assert.Equal(controlK, onCount);

        // 3. The assertion itself, through the real store, both search modes, several k, several callers.
        var callers = new[]
        {
            Caller(HostileScenario.VictimSlug, Roles.Employee),
            Caller(HostileScenario.VictimSlug, Roles.HrAdmin),
            Caller(HostileScenario.SmallSlug, Roles.Employee),
            Caller(HostileScenario.NoiseSlug, Roles.Employee),
        };
        var store = new NpgsqlChunkStore(fx.AppForcedHnswDs);
        foreach (var c in callers)
        {
            var slug = HostileScenario.Slugs.Single(s => HostileScenario.TenantId(s) == c.Tenant);
            var level = Roles.LevelOf(c.Role);
            var available = await AvailableAsync(c.Tenant, level);
            var permitted = Permitted(slug, level);
            Assert.Equal(permitted.Count, available); // the test's own expectation agrees with the database

            foreach (var k in new[] { 1, 5, 10, 20 })
            {
                foreach (var mode in new[] { RetrievalFilterMode.RlsOnly, RetrievalFilterMode.AppAndRls })
                {
                    await using var session = await store.BeginAsync(c.ToContext());
                    var rows = await session.SearchAsync(HostileScenario.QueryPoint, k, mode);
                    var expected = Math.Min(k, available);
                    summary.Cases.Add(new UnderReturnCase
                    {
                        Path = $"store/{mode}/forced-HNSW",
                        Caller = $"{slug}/{c.Role}",
                        K = k,
                        Available = available,
                        Returned = rows.Count,
                    });
                    Assert.True(rows.Count == expected,
                        $"UNDER-RETURN: {slug}/{c.Role} mode={mode} k={k} available={available} returned={rows.Count}");
                    Assert.All(rows, r => Assert.Contains(r.Id, permitted));
                }
            }
        }

        // 4. Through the full service (embedding override puts the query on the hostile point), both planner paths.
        foreach (var (name, ds) in new[] { ("default planner", fx.AppDs), ("forced HNSW", fx.AppForcedHnswDs) })
        {
            var victim = Caller(HostileScenario.VictimSlug, Roles.Employee);
            var permitted = Permitted(HostileScenario.VictimSlug, 1);
            foreach (var rlsOnly in new[] { false, true })
            {
                var p = rlsOnly ? fx.NewRlsOnlyPipeline(ds) : fx.NewPipeline(ds);
                p.Embeddings.Override = HostileScenario.QueryPoint;
                var result = rlsOnly
                    ? await p.Service.QueryWithoutAppFilterForTestingAsync(victim.ToContext(), "hostile neighbourhood", LeakageFixture.K)
                    : await p.Service.QueryAsync(victim.ToContext(), "hostile neighbourhood", LeakageFixture.K);
                summary.Cases.Add(new UnderReturnCase
                {
                    Path = $"service/{(rlsOnly ? "RlsOnly" : "AppAndRls")}/{name}",
                    Caller = $"{HostileScenario.VictimSlug}/{victim.Role}",
                    K = LeakageFixture.K,
                    Available = permitted.Count,
                    Returned = result.RetrievedChunkIds.Count,
                });
                Assert.Equal(LeakageFixture.K, result.RetrievedChunkIds.Count);
                Assert.All(result.RetrievedChunkIds, id => Assert.Contains(id, permitted));
                Assert.All(result.SentChunkIds, id => Assert.Contains(id, permitted));
            }
        }
    }
}
