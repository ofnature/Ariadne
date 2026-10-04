using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

public class GoalTests
{
    [Fact]
    public void GoalNear_ExactRange_IsNeverSatisfiedEarly()
    {
        // range 0 means "exactly there": the follower ends the path, the goal never cuts it short
        var goal = new GoalNear(new Vector3(10, 0, 0), 0);
        Assert.False(goal.IsSatisfied(new Vector3(10, 0, 0)));
        Assert.Equal(0, goal.PlannerTolerance);
    }

    [Fact]
    public void GoalNear_WithinRange()
    {
        var goal = new GoalNear(new Vector3(10, 0, 0), 3);
        Assert.True(goal.IsSatisfied(new Vector3(8, 0, 0)));
        Assert.False(goal.IsSatisfied(new Vector3(6, 0, 0)));
    }

    [Fact]
    public void GoalInteract_TracksTheObject_AndUsesItsHitbox()
    {
        var pos = new Vector3(20, 0, 0);
        var goal = GoalInteract.For(() => (pos, 1.5f), 3.5f)!;
        Assert.Equal(5f, goal.PlannerTolerance);
        Assert.False(goal.IsSatisfied(new Vector3(14, 0, 0)));
        Assert.True(goal.IsSatisfied(new Vector3(15, 0, 0)));

        pos = new Vector3(40, 0, 0); // it wandered
        Assert.Equal(new Vector3(40, 0, 0), goal.Target);
        Assert.False(goal.IsSatisfied(new Vector3(15, 0, 0)));
    }

    [Fact]
    public void GoalInteract_DespawnedObject_KeepsLastKnown()
    {
        (Vector3, float)? live = (new Vector3(20, 0, 0), 0.5f);
        var goal = GoalInteract.For(() => live, 3.5f)!;
        live = null;
        Assert.Equal(new Vector3(20, 0, 0), goal.Target);
        Assert.True(goal.IsSatisfied(new Vector3(17, 0, 0)));
    }

    [Fact]
    public void GoalInteract_NothingToResolve_IsNull()
    {
        Assert.Null(GoalInteract.For(() => null, 3.5f));
    }

    [Fact]
    public void GoalAway_SatisfiedByDistanceFromThreat_NotByReachingTarget()
    {
        var goal = new GoalAway(new Vector3(0, 0, 0), 15) { Target = new Vector3(20, 0, 0) };
        Assert.False(goal.IsSatisfied(new Vector3(10, 0, 0)));
        Assert.True(goal.IsSatisfied(new Vector3(0, 0, 16)));  // any direction counts
        Assert.Equal(0, goal.PlannerTolerance);
    }

    [Fact]
    public void DistanceOutside_IsZeroInside_AndTheGapOutside()
    {
        var near = new GoalNear(new Vector3(10, 0, 0), 3);
        Assert.Equal(0, near.DistanceOutside(new Vector3(8, 0, 0)));
        Assert.Equal(2f, near.DistanceOutside(new Vector3(5, 0, 0)), 3);

        var interact = GoalInteract.For(() => (new Vector3(10, 0, 0), 0.5f), 3.5f)!;
        Assert.Equal(1f, interact.DistanceOutside(new Vector3(5, 0, 0)), 3);

        var away = new GoalAway(new Vector3(0, 0, 0), 15);
        Assert.Equal(5f, away.DistanceOutside(new Vector3(10, 0, 0)), 3);
        Assert.Equal(0, away.DistanceOutside(new Vector3(20, 0, 0)));
    }

    [Fact]
    public void DescribeEnd_ShortRouteFarBelowAnExactSpot_IsNotArrival()
    {
        // Eulmore's aetheryte: the planner said noRouteOnMesh, and the route ended on the
        // Mainstay floor 34 y under the crystal - which used to report "goal reached"
        var crystal = new GoalNear(new Vector3(0f, 82f, 0.9f), 0);
        var floorBelow = new Vector3(0.1f, 48.1f, 0.8f);
        Assert.StartsWith("closest reachable point", MoveRequest.DescribeEnd(crystal, floorBelow, routeShort: true));
        // a route the planner said reaches it keeps the guessed-height rule
        Assert.Equal("goal reached", MoveRequest.DescribeEnd(crystal, floorBelow, routeShort: false));
        // and a short route ending a yalm or two off in height is still on the spot
        Assert.Equal("goal reached", MoveRequest.DescribeEnd(crystal, new Vector3(0.2f, 80f, 0.9f), routeShort: true));
    }

    [Fact]
    public void DistanceOutside_ExactDestination_IgnoresHeight()
    {
        // a spot on the ground: the mesh's height for it is not the caller's
        var exact = new GoalNear(new Vector3(10, 100, 0), 0);
        Assert.Equal(0.5f, exact.DistanceOutside(new Vector3(10.5f, 4, 0)), 3);
    }
}
