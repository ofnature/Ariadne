using Ariadne.Mnemosyne;
using System;
using System.Collections.Generic;

namespace Ariadne.Movement;

/// <summary>How a leg of a planned route is travelled.</summary>
internal enum LegMode { Walk, Fly }

/// <summary>
/// What the follower does at a leg boundary, before travelling that leg's waypoints. The
/// spec's vocabulary (docs/mnemosyne-protocol.md → findPath → legs). A transition Ariadne
/// cannot perform yet is logged and the leg is followed as-is — refusing the path would be
/// worse than walking it without the mount/dismount the planner asked for.
/// </summary>
internal enum LegTransition { Mount, JumpOff, Land, Dismount, Teleport }

/// <summary>
/// One span of a multi-modal route: a run of waypoints sharing a movement mode, plus the
/// transition that starts it. <see cref="First"/>/<see cref="Count"/> index into the same flat
/// waypoint array the server also sends, so a consumer that ignores legs still gets a
/// followable — if mode-naive — path.
/// </summary>
internal sealed record PathLeg(LegMode Mode, LegTransition? Enter, ulong EnterArg, int First, int Count)
{
    /// <summary>Index one past this leg's last waypoint.</summary>
    public int End => First + Count;

    public bool Covers(int waypointIndex) => waypointIndex >= First && waypointIndex < End;

    public string Describe()
    {
        var mode = Mode == LegMode.Fly ? "fly" : "walk";
        return Enter is { } enter ? $"{mode} (enter: {enter.ToString().ToLowerInvariant()})" : mode;
    }
}

internal static class PathLegs
{
    private static readonly IReadOnlyList<PathLeg> None = Array.Empty<PathLeg>();

    /// <summary>
    /// Wire legs → domain legs, tolerantly. Unknown modes, non-positive spans and spans that
    /// start outside the waypoint array are dropped; a span running past the end is clamped.
    /// Anything unusable leaves an empty list, which is exactly what a legacy server's answer
    /// looks like — the path then follows as plain waypoints.
    /// </summary>
    public static IReadOnlyList<PathLeg> Parse(IReadOnlyList<FindPathLegResponse>? wire, int waypointCount)
    {
        if (wire is not { Count: > 0 } || waypointCount <= 0)
            return None;

        var legs = new List<PathLeg>(wire.Count);
        foreach (var w in wire)
        {
            var mode = w.Mode?.ToLowerInvariant() switch
            {
                "walk" => LegMode.Walk,
                "fly" => LegMode.Fly,
                _ => (LegMode?)null, // a mode we cannot travel: drop the leg, keep the path
            };
            if (mode is not { } m || w.First < 0 || w.First >= waypointCount)
                continue;
            var count = Math.Min(w.Count, waypointCount - w.First);
            if (count <= 0)
                continue;
            legs.Add(new PathLeg(m, ParseTransition(w.Enter), w.EnterArg ?? 0, w.First, count));
        }

        if (legs.Count == 0)
            return None;

        legs.Sort((a, b) => a.First.CompareTo(b.First));
        return legs;
    }

    /// <summary>
    /// The index of the leg covering a waypoint index — the index of the waypoint currently at
    /// the head of the path, i.e. how many have been passed. -1 in a gap between spans (partial
    /// coverage is tolerated: the path-level mode applies there) and past the last span.
    /// Assumes <paramref name="legs"/> is ordered by <see cref="PathLeg.First"/>, as Parse returns it.
    /// </summary>
    public static int IndexAt(IReadOnlyList<PathLeg> legs, int waypointIndex)
    {
        for (var i = 0; i < legs.Count; i++)
        {
            if (legs[i].Covers(waypointIndex))
                return i;
            if (legs[i].First > waypointIndex)
                break; // ordered: no later leg can cover it either
        }
        return -1;
    }

    /// <summary>Unknown transitions are informational, never fatal: the leg survives with no
    /// transition, and the follower logs that it is following it as-is.</summary>
    private static LegTransition? ParseTransition(string? enter) => enter?.ToLowerInvariant() switch
    {
        "mount" => LegTransition.Mount,
        "jumpoff" => LegTransition.JumpOff,
        "land" => LegTransition.Land,
        "dismount" => LegTransition.Dismount,
        "teleport" => LegTransition.Teleport,
        _ => null,
    };
}
