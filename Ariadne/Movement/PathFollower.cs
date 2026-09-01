using Ariadne.Config;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Movement;

// Drives the character along a waypoint list via the vendored input hooks. Ported from
// vnavmesh's FollowPath with two changes: config is Ariadne's, and stall handling is
// window-based (StallDetector) with the decision left to the caller through OnStalled —
// MoveRequest turns that into re-path attempts, which is the recovery vnavmesh lacks.
internal sealed class PathFollower : IDisposable
{
    public bool MovementAllowed = true;
    public float Tolerance = 0.25f;

    public IReadOnlyList<Vector3> Waypoints => _waypoints;
    public bool IsRunning => _waypoints.Count > 0;
    public bool IgnoreDeltaY { get; private set; }
    public float DestinationTolerance { get; private set; }

    /// <summary>True when the active path was supplied by an external caller (Path.MoveTo)
    /// rather than MoveRequest. Stall recovery must not touch external paths: their
    /// waypoints may encode knowledge the mesh doesn't have (Minerva's danger-aware dodge
    /// corners), and a mesh re-path would discard exactly that (docs/externally-supplied-paths.md).</summary>
    public bool IsExternalPath { get; private set; }

    /// <summary>Stalls detected on the current path (external callers poll this over IPC to
    /// re-plan with their own geometry knowledge). Reset by Move/Stop.</summary>
    public int StallCount { get; private set; }

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
        bool external = true, float? waypointTolerance = null)
    {
        _waypoints.Clear();
        _waypoints.AddRange(waypoints);
        IgnoreDeltaY = !fly;
        DestinationTolerance = destinationTolerance;
        IsExternalPath = external;
        _pathTolerance = waypointTolerance; // per-path override; null = the global setting
        StallCount = 0;
        _stall.Reset();
        _progress.Reset();
        _signal.Set(_waypoints.Count > 0);
    }

    public void Stop()
    {
        _waypoints.Clear();
        IsExternalPath = false;
        _pathTolerance = null;
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

        PathProgress.Advance(_waypoints, player.Position, _posPreviousFrame, _pathTolerance ?? Tolerance, DestinationTolerance, IgnoreDeltaY);

        if (_waypoints.Count == 0)
        {
            _posPreviousFrame = player.Position;
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            _signal.Set(false); // path finished naturally (Advance emptied it)
            return;
        }

        if (_config.CancelMoveOnUserInput && _movement.UserInput)
        {
            Stop();
            return;
        }

        if (_config.DetectStalls)
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
        if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[ConditionFlag.InFlight] && !Service.Condition[ConditionFlag.Diving] && !IgnoreDeltaY) // only on a flying path
        {
            // walk->fly transition: spam jump to take off if mounted, otherwise wait (moving would just run on the spot)
            if (Service.Condition[ConditionFlag.Mounted])
                ExecuteJump();
            else
            {
                _movement.Enabled = false;
                return;
            }
        }

        _camera.Enabled = _config.AlignCameraToMovement;
        _camera.SpeedH = _camera.SpeedV = 360.Degrees();
        _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
        _camera.DesiredAltitude = _config.AlignCameraHeight.Degrees();
    }

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
