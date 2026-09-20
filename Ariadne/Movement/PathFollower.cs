using Ariadne.Config;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Movement;

// Drives the character along a waypoint list via the vendored input hooks. Ported from
// vnavmesh's FollowPath with three changes: config is Ariadne's; stall handling is
// window-based (StallDetector) with the decision left to the caller through OnStalled —
// MoveRequest turns that into re-path attempts, which is the recovery vnavmesh lacks; and a
// path may carry legs (multi-modal spans with transitions), whose walk/fly mode switches at
// leg boundaries and whose `land` transition is executed here.
internal sealed class PathFollower : IDisposable
{
    public bool MovementAllowed = true;
    public float Tolerance = 0.25f;

    public IReadOnlyList<Vector3> Waypoints => _waypoints;
    public bool IsRunning => _waypoints.Count > 0;
    public bool IgnoreDeltaY { get; private set; }
    public float DestinationTolerance { get; private set; }

    /// <summary>The leg being followed ("leg 2/3: walk"), "landing" while a `land` transition
    /// holds the path, "" for a path without legs (a legacy server's answer, or an
    /// externally-supplied one). Shown in the window.</summary>
    public string CurrentLeg { get; private set; } = "";

    /// <summary>True when the active path was supplied by an external caller (Path.MoveTo)
    /// rather than MoveRequest. Stall recovery must not touch external paths: their
    /// waypoints may encode knowledge the mesh doesn't have (Minerva's danger-aware dodge
    /// corners), and a mesh re-path would discard exactly that (docs/externally-supplied-paths.md).</summary>
    public bool IsExternalPath { get; private set; }

    /// <summary>Stalls detected on the current path (external callers poll this over IPC to
    /// re-plan with their own geometry knowledge). Reset by Move/Stop.</summary>
    public int StallCount { get; private set; }

    /// <summary>Continuous direct-steer target (SteerTo) — no waypoints, no mesh, no stall
    /// machinery; the owner re-issues per tick and Ariadne just drives the input hook.
    /// Auto-clears on arrival. For micro-dodges when a path is overkill.</summary>
    public Vector3? SteerTarget { get; private set; }
    public bool IsSteering => SteerTarget != null;

    /// <summary>Yalms left to travel: steer distance, or player→wp0→…→end along the path.
    /// -1 when idle. Deadline-driven consumers (dodge vs cast timer) poll this.</summary>
    public float RemainingDistance
    {
        get
        {
            var player = Service.ObjectTable.LocalPlayer;
            if (player == null)
                return -1;
            if (SteerTarget is { } steer)
                return (steer - player.Position).Length();
            if (_waypoints.Count == 0)
                return -1;
            var total = (_waypoints[0] - player.Position).Length();
            for (var i = 1; i < _waypoints.Count; i++)
                total += (_waypoints[i] - _waypoints[i - 1]).Length();
            return total;
        }
    }

    /// <summary>Raised on the framework thread when no progress is made for the configured
    /// window while a path is active. Args: final destination, fly, destination tolerance.
    /// The follower keeps going unless the handler calls Stop().</summary>
    public event Action<Vector3, bool, float>? OnStalled;

    private readonly AriadneConfig _config;
    private readonly OverrideCamera _camera = new();
    private readonly OverrideMovement _movement = new();
    private readonly PathIsRunningSignal _signal;
    private readonly List<Vector3> _waypoints = [];
    private readonly StallDetector _stall;
    private readonly ProgressBudget _progress;

    private readonly TakeoffAttempt _takeoff = new();
    private readonly LandingAttempt _landing = new();

    private IReadOnlyList<PathLeg> _legs = Array.Empty<PathLeg>();
    private int _legIndex = -1;
    private int _legsConsumed;   // waypoints popped off the head; legs index into the original array
    private bool _landingHold;   // a `land` transition is waiting for the ground
    private bool _flyPath = true; // the request-level mode, for waypoints no leg covers

    private Vector3? _posPreviousFrame;
    private DateTime _nextJump;
    private float? _pathTolerance;

    public PathFollower(AriadneConfig config, PathIsRunningSignal signal)
    {
        _config = config;
        _signal = signal;
        _stall = new StallDetector(config.StallMinProgress, config.StallWindowMs);
        _progress = new ProgressBudget(config.ProgressMinGain, config.ProgressWindowMs);
    }

    public void Dispose()
    {
        _signal.Set(false);
        _camera.Dispose();
        _movement.Dispose();
    }

