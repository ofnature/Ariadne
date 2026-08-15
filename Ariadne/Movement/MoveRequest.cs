using Ariadne.Config;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Movement;

// "Path to X and go": asks the broker (Mnemosyne) for a path off-thread, hands the result
// to the follower on the framework thread, and owns stall recovery — on a stall it
// re-paths from the current position up to config.StallRetries times before giving up.
// Ported from vnavmesh's AsyncMoveRequest; the retry loop is the new part.
internal sealed class MoveRequest : IDisposable
{
    public bool TaskInProgress => _pending != null;
    public string LastResult { get; private set; } = "";
    public int RetriesUsed { get; private set; }

    private readonly MeshBroker _broker;
    private readonly PathFollower _follower;
    private readonly AriadneConfig _config;
    private readonly Func<Vector3?> _playerPosition;
    private readonly Action<string> _log;

    private Task<List<Vector3>>? _pending;
    private Vector3 _pendingDest;
    private bool _pendingFly;
    private float _pendingRange;

    public MoveRequest(MeshBroker broker, PathFollower follower, AriadneConfig config, Func<Vector3?> playerPosition, Action<string> log)
    {
        _broker = broker;
        _follower = follower;
        _config = config;
        _playerPosition = playerPosition;
        _log = log;
        _follower.OnStalled += OnStalled;
    }

    public void Dispose()
    {
        _follower.OnStalled -= OnStalled;
        _pending = null; // don't block unload on a slow pipe; the task completes into nothing
    }

    /// <summary>Framework-thread tick: promotes a finished pathfind into movement.</summary>
    public void Update()
    {
        if (_pending is not { IsCompleted: true } task)
            return;
        _pending = null;

        var path = task.IsCompletedSuccessfully ? task.Result : [];
        if (path.Count == 0)
        {
            LastResult = "no path";
            _log($"[Move] no path to {_pendingDest:f1}");
            return;
        }

        LastResult = $"{path.Count} waypoints";
        _follower.Move(path, _pendingFly, _pendingRange);
    }

    public bool MoveTo(Vector3 dest, bool fly, float range = 0)
    {
        RetriesUsed = 0;
        return Request(dest, fly, range);
    }

    public void Stop()
    {
        _pending = null;
        _follower.Stop();
    }

    private bool Request(Vector3 dest, bool fly, float range)
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

        _pendingDest = dest;
        _pendingFly = fly;
        _pendingRange = range;
        LastResult = "pathfinding…";
        _log($"[Move] {(fly ? "fly" : "walk")} to {dest:f1}{(range > 0 ? $" within {range}" : "")}");
        _pending = _broker.FindPathAsync(from.Value, dest, fly);
        return true;
    }

    private void OnStalled(Vector3 destination, bool fly, float range)
    {
        if (RetriesUsed >= _config.StallRetries)
        {
            _log($"[Move] stalled {RetriesUsed + 1}× — giving up");
            LastResult = "stuck";
            _follower.Stop();
            return;
        }

        RetriesUsed++;
        _log($"[Move] stalled — re-pathing (attempt {RetriesUsed}/{_config.StallRetries})");
        _follower.Stop();
        Request(destination, fly, range);
    }
}
