using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Movement;

// Pure waypoint-advance logic, factored out of vnavmesh's FollowPath.Update so it can be
// unit-tested without a game: pops leading waypoints the player has passed (within
// tolerance of the segment player-previous → player-now), and honours the destination
// tolerance shortcut. Game-conditional waypoint kinds (client paths, warps) are not
// modelled yet — Mnemosyne's findPath emits plain positions today; the AreaId hooks come
// back with multi-modal legs (its milestone 11).
internal static class PathProgress
{
    /// <summary>Removes waypoints already passed. Returns true when the path is finished
    /// (empty, or within destinationTolerance of the final waypoint).</summary>
    public static bool Advance(List<Vector3> waypoints, Vector3 playerNow, Vector3? playerPrev,
        float tolerance, float destinationTolerance, bool ignoreDeltaY)
    {
        while (waypoints.Count > 0)
        {
            var a = waypoints[0];
            var b = playerNow;
            var c = playerPrev ?? b;

            if (destinationTolerance > 0 && (b - waypoints[^1]).Length() <= destinationTolerance)
            {
                waypoints.Clear();
                return true;
            }

            if (ignoreDeltaY)
            {
                a.Y = 0;
                b.Y = 0;
                c.Y = 0;
            }

            if (DistanceToLineSegment(a, b, c) > tolerance)
                return false;

            waypoints.RemoveAt(0);
        }
        return true;
    }

    // distance from point v to segment a-b (vnavmesh's DistanceToLineSegment)
    internal static float DistanceToLineSegment(Vector3 v, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var av = v - a;

        if (ab.Length() == 0 || Vector3.Dot(av, ab) <= 0)
            return av.Length();

        var bv = v - b;
        if (Vector3.Dot(bv, ab) >= 0)
            return bv.Length();

        return Vector3.Cross(ab, av).Length() / ab.Length();
    }
}
