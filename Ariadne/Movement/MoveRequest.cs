using Ariadne.Config;
using Ariadne.Travel;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Movement;

// "Get me to the goal": optionally teleports first (Lifestream, when an attuned aetheryte
// beats travelling directly), asks the broker (Mnemosyne) for a path off-thread, hands the
// result to the follower on the framework thread, checks the goal live while following,
// and owns stall recovery — on a stall it re-paths from the current position, budgeted by
// ground gained. Ported from vnavmesh's AsyncMoveRequest; goals, teleport legs and the
// retry loop are the new parts.
internal sealed class MoveRequest : IDisposable
{
    public bool TaskInProgress => _pending != null || _teleport != null || _meshWait.Waiting
        || _held != null || _dismounting;
    public string LastResult { get; private set; } = "";
    public int RetriesUsed { get; private set; }

    /// <summary>"teleporting to X…" while a teleport leg is in flight; "" otherwise.</summary>
    public string TeleportStatus => _teleport is { } t ? $"teleporting to {t.Name}…" : "";

    /// <summary>What the move is doing while nothing is being followed yet, for the window.</summary>
    public string PhaseText => _teleport != null ? TeleportStatus
        : _held != null ? "calling the mount…"
        : _dismounting ? "putting the mount away…"
        : "pathfinding…";

    /// <summary>Per-session override of config.PreferFlying (set over IPC); null = config.</summary>
    public bool? PreferFlyingOverride { get; set; }
    public bool PreferFlying => PreferFlyingOverride ?? _config.PreferFlying;

    /// <summary>Per-session override of config.UseAetherytes (set over IPC); null = config.</summary>
    public bool? UseAetherytesOverride { get; set; }
    public bool UseAetherytes => UseAetherytesOverride ?? _config.UseAetherytes;

    private const float TeleportArrivalRadius = 40f; // you land within a few yalms of the crystal; plazas are big
    private const float GoalDriftRepath = 5f;        // a wandering NPC moved this far from where we pathed
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TeleportIdleGrace = TimeSpan.FromSeconds(5); // Lifestream not busy, still far: it never went

    private readonly MeshBroker _broker;
    private readonly PathFollower _follower;
    private readonly AriadneConfig _config;
    private readonly Func<Vector3?> _playerPosition;
    private readonly Func<ulong, (Vector3 Position, float HitboxRadius)?> _resolveObject;
    private readonly TeleportService? _teleports;
    private readonly FlightControl? _flight;
    private readonly Action<string> _log;

    private static readonly TimeSpan LandingGrace = TimeSpan.FromSeconds(2); // let the descent finish before dismounting
    private readonly BudgetedRetry _mount = new(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(8));
    private readonly BudgetedRetry _dismount = new(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(8));
    private MeshBroker.PathAnswer? _held; // a route with a leg in the air, waiting for the mount
    private bool _smart;        // the caller is Ariadne's own surface: teleports and flight are allowed
    private bool _upgraded;     // asked for a walk, flying instead
    private bool _mountedByUs;  // so only a mount we called is put away
    private bool _dismounting;
    private DateTime _dismountSince;

    private readonly FutilityCounter _futility;
    private Task<MeshBroker.PathAnswer>? _pending;
    private IGoal? _goal;
    private bool _fly;
    private Vector3 _plannedTarget;
    private readonly MeshWait _meshWait = new();
    private TeleportService.Plan? _teleport;
    private DateTime _teleportDeadline;
    private DateTime? _teleportIdleSince;
    private bool _landed;
    private bool _following; // the follower is running a path we handed it for _goal

    /// <summary>An exact destination (range 0) counts as reached within this much horizontal
    /// error: the route ends on the mesh's version of the point, not the caller's.</summary>
    internal const float ExactArrivalSlack = 1f;

