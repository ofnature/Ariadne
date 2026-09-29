using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Movement;

/// <summary>
/// What to do when the mesh stops short of where the character is going.
///
/// <para>The meshes are cracked. Yanxia's is 2,783 separate pieces, and many of the gaps
/// between them are a yalm wide at the same height: continuous ground in the game, two
/// islands in the mesh. The planner routes inside one piece and hands back a partial route, or
/// — when the target itself is off the mesh — no route and the nearest point that is on it. On
/// one client, over two days, 135 path requests found a route and 471 ended one of those two
/// ways (2026-09-29).</para>
///
/// <para>vnavmesh has the same files and the same cracks. It gets by because its route always
/// ends with the target itself as the last waypoint, clamped to nothing: the character walks
/// the mesh as far as it goes and then straight at the target. That is the behaviour adopted
/// here, with two differences. The straight stretch is limited in length, since a line through
/// unknown ground is a guess and a short guess is a better one. And a stall on that stretch
/// ends the move where it stands, instead of re-planning a route that will end in the same
/// place.</para>
///
/// Pure: the broker does the asking, this does the deciding.
/// </summary>
internal static class RouteCompletion
{
    /// <summary>A gap this small is arrival, not a stretch to walk.</summary>
    public const float MinGap = 0.5f;

    /// <summary>Whether to walk straight from the end of the route to the target.</summary>
    /// <param name="stoppedShort">The planner said the route does not reach: it is partial, or
    /// the target is off the mesh. A complete route is never extended.</param>
    /// <param name="maxTail">The longest straight stretch allowed; 0 turns this off.</param>
    /// <param name="range">The goal's range. A route that already ends inside it needs nothing.</param>
    public static bool ShouldWalkTheRest(IReadOnlyList<Vector3> waypoints, Vector3 target, bool stoppedShort,
        float maxTail, float range)
    {
        if (!stoppedShort || waypoints.Count == 0 || maxTail <= 0)
            return false;
        var gap = Vector3.Distance(waypoints[^1], target);
        if (gap <= MathF.Max(range, MinGap))
            return false;
        return gap <= maxTail;
    }

    /// <summary>The route with the target added as its last waypoint.</summary>
    public static List<Vector3> WithTail(IReadOnlyList<Vector3> waypoints, Vector3 target)
    {
        var route = new List<Vector3>(waypoints.Count + 1);
        route.AddRange(waypoints);
        route.Add(target);
        return route;
    }
}
