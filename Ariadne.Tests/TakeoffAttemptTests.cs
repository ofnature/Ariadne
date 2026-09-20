using Ariadne.Movement;
using System;
using Xunit;

namespace Ariadne.Tests;

public class TakeoffAttemptTests
{
    private static readonly DateTime T0 = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
    private static TakeoffAttempt Attempt() => new(TimeSpan.FromSeconds(2));

    [Fact]
    public void KeepsTryingInsideTheBudget()
    {
        var takeoff = Attempt();
        Assert.True(takeoff.Update(true, T0));
        Assert.True(takeoff.Update(true, T0.AddSeconds(1.9)));
        Assert.False(takeoff.Abandoned);
    }

    [Fact]
    public void GivesUpWhenTheBudgetRunsOut()
    {
        var takeoff = Attempt();
        takeoff.Update(true, T0);
        Assert.False(takeoff.Update(true, T0.AddSeconds(2)));
        Assert.True(takeoff.Abandoned);
    }

    // The bug this class exists for: in a town every kerb is a climb, so an attempt that
    // re-arms on the first waypoint below the character jumps forever in a different rhythm.
    [Fact]
    public void StaysAbandonedAcrossLaterClimbs()
    {
        var takeoff = Attempt();
        takeoff.Update(true, T0);
        takeoff.Update(true, T0.AddSeconds(2)); // spent

        takeoff.Update(false, T0.AddSeconds(3));                 // a downhill waypoint
        Assert.False(takeoff.Update(true, T0.AddSeconds(4)));    // the next kerb
        Assert.True(takeoff.Abandoned);
    }

    // Time only runs while actually climbing: a long flight that dips below the next waypoint
    // near the end must not arrive with a spent budget for the final climb.
    [Fact]
    public void ClockRestartsAfterANonClimbingFrame()
    {
        var takeoff = Attempt();
        takeoff.Update(true, T0);
        takeoff.Update(false, T0.AddSeconds(1)); // airborne, or heading down
        Assert.True(takeoff.Update(true, T0.AddSeconds(2.5)));
        Assert.True(takeoff.Update(true, T0.AddSeconds(4.4)));
        Assert.False(takeoff.Abandoned);
    }

    [Fact]
    public void ResetRearmsForANewPath()
    {
        var takeoff = Attempt();
        takeoff.Update(true, T0);
        takeoff.Update(true, T0.AddSeconds(2));
        Assert.True(takeoff.Abandoned);

        takeoff.Reset();
        Assert.False(takeoff.Abandoned);
        Assert.True(takeoff.Update(true, T0.AddSeconds(5)));
    }
}