    /// <summary>What a finished route amounts to. A ranged goal is reached only inside its
    /// range; anything else names how far short the closest reachable point was, so a
    /// consumer retrying on LastResult knows that retrying will not help.</summary>
    internal static string DescribeEnd(IGoal goal, Vector3 player)
    {
        var outside = goal.DistanceOutside(player);
        var slack = goal.PlannerTolerance > 0 ? 0f : ExactArrivalSlack;
        return outside <= slack ? "goal reached" : $"closest reachable point, {outside:0.0}y short";
    }
    private string _zone = ""; // the territory the current plan's coordinates belong to

    public MoveRequest(MeshBroker broker, PathFollower follower, AriadneConfig config, Func<Vector3?> playerPosition,
        Func<ulong, (Vector3 Position, float HitboxRadius)?> resolveObject, TeleportService? teleports,
        FlightControl? flight, Action<string> log)
    {
        _flight = flight;
        _broker = broker;
        _follower = follower;
        _config = config;
        _playerPosition = playerPosition;
        _resolveObject = resolveObject;
        _teleports = teleports;
        _log = log;
        _futility = new FutilityCounter(config.ProgressMinGain, config.StallRetries);
        _follower.OnStalled += OnStalled;
    }

    public void Dispose()
    {
        _follower.OnStalled -= OnStalled;
        _pending = null; // don't block unload on a slow pipe; the task completes into nothing
    }

    /// <param name="smart">False for callers that expect vnavmesh's behaviour: the vnavmesh.*
    /// compat gates. A consumer written against vnavmesh asked for a walk; a teleport cast, a
    /// loading screen or a mount appearing in the middle of it is not what it planned around.</param>
    public bool MoveTo(Vector3 dest, bool fly, float range = 0, bool smart = true)
        => Move(new GoalNear(dest, range), fly, smart);

    /// <summary>Path to a game object and stop inside interact range (config.InteractRange +
    /// its hitbox), following it if it wanders.</summary>
    public bool MoveToInteract(ulong gameObjectId, bool fly)
    {
        var goal = GoalInteract.For(() => _resolveObject(gameObjectId), _config.InteractRange);
        if (goal == null)
        {
            LastResult = "no such object";
            return false;
        }
        return Move(goal, fly);
    }

    /// <summary>Get at least <paramref name="distance"/> away from a point, to a reachable spot.</summary>
    public bool MoveAway(Vector3 from, float distance, bool fly) => Move(new GoalAway(from, distance), fly);

    public bool Move(IGoal goal, bool fly, bool smart = true)
    {
        using var trace = MainThreadTrace.Enter("move request");
        if (TaskInProgress)
        {
            _log("[Move] request already in progress");
            return false;
        }
        var from = _playerPosition();
        if (from == null)
        {
            LastResult = "no player";
            return false;
        }

        RetriesUsed = 0;
        _meshWait.Reset();
        _futility.Reset();
        _goal = goal;
        _smart = smart;
        _upgraded = false;
        _mountedByUs = false;

        // Fly when the zone allows it, whatever was asked for. Decided before the teleport
        // leg, because how long the direct trip takes depends on how it is travelled.
        if (smart && !fly && PreferFlying && _flight != null && goal is not GoalAway
            && FlightPreference.ShouldUpgrade(TeleportPlanner.Horizontal(from.Value, goal.Target), _config.FlyMinDistance, _flight.CanFlyHere()))
        {
            fly = true;
            _upgraded = true;
            _log($"[Move] flight is unlocked here and {goal.Describe()} is {TeleportPlanner.Horizontal(from.Value, goal.Target):0}y away — flying instead of walking");
        }
        _fly = fly;

        // Teleport leg first when a crystal wins on ETA. Ariadne's own surface only — the
        // vnavmesh.* compat gates never reach here with aetherytes on. Escapes never teleport.
        if (smart && UseAetherytes && _teleports != null && goal is not GoalAway)
        {
            if (_teleports.TryPlan(from.Value, goal.Target, fly, out var why) is { } plan)
            {
                if (_teleports.Start(plan.AetheryteId))
                {
                    _teleport = plan;
                    _teleportDeadline = DateTime.UtcNow + TeleportTimeout;
                    _teleportIdleSince = null;
                    _landed = false;
                    LastResult = TeleportStatus;
                    _log($"[Move] {goal.Describe()} is {plan.DirectSeconds:0}s direct, {plan.ViaSeconds:0}s via {plan.Name} — teleporting first");
                    return true;
                }
                _log($"[Move] Lifestream declined the teleport to {plan.Name} — going direct");
            }
            else if (why.Length > 0)
            {
                _log($"[Move] no teleport leg: {why}");
            }
        }
        return Request();
    }

