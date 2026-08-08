using System;
using System.Collections.Generic;
using System.Linq;

namespace Ariadne;

internal sealed record ZoneTiming(string CacheKey, double Seconds, bool Built, DateTime When);

// Measures how long each zone takes from layout-ready to vnavmesh mesh-ready, and whether
// that time was a from-scratch build or a cache load — the number milestone 5 exists to
// compare (seeded cache load vs native build).
//
// Classification detail: vnavmesh's Nav.BuildProgress is 0 during a cache *load* too, and
// only climbs while tiles are actually being built — so "progress advanced past epsilon"
// is the build discriminator, not "progress exists".
//
// Dependencies are injected as delegates (vnavmesh state, monotonic clock) so the state
// machine is unit-testable without Dalamud; the plugin drives Tick() from Framework.Update.
internal sealed class ReadyTracker
{
    private const double TransitionGraceSeconds = 5;   // vnavmesh never left ready-state → nothing to measure
    private const double MeasureTimeoutSeconds = 600;  // give up (vnavmesh absent, build cancelled, ...)
    private const float BuiltProgressEpsilon = 0.02f;
    private const int MaxHistory = 20;

    private enum Phase { Idle, WaitingForTransition, Measuring }

    private readonly Func<bool> _vnavAvailable;
    private readonly Func<bool> _isReady;
    private readonly Func<float> _buildProgress;
    private readonly Func<double> _now; // monotonic seconds

    private readonly object _lock = new();
    private readonly List<ZoneTiming> _history = new();

    private Phase _phase = Phase.Idle;
    private string _cacheKey = "";
    private double _start;
    private float _maxProgress = -1;

    public ReadyTracker(Func<bool> vnavAvailable, Func<bool> isReady, Func<float> buildProgress, Func<double> now)
    {
        _vnavAvailable = vnavAvailable;
        _isReady = isReady;
        _buildProgress = buildProgress;
        _now = now;
    }

    /// <summary>Latest first.</summary>
    public ZoneTiming[] History
    {
        get { lock (_lock) return [.. Enumerable.Reverse(_history)]; }
    }

    /// <summary>Non-null while a measurement is running (for the live UI line).</summary>
    public (string CacheKey, double Elapsed, float MaxProgress)? InProgress =>
        _phase == Phase.Measuring ? (_cacheKey, _now() - _start, _maxProgress) : null;

    public void OnZoneChanged(string cacheKey)
    {
        if (cacheKey.Length == 0)
        {
            _phase = Phase.Idle;
            return;
        }
        _cacheKey = cacheKey;
        _start = _now();
        _maxProgress = -1;
        _phase = Phase.WaitingForTransition;
    }

    public void Tick()
    {
        switch (_phase)
        {
            case Phase.Idle:
                return;

            case Phase.WaitingForTransition:
                if (!_vnavAvailable())
                {
                    if (_now() - _start > TransitionGraceSeconds)
                        _phase = Phase.Idle;
                    return;
                }
                TrackProgress();
                if (_maxProgress >= 0 || !_isReady())
                {
                    _phase = Phase.Measuring;
                    return;
                }
                if (_now() - _start > TransitionGraceSeconds)
                    _phase = Phase.Idle; // vnavmesh kept its mesh; nothing happened worth timing
                return;

            case Phase.Measuring:
                TrackProgress();
                if (_isReady())
                {
                    Record(new ZoneTiming(_cacheKey, _now() - _start, _maxProgress > BuiltProgressEpsilon, DateTime.Now));
                    _phase = Phase.Idle;
                    return;
                }
                if (_now() - _start > MeasureTimeoutSeconds)
                    _phase = Phase.Idle;
                return;
        }
    }

    private void TrackProgress()
    {
        var p = _buildProgress();
        if (p > _maxProgress)
            _maxProgress = p;
    }

    private void Record(ZoneTiming timing)
    {
        lock (_lock)
        {
            _history.Add(timing);
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
    }
}
