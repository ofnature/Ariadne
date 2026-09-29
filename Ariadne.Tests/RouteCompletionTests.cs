using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

public class RouteCompletionTests
{
    private static readonly Vector3 Target = new(100, 10, 100);
    private const float MaxTail = 20f;

    private static List<Vector3> RouteEnding(float shortBy)
        => [new(40, 10, 100), new(100 - shortBy, 10, 100)];

    [Fact]
    public void PartialRouteThatStopsAYalmShort_IsWalkedToTheTarget()
    {
        // the crack in the mesh: continuous ground, two islands
        Assert.True(RouteCompletion.ShouldWalkTheRest(RouteEnding(1.2f), Target, stoppedShort: true, MaxTail, range: 0));
    }

    [Fact]
    public void CompleteRoute_IsNeverExtended()
    {
        Assert.False(RouteCompletion.ShouldWalkTheRest(RouteEnding(1.2f), Target, stoppedShort: false, MaxTail, 0));
    }

    [Fact]
    public void AlreadyInsideTheGoalsRange_NeedsNothing()
    {
        // the counter edge, 2.05 y from the NPC, asked for with range 3
        Assert.False(RouteCompletion.ShouldWalkTheRest(RouteEnding(2.05f), Target, true, MaxTail, range: 3));
    }

    [Fact]
    public void AtTheTarget_NeedsNothing()
    {
        Assert.False(RouteCompletion.ShouldWalkTheRest(RouteEnding(0.3f), Target, true, MaxTail, 0));
    }

    [Fact]
    public void TooFarToGuess_IsLeftAlone()
    {
        // Yanxia: the route along the water ended 291 y short. A line that long is not a plan.
        Assert.False(RouteCompletion.ShouldWalkTheRest(RouteEnding(291f), Target, true, MaxTail, 0));
        Assert.True(RouteCompletion.ShouldWalkTheRest(RouteEnding(19.9f), Target, true, MaxTail, 0));
    }

    [Fact]
    public void TurnedOff_ByALimitOfZero()
    {
        Assert.False(RouteCompletion.ShouldWalkTheRest(RouteEnding(1.2f), Target, true, maxTail: 0, 0));
    }

    [Fact]
    public void NoRoute_NothingToExtend()
    {
        Assert.False(RouteCompletion.ShouldWalkTheRest([], Target, true, MaxTail, 0));
    }

    [Fact]
    public void HeightCounts_TheGapIsMeasuredInThreeDimensions()
    {
        // the route ends right under the target, 30 y below it: not a stretch to walk
        List<Vector3> under = [new(40, -20, 100), new(100, -20, 100)];
        Assert.False(RouteCompletion.ShouldWalkTheRest(under, Target, true, MaxTail, 0));
    }

    [Fact]
    public void WithTail_AddsTheTargetAndLeavesTheRouteAlone()
    {
        var route = RouteEnding(1.2f);
        var extended = RouteCompletion.WithTail(route, Target);
        Assert.Equal(3, extended.Count);
        Assert.Equal(Target, extended[^1]);
        Assert.Equal(2, route.Count); // the planner's answer is not modified
    }
}