    public void Stop()
    {
        if (_teleport != null)
        {
            _teleports?.Abort();
            _teleport = null;
        }
        _pending = null;
        _goal = null;
        _following = false;
        _held = null;
        _dismounting = false; // stopped by hand: the mount is the player's to keep or put away
        _meshWait.Reset();
        _follower.Stop();
    }

    /// <summary>
    /// What a zone change means for a path in flight: its waypoints are coordinates in the zone we
    /// just left, and following them drives the character at a spot that no longer means anything.
    /// Dropping the path is the only honest answer, whoever supplied the waypoints.
    ///
    /// The test is the territory, not the cache key. A festival layer or a shared-group state
    /// changes the key inside one zone — the mesh changed, the coordinates did not — and a
    /// consumer-supplied path there is still its owner's to keep
    /// (docs/externally-supplied-paths.md).
    /// </summary>
    public void OnZoneChanged(string cacheKey)
    {
        if (cacheKey.Length == 0)
            return; // layout unloading: the next key is the one worth comparing

        var territory = cacheKey.Split(["__"], StringSplitOptions.None)[0];
        var previous = _zone;
        _zone = territory;
        if (previous.Length == 0 || previous == territory)
            return; // the first key of the session, or still in the same territory
        if (!_follower.IsRunning && !TaskInProgress)
            return; // nothing in flight to drop

        Stop();
        LastResult = "zone changed";
        _log($"[Move] zone changed ({previous} -> {territory}) — path dropped, the old coordinates mean nothing here");
    }

    /// <summary>Framework-thread tick: drives the teleport leg, promotes a finished pathfind
    /// into movement, and ends the path early when the goal says so.</summary>
    public void Update()
    {
        if (_teleport != null)
        {
            UpdateTeleport();
            return;
        }

        if (_held != null)
        {
            UpdateMounting();
            return;
        }
        if (_dismounting)
        {
            UpdateDismounting();
            return;
        }

        if (_pending is { IsCompleted: true } task)
        {
            _pending = null;
            Promote(task);
            return;
        }

        // A pathfind that came back "meshNotReady" is waiting on the zone's volume, not failing:
        // re-ask on the timer rather than treating the move as impossible. Promote keeps the goal
        // alive for exactly this, and the retry budget is what stops the waiting from being eternal.
        if (_pending == null && _goal != null && _meshWait.Due(DateTime.UtcNow))
        {
            _meshWait.ClearTimer();
            Request();
            return;
        }

        if (_pending != null || _goal == null)
            return;
        if (_follower.IsRunning && _follower.IsExternalPath)
        {
            _following = false; // a consumer replaced our path with its own: the goal went with it
            _goal = null;
            return;
        }
        if (!_follower.IsRunning)
        {
            if (_following)
                Conclude();
            return;
        }
        if (_playerPosition() is not { } pos)
            return;
        // In the air on a mount we called, the range is met overhead long before the ground is:
        // fly the route to its end, which is on the ground, rather than dropping out of the sky.
        var overhead = _mountedByUs && _flight is { IsFlying: true };
        if (!overhead && _goal.IsSatisfied(pos))
        {
            _follower.Stop();
            var reached = _goal;
            Finish("goal reached", $"[Move] goal reached: {reached.Describe()}");
            return;
        }
        var drift = Vector3.Distance(_goal.Target, _plannedTarget);
        if (drift > GoalDriftRepath)
        {
            _log($"[Move] goal moved {drift:0}y since the path was planned — re-pathing");
            _follower.Stop();
            Request();
        }
    }

