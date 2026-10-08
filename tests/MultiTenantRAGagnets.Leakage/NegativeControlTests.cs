using MultiTenantRAGagnets.Leakage.Harness;
using MultiTenantRAGagnets.Leakage.Infrastructure;

namespace MultiTenantRAGagnets.Leakage;

/// <summary>
/// TESTING.md section 1c, the most important check in the project: take the protection away and prove the
/// suite SEES the leak. These tests PASS only when leaks are detected. If a control reports 0 leaks the harness
/// is hollow and the test fails loudly (HollowHarnessException) instead of letting a green result stand.
///
/// RLS is broken in place on the shared database and ALWAYS restored in a finally block (exact policy text and
/// flags are captured first and compared afterwards). The collection runs tests one at a time, so nothing else
/// sees the broken state. If the process is killed mid-test, re-run the migrations (CI uses a throwaway database).
/// </summary>
[Collection(LeakageCollection.Name)]
public class NegativeControlTests(LeakageFixture fx)
{
    private const int K = LeakageFixture.K;

    private async Task<(LeakReport Natural, LeakReport Bait)> RunBrokenAsync(Func<Task> breakIt, RlsSnapshot original)
    {
        LeakReport natural, bait;
        try
        {
            await breakIt();
            // Defence against a control that "breaks" nothing: the snapshot must now differ from the original.
            var broken = await RlsSnapshot.CaptureAsync(fx.OwnerDs);
            Assert.False(broken.SameAs(original), "The control did not change the RLS configuration at all. " + broken.Describe());

            var p = fx.NewRlsOnlyPipeline(fx.AppDs);
            natural = await p.Runner.RunAsync("NEGATIVE CONTROL natural", RunMode.RlsOnly, K, baitOnQueryPoint: false);
            bait = await p.Runner.RunAsync("NEGATIVE CONTROL bait on query point", RunMode.RlsOnly, K, baitOnQueryPoint: true);
        }
        finally
        {
            await RlsControl.RestoreAsync(fx.OwnerDs, original);
        }
        return (natural, bait);
    }

    private async Task<RlsSnapshot> PristineAsync()
    {
        var snap = await RlsSnapshot.CaptureAsync(fx.OwnerDs);
        Assert.True(snap.Enabled && snap.Forced && snap.Policies.Count == 1,
            "RLS on chunks is not in its migrated state before the control starts: " + snap.Describe());
        return snap;
    }

    private async Task AssertRestoredAndCleanAsync()
    {
        // After the restore the same RLS-only bait run must be clean again: the difference was the policy.
        var p = fx.NewRlsOnlyPipeline(fx.AppDs);
        var after = await p.Runner.RunAsync("after restore, bait on query point", RunMode.RlsOnly, K, baitOnQueryPoint: true);
        after.AssertClean();
    }

    [DbFact]
    public async Task Control_1_rls_disabled_RlsOnly_run_must_leak_across_tenants_and_roles()
    {
        var original = await PristineAsync();
        var (natural, bait) = await RunBrokenAsync(() => RlsControl.DisableRlsAsync(fx.OwnerDs), original);

        var summary = new NegativeControlSummary
        {
            Broken = "RLS disabled on chunks (no policy protection at all)",
            Run = RunSummary.From(bait),
            Expectation = "leaks > 0, both cross-tenant and cross-role",
            DetectedLeaks = bait.TotalLeaks > 0 && natural.TotalLeaks > 0,
        };
        summary.Run.Label = "RLS disabled, RLS-only run, bait on query point";
        fx.Results.NegativeControls.Add(summary);
        fx.Results.NegativeControls.Add(new NegativeControlSummary
        {
            Broken = "RLS disabled on chunks (no policy protection at all)",
            Run = RunSummary.From(natural),
            Expectation = "leaks > 0",
            DetectedLeaks = natural.TotalLeaks > 0,
        });

        NegativeControlVerdict.RequireLeaks(natural, "RLS disabled, natural queries");
        NegativeControlVerdict.RequireLeaks(bait, "RLS disabled, bait on query point");
        Assert.True(bait.CrossTenantLeakedChunks > 0, "No cross-TENANT leak detected with RLS disabled: " + bait.Summary);
        Assert.True(bait.CrossRoleLeakedChunks > 0, "No cross-ROLE leak detected with RLS disabled: " + bait.Summary);
        Assert.True(bait.LeakedSentChunks > 0, "Leaked chunks never reached the chat provider; the prompt-side check is not seeing them: " + bait.Summary);
        Assert.True(bait.BaitRetrievedQueries >= bait.Queries / 2,
            $"Only {bait.BaitRetrievedQueries}/{bait.Queries} bait chunks were retrieved with no protection: the queries do not bait. " + bait.Summary);

        await AssertRestoredAndCleanAsync();
    }

    [DbFact]
    public async Task Control_2_role_clause_dropped_from_policy_must_leak_across_roles_but_not_tenants()
    {
        var original = await PristineAsync();
        var (natural, bait) = await RunBrokenAsync(
            () => RlsControl.ReplaceWithTenantOnlyPolicyAsync(fx.OwnerDs, original), original);

        fx.Results.NegativeControls.Add(new NegativeControlSummary
        {
            Broken = "chunks policy reduced to tenant-only (role/level clause removed)",
            Run = RunSummary.From(bait),
            Expectation = "cross-role leaks > 0, cross-tenant leaks == 0",
            DetectedLeaks = bait.CrossRoleLeakedChunks > 0,
        });
        fx.Results.NegativeControls[^1].Run.Label = "tenant-only policy, RLS-only run, bait on query point";

        NegativeControlVerdict.RequireLeaks(bait, "role clause removed, bait on query point");
        Assert.True(bait.CrossRoleLeakedChunks > 0, "Dropping the role clause leaked no higher-level chunk: the role dimension is not covered by this suite. " + bait.Summary);
        Assert.Equal(0, bait.CrossTenantLeakedChunks); // the tenant wall is still there, so this must stay 0
        Assert.Equal(0, natural.CrossTenantLeakedChunks);

        await AssertRestoredAndCleanAsync();
    }
}
