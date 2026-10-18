using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Movement;

/// <summary>
/// A walking route has to begin where the character is standing. When it begins yalms below
/// the feet, the start was snapped to something that is not the ground being stood on.
///
/// <para>Reported 2026-09-28: movement requests that ran underground. The meshes explain it.
/// South Shroud's has 17,606 walkable polys in 581 islands, and the largest island is not the
/// terrain: it is a flat plane at y -14, 41% of the area, lying under the real ground, with
/// more planes at -49 and -51. vnavmesh flood-fills from known-reachable seeds after loading
/// and flags everything else unreachable, so it never routes on those. Meshes served by
/// Mnemosyne carry no such flags. The planner snaps a position to the nearest poly within
/// five yalms, so wherever the surface mesh has a gap, the character's position lands on the
/// plane underneath, and the whole route is planned there.</para>
///
/// The fix proper is Mnemosyne's (prune the islands nobody can stand on). This is the check
/// Ariadne can make with what it knows and the planner does not: where the character really is.
/// </summary>
internal static class RouteSanity
{
    /// <summary>How far above or below the feet a walking route may begin. Wide enough for a
    /// jump in progress or a prop being stood on; narrower than the planner's five-yalm snap,
    /// which is what lets a plane under the terrain be chosen.</summary>
    public const float MaxStartOffset = 2.5f;

    /// <summary>Signed height of the route's first waypoint relative to the character, when it
    /// is further than <see cref="MaxStartOffset"/>; null when the route starts where the
    /// character stands (or there is no route to judge).</summary>
    public static float? StartOffset(Vector3 player, IReadOnlyList<Vector3> waypoints)
    {
        if (waypoints.Count == 0)
            return null;
        var dy = waypoints[0].Y - player.Y;
        return MathF.Abs(dy) > MaxStartOffset ? dy : null;
    }

    public static string Describe(float offset)
        => offset < 0 ? $"{-offset:0.0}y below your feet" : $"{offset:0.0}y above your head";

    /// <summary>How far around the character a route that began on another level looks for
    /// the surface they are actually standing on.</summary>
    public const float NearbySurfaceRadius = 6f;

    /// <summary>How far around the character to look for the mesh when the route cannot
    /// start where they stand, or null to not look.
    ///
    /// <para>Two cases. A route that began on another level (<see cref="StartOffset"/>) means
    /// the surface is close and the planner picked the wrong one: look nearby. No route at all
    /// because nothing is within the planner's five-yalm snap (`startOffMesh`) means a hole in
    /// the mesh around the character, and the walk out of it is a straight stretch over ground
    /// the mesh does not know — the same guess RouteCompletion makes at the other end of a
    /// route, so its limit applies, and turning that off turns this off. Seen 2026-10-18: a
    /// follower stood in such a hole in a dungeon for three minutes, answered "no path" twice
    /// a second, while the leader waited 13 y away.</para></summary>
    public static float? SurfaceSearchRadius(bool offTheMesh, float straightTailMax)
        => !offTheMesh ? NearbySurfaceRadius : straightTailMax > 0 ? straightTailMax : null;
}
