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

    /// <summary>Raised on the framework thread when no progress is made for the configured
    /// window while a path is active. Args: final destination, fly, destination tolerance.
    /// The follower keeps going unless the handler calls Stop().</summary>
    public event Action<Vector3, bool, float>? OnStalled;

    private readonly AriadneConfig _config;
    private readonly OverrideCamera _camera = new();
    private readonly OverrideMovement _movement = new();
    private readonly List<Vector3> _waypoints = [];
    private readonly StallDetector _stall;

    private Vector3? _posPreviousFrame;
    private DateTime _nextJump;

    public PathFollower(AriadneConfig config)
    {
        _config = config;
        _stall = new StallDetector(config.StallMinProgress, config.StallWindowMs);
    }

    public void Dispose()
    {
        _camera.Dispose();
        _movement.Dispose();
    }

    public void Move(List<Vector3> waypoints, bool fly, float destinationTolerance = 0)
    {
        _waypoints.Clear();
        _waypoints.AddRange(waypoints);
        IgnoreDeltaY = !fly;
        DestinationTolerance = destinationTolerance;
        _stall.Reset();
    }

    public void Stop()
    {
        _waypoints.Clear();
        _stall.Reset();
    }

    public void Update(IFramework fwk)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        PathProgress.Advance(_waypoints, player.Position, _posPreviousFrame, Tolerance, DestinationTolerance, IgnoreDeltaY);

        if (_waypoints.Count == 0)
        {
            _posPreviousFrame = player.Position;
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            return;
        }

        if (_config.CancelMoveOnUserInput && _movement.UserInput)
        {
            Stop();
            return;
        }

        if (_config.DetectStalls && _stall.Update(player.Position, fwk.UpdateDelta.Milliseconds))
        {
            var destination = _waypoints[^1];
            OnStalled?.Invoke(destination, !IgnoreDeltaY, DestinationTolerance);
            if (_waypoints.Count == 0)
                return; // handler stopped us
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
