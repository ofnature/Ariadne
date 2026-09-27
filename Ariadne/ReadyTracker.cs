using System;
using System.Collections.Generic;
using System.Linq;

namespace Ariadne;

/// <summary>What one side of the mesh pipeline can say about the current zone: whether that pipeline
/// is in the picture at all, whether it has a usable mesh in hand, and how far a build it is running
/// has got (-1 when nothing is building).</summary>
internal readonly record struct MeshReadiness(bool Available, bool Ready, float Progress);

/// <summary>Which pipeline answered for a zone, and how. Both live in one table on purpose: a seeded
/// load against a native build is the comparison milestone 5 exists for, and it is only visible if
/// the two are told apart.</summary>
internal enum MeshSource
{
    VnavmeshBuilt,    // vnavmesh built it in-game
    VnavmeshCache,    // vnavmesh loaded it from its own meshcache
    AriadneBuilt,     // Mnemosyne built it out of process (nothing had a mesh for the zone)
    AriadneAnswered,  // a mesh was already in hand: vnavmesh's cache, or Mnemosyne's store
}

internal sealed record ZoneTiming(string CacheKey, double Seconds, MeshSource Source, DateTime When)
{
    /// <summary>True when the time was paid by a build rather than a load — the difference that makes
    /// a seeded zone's number worth quoting.</summary>
    public bool Built => Source is MeshSource.VnavmeshBuilt or MeshSource.AriadneBuilt;

    public string Label => Source switch
    {
        MeshSource.VnavmeshBuilt => "vnav built",
        MeshSource.VnavmeshCache => "vnav cache",
        MeshSource.AriadneBuilt => "Ariadne build",
        _ => "Ariadne",
    };
}

/// <summary>
/// Measures how long each mesh pipeline takes to have a usable mesh for a zone, from the moment the
/// layout becomes ready, and records which pipeline answered first.
///
/// <para>Two rows can come out of one zone entry, and that is the point: with vnavmesh installed,
/// Ariadne usually answers in ~0.1 s while vnavmesh either loads the file Ariadne seeded or spends
/// seconds building — and that pair is the whole claim. With vnavmesh gone the Ariadne row is the
/// only one, so the measurement survives the flip instead of quietly going empty.</para>
///
/// <para>A source is recorded only once it has been seen waiting (not-ready) and then ready, so a
/// pipeline that simply kept its mesh through the transition contributes nothing — it was never the
/// reason the zone took time. Classification detail, unchanged: vnavmesh's BuildProgress is 0 during
/// a cache <i>load</i> too and only climbs while tiles are actually built, so "progress advanced past
/// epsilon" is the build discriminator, not "progress exists".</para>
///
/// Dependencies are injected as delegates (both pipelines, monotonic clock) so the state machine is
/// unit-testable without Dalamud; the plugin drives Tick() from Framework.Update.
/// </summary>
internal sealed class ReadyTracker
{
    private const double TransitionGraceSeconds = 5;   // no pipeline ever left ready-state → nothing to measure
    private const double MeasureTimeoutSeconds = 600;  // give up (build cancelled, pipeline gone, ...)
    private const float BuiltProgressEpsilon = 0.02f;
    private const int MaxHistory = 20;

    private enum Phase { Idle, WaitingForTransition, Measuring }

    private readonly Func<MeshReadiness> _vnavmesh;
    private readonly Func<MeshReadiness> _ariadne;
    private readonly Func<double> _now; // monotonic seconds

    private readonly object _lock = new();
    private readonly List<ZoneTiming> _history = new();

    private Phase _phase = Phase.Idle;
    private string _cacheKey = "";
    private double _start;
    private float _maxProgress = -1;
    private float _maxVnavmeshProgress = -1;
    private float _maxAriadneProgress = -1;
    private bool _vnavmeshWaiting, _vnavmeshRecorded;
    private bool _ariadneWaiting, _ariadneRecorded;