    // external defaults to true: any caller that doesn't explicitly claim ownership
    // (only MoveRequest does) is treated as supplying its own waypoints
    public void Move(List<Vector3> waypoints, bool fly, float destinationTolerance = 0,
        bool external = true, float? waypointTolerance = null, IReadOnlyList<PathLeg>? legs = null)
    {
        _waypoints.Clear();
        _waypoints.AddRange(waypoints);
        _flyPath = fly;
        IgnoreDeltaY = !fly;
        _takeoff.Reset();
        _landing.Reset();
        DestinationTolerance = destinationTolerance;
        IsExternalPath = external;
        _pathTolerance = waypointTolerance; // per-path override; null = the global setting
        SteerTarget = null;
        StallCount = 0;
        _legs = legs ?? Array.Empty<PathLeg>();
        _legIndex = -1;
        _legsConsumed = 0;
        _landingHold = false;
        CurrentLeg = "";
        _stall.Reset();
        _progress.Reset();
        _signal.Set(_waypoints.Count > 0);
    }

    /// <summary>Steer straight at a point through the input hook — no path, no mesh. The
    /// PathIsRunning flag is set (steering IS the movement handover); arrival auto-stops.</summary>
    public void SteerTo(Vector3 target)
    {
        _waypoints.Clear();
        IsExternalPath = true;
        SteerTarget = target;
        _legs = Array.Empty<PathLeg>();
        _legIndex = -1;
        _legsConsumed = 0;
        _landingHold = false;
        CurrentLeg = "";
        StallCount = 0;
        _stall.Reset();
        _progress.Reset();
        _signal.Set(true);
    }

    public void Stop()
    {
        _waypoints.Clear();
        SteerTarget = null;
        IsExternalPath = false;
        _pathTolerance = null;
        _legs = Array.Empty<PathLeg>();
        _legIndex = -1;
        _legsConsumed = 0;
        _landingHold = false;
        CurrentLeg = "";
        StallCount = 0;
        _stall.Reset();
        _progress.Reset();
        _signal.Set(false);
    }

    public void Update(IFramework fwk)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        if (_waypoints.Count == 0 && SteerTarget is { } steer)
        {
            var toTarget = steer - player.Position;
            toTarget.Y = 0; // walk steering: the input pair is horizontal
            if (toTarget.Length() <= 0.5f)
            {
                Stop(); // arrived — release the hook and the shared flag
                _posPreviousFrame = player.Position;
                return;
            }
            _posPreviousFrame = player.Position;
            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = steer;
            return;
        }

        var before = _waypoints.Count;
        PathProgress.Advance(_waypoints, player.Position, _posPreviousFrame, _pathTolerance ?? Tolerance, DestinationTolerance, IgnoreDeltaY);
        _legsConsumed += before - _waypoints.Count; // legs index into the array we were handed

        if (_waypoints.Count == 0)
        {
            _posPreviousFrame = player.Position;
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            CurrentLeg = "";
            _signal.Set(false); // path finished naturally (Advance emptied it)
            return;
        }

        // entering a leg performs its transition before anything else this frame
        var legIndex = PathLegs.IndexAt(_legs, _legsConsumed);
        if (legIndex != _legIndex)
        {
            _legIndex = legIndex;
            BeginLeg(legIndex >= 0 ? _legs[legIndex] : null);
        }

        if (_config.CancelMoveOnUserInput && _movement.UserInput)
        {
            Stop();
            return;
        }

        var inFlight = Service.Condition[ConditionFlag.InFlight];

        // A pending `land`: hold the walk leg until the game says we are down. The waypoints
        // below us are what the fly hook descends to (it always drives vertical).
        if (_landingHold && !_landing.Update(inFlight, DateTime.Now))
        {
            _landingHold = false;
            if (_landing.GaveUp)
                Service.Log.Info($"[Follow] could not land in {LandingAttempt.DefaultBudget.TotalSeconds:0.#}s "
                    + "— following the leg on foot anyway");
            UpdateLegLabel();
        }

        // Walk-vs-fly semantics for this frame: the current leg decides, the request's flag
        // stands in where no leg covers us. Height keeps mattering until we are down — with
        // walk semantics a ground waypoint passes on horizontal distance alone, so a descent
        // could end the path in mid-air.
        var ignoreDeltaY = _legIndex >= 0 ? _legs[_legIndex].Mode == LegMode.Walk : !_flyPath;
        if (_takeoff.Abandoned)
            ignoreDeltaY = true; // flying looks unavailable here: walk the path
        if (_landingHold || (_landing.GaveUp && inFlight))
            ignoreDeltaY = false;
        IgnoreDeltaY = ignoreDeltaY;

