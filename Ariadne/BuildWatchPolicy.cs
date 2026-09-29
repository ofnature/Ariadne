using System;

namespace Ariadne;

/// <summary>Why the broker is watching a zone, which decides what ends the watch.</summary>
internal enum WatchMode
{
    Missing, // no usable mesh: any current mesh ends it
    Forced,  // an explicit capture: only the build we asked for ends it
    Upgrade, // the only mesh is Mnemosyne's offline baseline: a live-built one ends it
}

internal enum WatchAction
{
    Requery,      // a mesh is there: run the zone query again and act on it
    Wait,         // nothing to do this poll but keep watching
    SendCapture,  // nobody is building and we want a build: (re)send the capture
    StopBuilding, // the capture was sent as often as we will; stop asking, keep watching
}

/// <summary>
/// What the broker does on each poll while the current zone has no usable mesh, and what it
/// tells consumers meanwhile. Extracted like <see cref="MeshRetryPolicy"/> so it can be tested
/// without the game or the pipe.
///
/// <para>Found in the field 2026-09-26/27 (Odysseus faulting "navmesh not ready" in four zones,
/// each within a minute of the mesh landing): the broker reported Nav.BuildProgress = -1 for
/// the whole of an out-of-process build, so a consumer that waits on a build and faults on
/// "not ready and not building" faulted mid-build. And the service builds one zone at a time —
/// a capture that arrives while another key is building is acknowledged and dropped — so with
/// a fleet entering zones together, most captures were never built; the broker polled five
/// minutes for a mesh nobody was making and then refused to ask again that session.</para>
///
/// Pure and clock-free: the broker owns the waiting and the IO, this owns the decision.
/// </summary>
internal static class BuildWatchPolicy
{
    /// <summary>Captures sent for one watch. A build that fails is re-kicked by a re-send, so
    /// this also bounds how often a zone that cannot build is attempted.</summary>
    public const int MaxSends = 3;

    /// <param name="ready">A current mesh exists: in vnavmesh's cache (it may have finished its
    /// own in-game build) or in Mnemosyne.</param>
    /// <param name="reachable">The service answered this poll.</param>
    /// <param name="building">The service is building something. It reports one build at a
    /// time and, for a stale key, cannot say whose — so any build means "not our turn yet or
    /// ours is running", and either way the answer is to wait.</param>
    /// <param name="wantBuild">Build-on-miss is on and the service has not refused this zone.</param>
    /// <param name="sends">Captures sent so far in this watch.</param>
    public static WatchAction Decide(bool ready, bool reachable, bool building, bool wantBuild, int sends)
    {
        if (ready)
            return WatchAction.Requery;
        if (!reachable || building || !wantBuild)
            return WatchAction.Wait;
        return sends < MaxSends ? WatchAction.SendCapture : WatchAction.StopBuilding;
    }

    /// <summary>
    /// The mesh at this path is Mnemosyne's OFFLINE baseline: built from the game's layout files
    /// with nobody in the zone, so without the event, festival and shared-group layers that only
    /// exist in a running game. Good enough to serve while nothing better exists, not good
    /// enough to keep. Found 2026-09-28 in the Resonatorium (an instanced quest zone, where
    /// those layers are most of the room): 987 polys in 82 islands, the platform the player
    /// stood on cut off from the floor, and a route drawn across a table. The service had
    /// built it for a path request that arrived before Ariadne's capture, and because a mesh
    /// then existed, the capture was never sent.
    ///
    /// <para>The store is told from the path, since the protocol has no `source` field yet:
    /// <c>%APPDATA%\Mnemosyne\built\</c> holds offline builds, <c>captured\</c> the live
    /// ones, and vnavmesh's own cache its in-game builds.</para>
    /// </summary>
    public static bool IsOfflineBaseline(string? meshPath)
    {
        if (string.IsNullOrEmpty(meshPath))
            return false;
        var parts = meshPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && parts[^2].Equals("built", StringComparison.OrdinalIgnoreCase)
            && parts[^3].Equals("Mnemosyne", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nav.BuildProgress for consumers: 0..1 while a mesh is on its way, -1 when
    /// nothing we know of is making one. "On its way" includes waiting for our turn behind
    /// another zone's build — a consumer should wait through that, not fault.</summary>
    public static float Progress(bool reachable, bool building, float serverProgress, bool wantBuild, int sends)
    {
        if (!reachable)
            return -1;
        if (building)
            return Math.Clamp(serverProgress, 0f, 1f);
        return wantBuild && sends < MaxSends ? 0f : -1f;
    }
}