    public ReadyTracker(Func<MeshReadiness> vnavmesh, Func<MeshReadiness> ariadne, Func<double> now)
    {
        _vnavmesh = vnavmesh;
        _ariadne = ariadne;
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
        _maxVnavmeshProgress = -1;
        _maxAriadneProgress = -1;
        _vnavmeshWaiting = _vnavmeshRecorded = false;
        _ariadneWaiting = _ariadneRecorded = false;
        _phase = Phase.WaitingForTransition;
    }

    public void Tick()
    {
        if (_phase == Phase.Idle)
            return;

        var vnavmesh = _vnavmesh();
        var ariadne = _ariadne();
        Observe(vnavmesh, ariadne);
        var elapsed = _now() - _start;

        switch (_phase)
        {
            case Phase.WaitingForTransition:
                // A transition is someone waiting for a mesh. A pipeline that is neither present nor
                // building has nothing to say, so it cannot hold the measurement open.
                if (_vnavmeshWaiting || _ariadneWaiting)
                {
                    _phase = Phase.Measuring;
                    return;
                }
                if (_maxProgress >= 0)
                {
                    _phase = Phase.Measuring; // building without dropping ready-state still counts
                    return;
                }
                if (elapsed > TransitionGraceSeconds)
                    _phase = Phase.Idle; // nothing left ready-state in time: no transition to time
                return;

            case Phase.Measuring:
                Record(vnavmesh, ariadne);
                if (Settled || elapsed > MeasureTimeoutSeconds)
                    _phase = Phase.Idle;
                return;
        }
    }

    /// <summary>Fold this tick into the per-pipeline observation: max progress seen, and whether a
    /// pipeline is currently waiting for a mesh (or building one, which is the same statement).</summary>
    private void Observe(MeshReadiness vnavmesh, MeshReadiness ariadne)
    {
        var progress = Math.Max(vnavmesh.Progress, ariadne.Progress);
        if (progress > _maxProgress)
            _maxProgress = progress;

        if (vnavmesh.Available)
        {
            if (vnavmesh.Progress > _maxVnavmeshProgress)
                _maxVnavmeshProgress = vnavmesh.Progress;
            if (!vnavmesh.Ready || vnavmesh.Progress >= 0)
                _vnavmeshWaiting = true;
        }
        else if (_vnavmeshWaiting)
        {
            _vnavmeshRecorded = true; // it left the picture while we were waiting: no row to wait for
        }

        if (ariadne.Available)
        {
            if (ariadne.Progress > _maxAriadneProgress)
                _maxAriadneProgress = ariadne.Progress;
            if (!ariadne.Ready || ariadne.Progress >= 0)
                _ariadneWaiting = true;
        }
        else if (_ariadneWaiting)
        {
            _ariadneRecorded = true;
        }
    }

    /// <summary>Record each pipeline once, the tick it comes back ready with a mesh.</summary>
    private void Record(MeshReadiness vnavmesh, MeshReadiness ariadne)
    {
        if (_vnavmeshWaiting && !_vnavmeshRecorded && vnavmesh.Available && vnavmesh.Ready)
        {
            _vnavmeshRecorded = true;
            Add(new ZoneTiming(_cacheKey, _now() - _start,
                _maxVnavmeshProgress > BuiltProgressEpsilon ? MeshSource.VnavmeshBuilt : MeshSource.VnavmeshCache,
                DateTime.Now));
        }

        if (_ariadneWaiting && !_ariadneRecorded && ariadne.Available && ariadne.Ready)
        {
            _ariadneRecorded = true;
            Add(new ZoneTiming(_cacheKey, _now() - _start,
                _maxAriadneProgress > BuiltProgressEpsilon ? MeshSource.AriadneBuilt : MeshSource.AriadneAnswered,
                DateTime.Now));
        }
    }

    /// <summary>Every pipeline that started waiting has either been recorded or left the picture, and
    /// at least one did wait — so this measurement is complete.</summary>
    private bool Settled =>
        (_vnavmeshWaiting || _ariadneWaiting) && _vnavmeshWaiting == _vnavmeshRecorded && _ariadneWaiting == _ariadneRecorded;

    private void Add(ZoneTiming timing)
    {
        lock (_lock)
        {
            _history.Add(timing);
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
    }
}