        if (_config.DetectStalls && !_landingHold)
        {
            var destination = _waypoints[^1];
            var deltaMs = fwk.UpdateDelta.Milliseconds;
            // hard stall (frozen) OR soft stall (wobbling/circling without closing on the goal)
            var stalled = _stall.Update(player.Position, deltaMs);
            stalled |= _progress.Update((destination - player.Position).Length(), deltaMs);
            if (stalled)
            {
                StallCount++;
                OnStalled?.Invoke(destination, !IgnoreDeltaY, DestinationTolerance);
                if (_waypoints.Count == 0)
                    return; // handler stopped us
            }
        }

        _posPreviousFrame = player.Position;

        OverrideAFK.ResetTimers();
        _movement.Enabled = MovementAllowed;
        _movement.DesiredPosition = _waypoints[0];
        var wantsTakeoff = !_landingHold
            && _movement.DesiredPosition.Y > player.Position.Y
            && !inFlight && !Service.Condition[ConditionFlag.Diving]
            && !IgnoreDeltaY; // only on a flying path
        if (wantsTakeoff && Service.Condition[ConditionFlag.Mounted])
        {
            // walk->fly transition: spam jump to take off - but on a budget. Where flight is
            // not allowed the climb never completes, and the unbudgeted version jumped every
            // 100 ms for the whole path.
            var wasAbandoned = _takeoff.Abandoned;
            if (_takeoff.Update(true, DateTime.Now))
            {
                ExecuteJump();
            }
            else if (!wasAbandoned)
            {
                // Walking a flying path means height is no longer the goal, so stop measuring
                // progress by it - otherwise every waypoint sits unreachably overhead and the
                // walk stalls instead. This also makes stall recovery re-path as a walk.
                IgnoreDeltaY = true;
                Service.Log.Info($"[Follow] no takeoff in {TakeoffAttempt.DefaultBudget.TotalSeconds:0.#}s "
                    + "— flying looks unavailable here, walking the path instead");
            }
        }
        else if (wantsTakeoff)
        {
            _movement.Enabled = false;
            return; // unmounted: moving would just run on the spot under the climb
        }
        else
        {
            _takeoff.Update(false, DateTime.Now); // airborne or descending: the clock stops
        }

        _camera.Enabled = _config.AlignCameraToMovement;
        _camera.SpeedH = _camera.SpeedV = 360.Degrees();
        _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
        _camera.DesiredAltitude = _config.AlignCameraHeight.Degrees();
    }

    /// <summary>Entered a new leg: perform its transition, then log it. Transitions Ariadne
    /// cannot perform yet are logged and the leg is followed as-is — refusing the path would be
    /// worse than travelling it without the mount/dismount/teleport the planner asked for.</summary>
    private void BeginLeg(PathLeg? leg)
    {
        if (leg is { } l)
        {
            Service.Log.Info($"[Follow] leg {_legIndex + 1}/{_legs.Count}: {l.Describe()}, {l.Count} waypoints");
            switch (l.Enter)
            {
                case LegTransition.Land:
                    _landing.Reset();
                    _landingHold = true; // released below the moment we are not airborne
                    break;
                case LegTransition.Teleport:
                    Service.Log.Info($"[Follow] planner teleport leg (aetheryte {l.EnterArg}) is not executed yet "
                        + "— following the waypoints as-is");
                    break;
                case LegTransition.Mount:
                case LegTransition.JumpOff:
                case LegTransition.Dismount:
                    Service.Log.Info($"[Follow] leg transition '{l.Enter.Value.ToString().ToLowerInvariant()}' is not "
                        + "implemented — following the waypoints as-is");
                    break;
            }
        }
        UpdateLegLabel();
    }

    private void UpdateLegLabel() =>
        CurrentLeg = _landingHold
            ? "landing"
            : _legIndex >= 0 ? $"leg {_legIndex + 1}/{_legs.Count}: {(_legs[_legIndex].Mode == LegMode.Fly ? "fly" : "walk")}"
            : "";

    private unsafe void ExecuteJump()
    {
        if (Service.Condition[ConditionFlag.Diving])
            return; // can't jump while diving; avoids error-message spam
        if (DateTime.Now >= _nextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            _nextJump = DateTime.Now.AddMilliseconds(100);
        }
    }
}
