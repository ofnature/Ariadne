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
    public bool TaskInProgress => _pending != null || _teleport != null;
    public string LastResult { get; private set; } = "";
    public int RetriesUsed { get; private set; }

    /// <summary>"teleporting to X…" while a teleport leg is in flight; "" otherwise.</summary>
    public string TeleportStatus => _teleport is { } t ? $"teleporting to {t.Name}…" : "";

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
    private readonly Action<string> _log;

    private readonly FutilityCounter _futility;
    private Task<MeshBroker.PathAnswer>? _pending;
    private IGoal? _goal;
    private bool _fly;
    private Vector3 _plannedTarget;
    private TeleportService.Plan? _teleport;
    private DateTime _teleportDeadline;
    private DateTime? _teleportIdleSince;

    public MoveRequest(MeshBroker broker, PathFollower follower, AriadneConfig config, Func<Vector3?> playerPosition,
        Func<ulong, (Vector3 Position, float HitboxRadius)?> resolveObject, TeleportService? teleports, Action<string> log)
    {
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

    public bool MoveTo(Vector3 dest, bool fly, float range = 0) => Move(new GoalNear(dest, range), fly);

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

    public bool Move(IGoal goal, bool fly)
    {
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
        _futility.Reset();
        _goal = goal;
        _fly = fly;

        // Teleport leg first when a crystal wins on ETA. Ariadne's own surface only — the
        // vnavmesh.* compat gates never reach here with aetherytes on. Escapes never teleport.
        if (UseAetherytes && _teleports != null && goal is not GoalAway
            && _teleports.TryPlan(from.Value, goal.Target, fly) is { } plan)
        {
            if (_teleports.Start(plan.AetheryteId))
            {
                _teleport = plan;
                _teleportDeadline = DateTime.UtcNow + TeleportTimeout;
                _teleportIdleSince = null;
                LastResult = TeleportStatus;
                _log($"[Move] {goal.Describe()} is {plan.DirectSeconds:0}s direct, {plan.ViaSeconds:0}s via {plan.Name} — teleporting first");
                return true;
            }
            _log($"[Move] Lifestream declined the teleport to {plan.Name} — going direct");
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
        _follower.Stop();
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

        if (_pending is { IsCompleted: true } task)
        {
            _pending = null;
            Promote(task);
            return;
        }

        if (_pending != null || _goal == null || !_follower.IsRunning || _follower.IsExternalPath)
            return;
        if (_playerPosition() is not { } pos)
            return;
        if (_goal.IsSatisfied(pos))
        {
            _follower.Stop();
            LastResult = "goal reached";
            _log($"[Move] goal reached: {_goal.Describe()}");
            _goal = null;
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

    private void Promote(Task<MeshBroker.PathAnswer> task)
    {
        // Carry the classified reason through to the log and the window. "no path" alone
        // reads as a mesh problem whatever went wrong, which is how a dead service spent an
        // evening looking like a doorway that would not path.
        var answer = task.IsCompletedSuccessfully
            ? task.Result
            : new MeshBroker.PathAnswer("failed", [], null, false);
        if (answer.Waypoints.Count == 0)
        {
            LastResult = $"no path ({answer.Result})";
            _log($"[Move] no path to {_goal?.Describe()} [{answer.Result}]");
            _goal = null;
            return;
        }

        LastResult = $"{answer.Waypoints.Count} waypoints";
        _plannedTarget = _goal?.Target ?? answer.Waypoints[^1];
        _follower.Move(answer.Waypoints, _fly, _goal?.PlannerTolerance ?? 0, external: false); // ours: stall recovery may re-path it
    }

    private void UpdateTeleport()
    {
        var plan = _teleport!;
        var now = DateTime.UtcNow;
        if (now > _teleportDeadline)
        {
            _teleport = null;
            LastResult = $"teleport to {plan.Name} never landed";
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
        if (Vector3.Distance(pos.Value, plan.Position) <= TeleportArrivalRadius)
        {
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
        LastResult = "pathfinding…";
        _log($"[Move] {(_fly ? "fly" : "walk")} to {goal.Describe()}");
        // Hand the range to the planner as a goal tolerance, not just to the follower. An NPC
        // behind a counter is an off-mesh goal: without a tolerance the server can only answer
        // "targetOffMesh", while with one it re-plans to the nearest reachable spot and trims
        // the tail, so the route ends where you can actually interact from. The follower still
        // gets the range too - it decides when to stop walking.
        _pending = goal is GoalAway away
            ? ResolveAwayAsync(away, from.Value)
            : _broker.FindPathDetailedAsync(from.Value, goal.Target, _fly, goal.PlannerTolerance > 0 ? goal.PlannerTolerance : null);
        return true;
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