    // The follower emptied the path itself. Arrival is judged against the goal, never against
    // the route's last waypoint: for an NPC behind a counter those are two yalms apart.
    private void Conclude()
    {
        _following = false;
        var goal = _goal!;
        _goal = null;
        if (!_follower.FinishedNaturally || _playerPosition() is not { } pos)
        {
            LastResult = "stopped"; // cancelled (player input), not arrived
            _log($"[Move] stopped before reaching {goal.Describe()}");
            return;
        }
        var result = DescribeEnd(goal, pos);
        Finish(result, $"[Move] route to {goal.Describe()} ended: {result}");
    }

    // Arrived, one way or the other. A mount we called for a caller that asked to walk is put
    // away again: it expects to be on foot when the move reports done, and TaskInProgress
    // stays true until it is.
    private void Finish(string result, string logLine)
    {
        LastResult = result;
        _log(logLine);
        _goal = null;
        _following = false;
        if (_upgraded && _mountedByUs && _flight is { IsMounted: true })
        {
            _dismounting = true;
            _dismountSince = DateTime.UtcNow;
            _dismount.Reset();
        }
    }

    // The route has a leg in the air and the character is on foot: the mount first.
    private void UpdateMounting()
    {
        var held = _held!;
        if (_flight!.IsMounted)
        {
            _held = null;
            _mountedByUs = true;
            Follow(held);
            return;
        }
        var now = DateTime.UtcNow;
        if (_mount.Due(now))
        {
            _flight.TryMount();
        }
        else if (_mount.GaveUp)
        {
            _held = null;
            _log("[Move] the mount would not come out — walking instead");
            _fly = false;
            _upgraded = false;
            Request();
        }
    }

    private void UpdateDismounting()
    {
        if (!_flight!.IsMounted)
        {
            _dismounting = false;
            _mountedByUs = false;
            return;
        }
        var now = DateTime.UtcNow;
        if (_flight.IsFlying && now - _dismountSince < LandingGrace)
            return; // still coming down: dismounting in the air is a fall
        if (_dismount.Due(now))
        {
            _flight.TryDismount();
        }
        else if (_dismount.GaveUp)
        {
            _dismounting = false;
            _log("[Move] the mount would not go away — leaving it out");
        }
    }

    private void Promote(Task<MeshBroker.PathAnswer> task)
    {
        _following = false;
        // Carry the classified reason through to the log and the window. "no path" alone
        // reads as a mesh problem whatever went wrong, which is how a dead service spent an
        // evening looking like a doorway that would not path.
        var answer = task.IsCompletedSuccessfully
            ? task.Result
            : new MeshBroker.PathAnswer("failed", [], null, false);
        if (answer.Waypoints.Count == 0 && _meshWait.Record(answer.Result, DateTime.UtcNow))
        {
            // The service answers `meshNotReady` while it is still decoding the zone's volume, off
            // the request (measured: ~1.9 s for a field zone). Retrying is the whole point of the
            // result — see MeshWait, and the consumer contract in MeshBroker.
            LastResult = "waiting for mesh";
            _log($"[Move] this zone's volume is still loading — retrying in {_meshWait.RetryDelayMs} ms ({_meshWait.Retries}/{_meshWait.MaxRetries})");
            return; // the goal stays: we still want to go there
        }
        if (answer.Waypoints.Count == 0)
        {
            LastResult = $"no path ({answer.Result})";
            _log($"[Move] no path to {_goal?.Describe()} [{answer.Result}]");
            _goal = null;
            return;
        }
        _meshWait.Reset(); // a real route: any waiting is over

        LastResult = $"{answer.Waypoints.Count} waypoints";
        _plannedTarget = _goal?.Target ?? answer.Waypoints[^1];
        // ours: stall recovery may re-path it, and its legs are ours to execute (mode switches
        // and the `land` transition; mount/dismount/teleport are logged, not performed yet).
        // Destination tolerance 0 on purpose. The follower measures it against the route's LAST
        // WAYPOINT, which for an off-mesh goal is the closest reachable point, not the goal:
        // the Flame Personnel Officer stands 2 y behind the counter edge the route ends at, so
        // "within 3 y of the end" ended the move up to 5 y from the NPC (SealBreaker,
        // 2026-09-27). vnavmesh never shows this because its last waypoint is the goal itself.
        // The range is the goal's, and Update checks it against the target every tick.
        if (_smart && _flight != null && !_flight.IsMounted
            && FlightPreference.NeedsFlight(answer.Legs, answer.Result, _fly))
        {
            _held = answer;
            _mount.Reset();
            LastResult = "calling the mount…";
            _log($"[Move] the route to {_goal?.Describe()} flies — calling the mount first");
            return;
        }
        Follow(answer);
    }

