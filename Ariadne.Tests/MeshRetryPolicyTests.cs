using System;

namespace Ariadne.Tests;

// The zone-query retry policy: how long a negative answer from Mnemosyne is asked again, and why
// the two cases differ. It came out of MeshBroker's query loop, where the behaviour was documented
// in a comment and could not be exercised (zone entry is peak contention on the mesh file, so this
// is the loop that decides how a zone with a contended or half-loaded volume looks to the user).
public class MeshRetryPolicyTests
{
    [Fact]
    public void IdleZone_RetriesTwiceAndThenStandsByTheAnswer()
    {
        var retry = new MeshRetryPolicy();
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: false));
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: false));
        Assert.False(retry.Record(ZoneMeshStatus.Missing, buildRunning: false));
        Assert.Equal(3, retry.Attempts); // the third answer is the one we report
    }

    [Fact]
    public void AnUnreachableService_IsRetriedExactlyLikeAMissingMesh()
    {
        // both are "ask again" answers: the service may be mid-restart, the file may be held
        var retry = new MeshRetryPolicy();
        Assert.True(retry.Record(ZoneMeshStatus.MnemosyneUnavailable, buildRunning: false));
        Assert.True(retry.Record(ZoneMeshStatus.MnemosyneUnavailable, buildRunning: false));
        Assert.False(retry.Record(ZoneMeshStatus.MnemosyneUnavailable, buildRunning: false));
    }

    [Fact]
    public void AnAnswerWorthActingOn_NeverRetries()
    {
        // a current mesh, or one worth seeding, is the answer the loop was asking for
        foreach (var status in new[] { ZoneMeshStatus.LocalCurrent, ZoneMeshStatus.MnemosyneCached, ZoneMeshStatus.NotReady })
        {
            var retry = new MeshRetryPolicy();
            Assert.False(retry.Record(status, buildRunning: false));
            Assert.Equal(1, retry.Attempts);
        }
    }

    [Fact]
    public void ARunningBuild_KeepsAskingPastTheIdleBudget()
    {
        // a seed stays profitable for the whole build (Nav.Reload turns it into a cache load), so
        // the idle give-up does not apply while vnavmesh is working
        var retry = new MeshRetryPolicy();
        for (var i = 0; i < 20; i++)
            Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: true));
        Assert.Equal(20, retry.Attempts);
    }

    [Fact]
    public void ABuildThatStops_LetsTheIdleBudgetEndIt()
    {
        var retry = new MeshRetryPolicy();
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: true));
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: true));
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: true));
        Assert.False(retry.Record(ZoneMeshStatus.Missing, buildRunning: false)); // past the budget now
    }

    [Fact]
    public void TheCeiling_HoldsEvenWithABuildRunning()
    {
        var retry = new MeshRetryPolicy();
        while (retry.Record(ZoneMeshStatus.Missing, buildRunning: true))
        {
            Assert.True(retry.Attempts < MeshRetryPolicy.DefaultMaxAttempts + 2, "the ceiling did not hold");
        }
        Assert.Equal(MeshRetryPolicy.DefaultMaxAttempts, retry.Attempts);
    }

    [Fact]
    public void TheDelay_RampsToOneSecondThenCaps()
    {
        var retry = new MeshRetryPolicy();
        retry.Record(ZoneMeshStatus.Missing, buildRunning: true);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Delay);
        retry.Record(ZoneMeshStatus.Missing, buildRunning: true);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay);
        retry.Record(ZoneMeshStatus.Missing, buildRunning: true);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay); // capped, not 3s and climbing
    }

    [Fact]
    public void TheThresholds_ComeFromTheConstructor()
    {
        var retry = new MeshRetryPolicy(maxAttempts: 2, attemptsWhileIdle: 1, maxDelaySeconds: 7);
        Assert.True(retry.Record(ZoneMeshStatus.Missing, buildRunning: true));
        Assert.False(retry.Record(ZoneMeshStatus.Missing, buildRunning: true)); // ceiling of 2
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay);
    }
}
