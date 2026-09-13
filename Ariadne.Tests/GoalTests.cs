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
}