    private void Follow(MeshBroker.PathAnswer answer)
    {
        LastResult = $"{answer.Waypoints.Count} waypoints";
        if (_playerPosition() is { } at)
            _log($"[Move] following {answer.Waypoints.Count} waypoints [{answer.Result}]: from {at:f1}, first {answer.Waypoints[0]:f1} "
                + $"({answer.Waypoints[0].Y - at.Y:+0.0;-0.0}y from your feet), last {answer.Waypoints[^1]:f1}");
        _follower.Move(answer.Waypoints, _fly, destinationTolerance: 0, external: false, legs: answer.Legs);
        _following = true;
    }

    private void UpdateTeleport()
    {
        var plan = _teleport!;
        var now = DateTime.UtcNow;
        if (now > _teleportDeadline)
        {
            _teleport = null;
            LastResult = _landed ? $"landed at {plan.Name} but the zone never became ready" : $"teleport to {plan.Name} never landed";
            _log($"[Move] {LastResult} — giving up");
            _goal = null;
            return;
        }
        if (_teleports!.Busy)
        {
            _teleportIdleSince = null; // casting / confirming / loading
            return;
        }
        var pos = _playerPosition();
        if (pos == null)
            return; // loading screen
        if (TeleportPlanner.Horizontal(pos.Value, plan.Position) <= TeleportArrivalRadius) // crystal Y may be unknown
        {
            // The player object stands at the crystal before the zone has finished loading and
            // the broker has re-resolved the mesh; pathing then is rejected as not ready.
            _landed = true;
            if (!_broker.NavIsReady)
                return;
            _teleport = null;
            _log($"[Move] landed at {plan.Name} — pathing on to {_goal!.Describe()}");
            Request();
            return;
        }
        // Not busy, not there: either the queue hasn't picked it up yet or it was refused
        // (combat, a dialog). A few seconds of that and the leg is dropped, not the goal.
        _teleportIdleSince ??= now;
        if (now - _teleportIdleSince > TeleportIdleGrace)
        {
            _teleport = null;
            _log($"[Move] teleport to {plan.Name} never started — going direct");
            Request();
        }
    }

    private bool Request()
    {
        if (_pending != null)
        {
            _log("[Move] pathfind already in progress");
            return false;
        }
        var from = _playerPosition();
        if (from == null)
        {
            LastResult = "no player";
            return false;
        }
        var goal = _goal!;
        _following = false;
        LastResult = "pathfinding…";
        _log($"[Move] {(_fly ? "fly" : "walk")} to {goal.Describe()}");
        // Hand the range to the planner as a goal tolerance, not just to the follower. An NPC
        // behind a counter is an off-mesh goal: without a tolerance the server can only answer
        // "targetOffMesh", while with one it re-plans to the nearest reachable spot and trims
        // the tail, so the route ends where you can actually interact from. The follower still
        // gets the range too - it decides when to stop walking.
        // In the air or in the water the character is not at the ground's height, and a route
        // that starts below it is the expected one.
        var onTheGround = !_fly && _flight is not ({ IsFlying: true } or { InWater: true });
        _pending = goal is GoalAway away
            ? ResolveAwayAsync(away, from.Value)
            : PlanAsync(from.Value, goal.Target, goal.PlannerTolerance > 0 ? goal.PlannerTolerance : null, onTheGround);
        return true;
    }

    private const float SurfaceSearchRadius = 6f;

