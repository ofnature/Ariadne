using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

// Where a MoveRequest-owned path ends, played out without the game: the follower's own
// arithmetic (PathProgress with the tolerances PathFollower passes) and the goal check
// MoveRequest runs each tick, stepped along the route.
//
// The case is SealBreaker's, 2026-09-27, with the route the live planner returned: Ul'dah -
// Steps of Nald, the Flame Personnel Officer behind the Hall of Flames counter. The goal is
// off-mesh, so the route ends at the closest reachable point - the counter edge, 2.05 y
// from the NPC.
public class OwnedPathArrivalTests
{
    private static readonly Vector3 Npc = new(-144.396f, 4.100f, -107.225f);
    // 4.9 y from the NPC. (The report called it 3.9 y and said the character "moved away" to
    // 5.0 y; these coordinates say it stood at 4.9 y and never moved, which is what the rule
    // below predicts.)
    private static readonly Vector3 Start = new(-140.9f, 4.11f, -103.8f);
    private static readonly Vector3 CounterEdge = new(-142.69107f, 4.25f, -106.08838f);
    private const float Range = 3.0f;
    private const float SealBreakerWaypointTolerance = 3.0f; // its Path.SetTolerance

    private sealed record Outcome(Vector3 Position, bool EndedByGoal, int Ticks);

    /// <summary>Walk the route at 0.05 y a tick, the way the follower and MoveRequest do:
    /// advance the waypoints, then let the goal end the path.</summary>
    private static Outcome Walk(Vector3 start, List<Vector3> route, IGoal goal,
        float waypointTolerance, float destinationTolerance, float? finalTolerance)
    {
        var pos = start;
        Vector3? prev = null;
        for (var tick = 0; tick < 10_000; tick++)
        {
            PathProgress.Advance(route, pos, prev, waypointTolerance, destinationTolerance, ignoreDeltaY: true, finalTolerance);
            if (route.Count == 0)
                return new Outcome(pos, false, tick);
            if (goal.IsSatisfied(pos))
                return new Outcome(pos, true, tick);

            prev = pos;
            var to = route[0] - pos;
            pos += to.Length() <= 0.05f ? to : Vector3.Normalize(to) * 0.05f;
        }
        throw new InvalidOperationException("the walk never ended");
    }

    [Fact]
    public void CloseTo_StopsWithinRangeOfTheTarget_NotOfTheRoutesEnd()
    {
        var goal = new GoalNear(Npc, Range);
        var end = Walk(Start, [Start, CounterEdge], goal, SealBreakerWaypointTolerance,
            destinationTolerance: 0, PathFollower.OwnedFinalTolerance(SealBreakerWaypointTolerance));

        Assert.True(end.EndedByGoal);
        var distance = Vector3.Distance(end.Position, Npc);
        Assert.InRange(distance, Range - 0.1f, Range); // walked in, and stopped as the range was met
        Assert.Equal("goal reached", MoveRequest.DescribeEnd(goal, end.Position));
    }

    [Fact]
    public void RangeAgainstTheLastWaypoint_IsWhatLeftTheCharacterShort()
    {
        // What owned paths did before, and what externally supplied paths still do (vnavmesh's
        // rule): the path is finished inside `range` of its LAST WAYPOINT. The start is 2.9 y
        // from the counter edge, so the route is over before a step is taken - 4.9 y from the NPC.
        var end = Walk(Start, [Start, CounterEdge], new GoalNear(Npc, 0), waypointTolerance: 0.25f,
            destinationTolerance: Range, finalTolerance: null);

        Assert.Equal(0, end.Ticks);
        Assert.Equal(4.9f, Vector3.Distance(end.Position, Npc), 1);
    }

    [Fact]
    public void ALargeWaypointTolerance_DoesNotEndAnOwnedRouteShort()
    {
        // without the final-waypoint rule, SealBreaker's tolerance of 3 pops the counter edge
        // from 2.9 y away and the route is over at the start, whatever the destination tolerance
        var popped = Walk(Start, [Start, CounterEdge], new GoalNear(Npc, 0), SealBreakerWaypointTolerance,
            destinationTolerance: 0, finalTolerance: null);
        Assert.Equal(0, popped.Ticks);

        var walked = Walk(Start, [Start, CounterEdge], new GoalNear(Npc, 0), SealBreakerWaypointTolerance,
            destinationTolerance: 0, PathFollower.OwnedFinalTolerance(SealBreakerWaypointTolerance));
        Assert.True(Vector3.Distance(walked.Position, CounterEdge) <= PathFollower.OwnedArrivalTolerance + 0.05f);
    }

    [Fact]
    public void MeshDoesNotAllowTheRange_EndsAtTheClosestPoint_AndSaysHowFarShort()
    {
        // the same counter, a range the edge cannot satisfy: 2.05 y away, asked for 1.5
        var goal = new GoalNear(Npc, 1.5f);
        var end = Walk(Start, [Start, CounterEdge], goal, waypointTolerance: 0.25f,
            destinationTolerance: 0, PathFollower.OwnedFinalTolerance(0.25f));

        Assert.False(end.EndedByGoal);
        Assert.True(Vector3.Distance(end.Position, CounterEdge) <= 0.3f); // walked the whole route
        // 2.05 y from the NPC at the edge, plus the quarter yalm the follower stops before it
        Assert.InRange(goal.DistanceOutside(end.Position), 0.5f, 0.85f);
        Assert.Matches(@"^closest reachable point, 0\.[5-8]y short$", MoveRequest.DescribeEnd(goal, end.Position));
    }

    [Fact]
    public void InteractGoal_UsesTheSamePath()
    {
        var goal = GoalInteract.For(() => (Npc, 0.5f), 3.5f)!; // 4.0 y reach
        // from 8 y out, so there is a walk to do
        var start = CounterEdge + Vector3.Normalize(Start - CounterEdge) * 6f;
        var end = Walk(start, [start, CounterEdge], goal, SealBreakerWaypointTolerance,
            destinationTolerance: 0, PathFollower.OwnedFinalTolerance(SealBreakerWaypointTolerance));

        Assert.True(end.EndedByGoal);
        Assert.InRange(Vector3.Distance(end.Position, Npc), 3.9f, 4.0f);
    }

    [Fact]
    public void ExactDestination_IsReachedOnTheMeshsVersionOfThePoint()
    {
        // range 0: the route ends on the mesh (y 4.25), the caller asked for y 4.10 - reached
        var onMesh = new Vector3(-130f, 4.10f, -100f);
        var goal = new GoalNear(onMesh, 0);
        Assert.Equal("goal reached", MoveRequest.DescribeEnd(goal, new Vector3(-130.1f, 4.25f, -100.1f)));
        // and an off-mesh exact destination reports the gap instead of pretending
        Assert.Equal("closest reachable point, 2.0y short", MoveRequest.DescribeEnd(new GoalNear(Npc, 0), CounterEdge));
    }
}
