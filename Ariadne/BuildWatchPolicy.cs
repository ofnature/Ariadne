using System;

namespace Ariadne;

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