    // Ask for the route, and check that it begins where the character stands (RouteSanity).
    // When it begins under the feet, find the surface nearby and plan from there; when there
    // is none, say so instead of handing the follower a route through the ground.
    private async Task<MeshBroker.PathAnswer> PlanAsync(Vector3 from, Vector3 to, float? tolerance, bool onTheGround)
    {
        var answer = await _broker.FindPathDetailedAsync(from, to, _fly, tolerance).ConfigureAwait(false);
        if (!onTheGround || RouteSanity.StartOffset(from, answer.Waypoints) is not { } offset)
            return answer;

        _log($"[Move] the route begins {RouteSanity.Describe(offset)} at {answer.Waypoints[0]:f1}: the mesh has no "
            + $"surface where you stand ({from:f1}) and the start fell onto another level — looking for the surface nearby");
        var surface = await _broker.NearestPointAsync(from, SurfaceSearchRadius, RouteSanity.MaxStartOffset, reachableOnly: false)
            .ConfigureAwait(false);
        if (surface is { } s && MathF.Abs(s.Y - from.Y) <= RouteSanity.MaxStartOffset)
        {
            var retry = await _broker.FindPathDetailedAsync(s, to, _fly, tolerance).ConfigureAwait(false);
            if (retry.Waypoints.Count > 0 && RouteSanity.StartOffset(from, retry.Waypoints) == null)
            {
                _log($"[Move] re-planned from the surface at {s:f1}, {Vector3.Distance(s, from):0.0}y from where you stand");
                return retry;
            }
        }
        _log("[Move] no surface within reach to plan from — refusing the route that runs on the other level");
        return new MeshBroker.PathAnswer("startOffSurface", [], surface, false);
    }

    // An escape point: on the ring at the wanted distance, starting straight away from the
    // threat through the player and fanning out from there, first reachable mesh point wins.
    private async Task<MeshBroker.PathAnswer> ResolveAwayAsync(GoalAway goal, Vector3 from)
    {
        var dir = from - goal.From;
        dir.Y = 0;
        dir = dir.LengthSquared() < 0.01f ? new Vector3(1, 0, 0) : Vector3.Normalize(dir);
        foreach (var deg in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
        {
            var (sin, cos) = MathF.SinCos(deg * MathF.PI / 180f);
            var d = new Vector3(dir.X * cos - dir.Z * sin, 0, dir.X * sin + dir.Z * cos);
            var candidate = goal.From + d * (goal.Distance + 2f);
            candidate.Y = from.Y;
            var point = await _broker.NearestPointAsync(candidate, 5f, 10f, reachableOnly: true).ConfigureAwait(false);
            if (point is not { } p || Vector3.Distance(p, goal.From) < goal.Distance)
                continue;
            goal.Target = p;
            return await _broker.FindPathDetailedAsync(from, p, _fly, null).ConfigureAwait(false);
        }
        return new MeshBroker.PathAnswer("noEscapePoint", [], null, false);
    }

    private void OnStalled(Vector3 destination, bool fly, float range)
    {
        // Externally-supplied paths (Path.MoveTo) are not ours to recover: their waypoints
        // may encode knowledge the mesh lacks — Minerva's dodge corners bend around AOEs a
        // mesh re-path would walk straight through. Leave the path running; the owner polls
        // Path.StallCount and re-plans with the geometry knowledge it actually has.
        if (_follower.IsExternalPath || _goal == null)
            return;

        // Attempts are budgeted by ground gained, not by count: a re-path that closed
        // ProgressMinGain since the last one clears the futility count (Odysseus's rule —
        // progress buys the clock back). Only consecutive futile recoveries give up.
        var remaining = _playerPosition() is { } pos ? (destination - pos).Length() : float.MaxValue;
        if (_futility.RecordAttempt(remaining))
        {
            _log($"[Move] {_futility.FutileAttempts} recoveries without gaining ground — giving up ({remaining:0}y short)");
            LastResult = $"stuck ({remaining:0}y short)";
            _follower.Stop();
            _goal = null;
            return;
        }

        RetriesUsed++;
        _log($"[Move] stalled at {remaining:0}y out — re-pathing (futile {_futility.FutileAttempts}/{_config.StallRetries})");
        _follower.Stop();
        Request();
    }
}
