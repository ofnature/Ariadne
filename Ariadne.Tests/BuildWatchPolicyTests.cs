namespace Ariadne.Tests;

public class BuildWatchPolicyTests
{
    [Fact]
    public void MeshThere_RequeriesWhateverElseIsTrue()
    {
        Assert.Equal(WatchAction.Requery, BuildWatchPolicy.Decide(ready: true, reachable: false, building: true, wantBuild: true, sends: 3));
    }

    [Fact]
    public void ServiceBuilding_Waits_EvenWithSendsLeft()
    {
        // our turn has not come, or our build is the one running: either way, no second capture
        Assert.Equal(WatchAction.Wait, BuildWatchPolicy.Decide(false, true, building: true, wantBuild: true, sends: 0));
    }

    [Fact]
    public void ServiceIdle_AndNoMesh_SendsTheCapture()
    {
        Assert.Equal(WatchAction.SendCapture, BuildWatchPolicy.Decide(false, true, false, true, sends: 0));
        // the first one was dropped while the service was busy, or its build failed
        Assert.Equal(WatchAction.SendCapture, BuildWatchPolicy.Decide(false, true, false, true, sends: 2));
    }

    [Fact]
    public void SendsSpent_StopsAsking()
    {
        Assert.Equal(WatchAction.StopBuilding, BuildWatchPolicy.Decide(false, true, false, true, BuildWatchPolicy.MaxSends));
    }

    [Fact]
    public void ServiceAway_OrBuildOff_OnlyWatches()
    {
        Assert.Equal(WatchAction.Wait, BuildWatchPolicy.Decide(false, reachable: false, false, true, 0));
        Assert.Equal(WatchAction.Wait, BuildWatchPolicy.Decide(false, true, false, wantBuild: false, 0));
    }

    [Fact]
    public void Progress_IsNeverMinusOne_WhileAMeshIsOnItsWay()
    {
        Assert.Equal(0.4f, BuildWatchPolicy.Progress(true, building: true, 0.4f, true, 1));
        // the server's idle -1 must not leak through while it says it is building
        Assert.Equal(0f, BuildWatchPolicy.Progress(true, building: true, -1f, true, 1));
        // waiting for our turn, or about to send: still "building" to a consumer
        Assert.Equal(0f, BuildWatchPolicy.Progress(true, building: false, -1f, true, 0));
    }

    [Fact]
    public void Progress_IsMinusOne_WhenNothingIsComing()
    {
        Assert.Equal(-1f, BuildWatchPolicy.Progress(reachable: false, false, 0.5f, true, 0));
        Assert.Equal(-1f, BuildWatchPolicy.Progress(true, false, -1f, wantBuild: false, 0));
        Assert.Equal(-1f, BuildWatchPolicy.Progress(true, false, -1f, true, BuildWatchPolicy.MaxSends));
    }
}
