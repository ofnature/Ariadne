using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Seeding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Ariadne;

internal enum ZoneMeshStatus
{
    NotReady,             // zone loading / layout not ready
    MnemosyneUnavailable, // no local mesh and Mnemosyne unreachable
    LocalCurrent,         // vnavmesh's own meshcache already has a current file
    MnemosyneCached,      // Mnemosyne has it; local cache lacks it (seed candidate)
    Missing,              // nobody has a current mesh — vnavmesh will build
}

// Coordinates the pipeline: zone change → local check → Mnemosyne query → (auto-)seed →
// vnavmesh reload nudge. The window, the consumer IPC surface, and the zone watcher all
// talk to this, never to the client/seeder directly.
internal sealed class MeshBroker : IDisposable
{
    public sealed record Snapshot(string CacheKey, ZoneMeshStatus Status, string? MeshPath, DateTime AsOfUtc)
    {
        public static readonly Snapshot Empty = new("", ZoneMeshStatus.NotReady, null, DateTime.MinValue);
    }

    public Snapshot Current { get; private set; } = Snapshot.Empty;
    public bool MnemosyneConnected => _client.IsConnected;
    public string? MnemosyneApp => _client.ServerApp;

    private readonly MnemosyneClient _client;
    private readonly CacheSeeder _seeder;
    private readonly VnavIpc _vnav;
    private readonly Func<bool> _autoSeed;
    private readonly Func<bool> _buildOnMiss;
    private readonly Func<string, Task<Zone.SceneCaptureDto?>> _captureScene; // marshals to framework thread
    private readonly Action<string> _log;

    private readonly object _activityLock = new();
    private readonly Queue<string> _activity = new();
    private readonly HashSet<string> _buildRequested = []; // one buildZone per key per session
    private const int MaxActivity = 100;

    private volatile string _currentKey = "";

    public MeshBroker(MnemosyneClient client, CacheSeeder seeder, VnavIpc vnav,
        Func<bool> autoSeed, Func<bool> buildOnMiss, Func<string, Task<Zone.SceneCaptureDto?>> captureScene,
        Action<string> log)
    {
        _client = client;
        _seeder = seeder;
        _vnav = vnav;
        _autoSeed = autoSeed;
        _buildOnMiss = buildOnMiss;
        _captureScene = captureScene;
        _log = log;
    }

    public void Dispose() => _client.Dispose();

    public string[] RecentActivity
    {
        get { lock (_activityLock) return _activity.ToArray(); }
    }

    /// <summary>Called on the framework thread by the zone watcher; work happens off-thread.</summary>
    public void OnZoneChanged(string cacheKey)
    {
        _currentKey = cacheKey;
        Current = cacheKey.Length == 0
            ? Snapshot.Empty
            : new Snapshot(cacheKey, ZoneMeshStatus.NotReady, null, DateTime.UtcNow);
        if (cacheKey.Length > 0)
            _ = Task.Run(() => QueryAsync(cacheKey));
    }

    public Task RefreshAsync()
    {
        var key = _currentKey;
        return key.Length == 0 ? Task.CompletedTask : QueryAsync(key);
    }

    /// <summary>Ensure a current mesh file for the current zone exists on disk somewhere;
    /// returns its path, or "" when neither the local cache nor Mnemosyne has one.</summary>
    public async Task<string> RequestMeshAsync()
    {
        var key = _currentKey;
        if (key.Length == 0)
            return "";
        if (_seeder.LocalStatus(key) == LocalMeshStatus.Current)
            return _seeder.TargetPath(key);
        var mesh = await _client.GetMeshAsync(key).ConfigureAwait(false);
        if (mesh is { Ok: true, Path: { } path } && NavmeshHeader.TryRead(path, out var header) && header.IsCurrent)
            return path;
        return "";
    }

    /// <summary>Seed vnavmesh's meshcache for the current zone (nudging a reload if vnavmesh
    /// already started building) and report whether a current file is in place.</summary>
    public async Task<bool> SeedVnavCacheAsync()
    {
        var key = _currentKey;
        if (key.Length == 0)
            return false;
        return await SeedAsync(key).ConfigureAwait(false);
    }

    /// <summary>A route plus the server's own account of it. `Result` is never empty: a
    /// legacy server that says nothing is reported as "ok" when waypoints came back and
    /// "unreachable" when they did not, per the protocol doc's legacy rule.</summary>
    public sealed record PathAnswer(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial);

    public async Task<List<Vector3>> FindPathAsync(Vector3 from, Vector3 to, bool fly,
        float? tolerance = null, Vector3? avoidCenter = null, float avoidRadius = 0)
        => (await FindPathDetailedAsync(from, to, fly, tolerance, avoidCenter, avoidRadius).ConfigureAwait(false)).Waypoints;

    public async Task<PathAnswer> FindPathDetailedAsync(Vector3 from, Vector3 to, bool fly,
        float? tolerance = null, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        var key = _currentKey;
        if (key.Length == 0)
        {
            Activity("findPath rejected: zone not ready");
            return new PathAnswer("meshNotReady", [], null, false);
        }

        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _pathfindQueued);
        FindPathResponse? resp;
        try
        {
            resp = await _client.FindPathAsync(key, [from.X, from.Y, from.Z], [to.X, to.Y, to.Z], fly,
                tolerance,
                avoidCenter is { } c ? [c.X, c.Y, c.Z] : null,
                avoidRadius).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pathfindQueued);
        }
        var nearest = resp?.Nearest is { Length: >= 3 } n ? new Vector3(n[0], n[1], n[2]) : (Vector3?)null;
        if (resp is not { Ok: true, Waypoints: { } waypoints })
        {
            // The whole point of the classified answers: say *why*, so the caller acts once.
            var why = resp?.Result ?? (resp == null ? "meshNotReady" : "unreachable");
            var near = nearest is { } np ? $" nearest {np:0.0}" : "";
            Activity($"findPath [{why}]{near}: {resp?.Error ?? "Mnemosyne unavailable"}");
            return new PathAnswer(why, [], nearest, false);
        }

        var result = resp.Result ?? (waypoints.Length > 0 ? "ok" : "unreachable"); // legacy server
        var qualifiers = (resp.Partial ? " partial" : "") + (result is not "ok" ? $" [{result}]" : "");
        Activity($"findPath: {waypoints.Length} waypoints{qualifiers} ({sw.Elapsed.TotalMilliseconds:0.0}ms)");
        return new PathAnswer(result,
            [.. waypoints.Where(w => w.Length >= 3).Select(w => new Vector3(w[0], w[1], w[2]))],
            nearest, resp.Partial);
    }

    // ---- vnavmesh gate parity (added 2026-08-24) --------------------------------------
    // Backs the Ariadne.Nav.* / Ariadne.Query.Mesh.* IPC gates. These exist so a consumer
    // can be pointed at Mnemosyne while vnavmesh is still installed and the answers
    // compared side by side, before Ariadne claims the vnavmesh.* names.

    private int _pathfindQueued;
    private volatile float _buildProgress = -1;

    /// <summary>vnavmesh's Nav.IsReady: a usable mesh for the current zone is in hand.</summary>
    public bool NavIsReady => Current.Status is ZoneMeshStatus.LocalCurrent or ZoneMeshStatus.MnemosyneCached;

    /// <summary>vnavmesh's Nav.BuildProgress: 0..1 while building, -1 when idle. Refreshed
    /// by the zone poll, so it lags a build by at most one poll interval.</summary>
    public float NavBuildProgress => _buildProgress;

    public bool PathfindInProgress => Volatile.Read(ref _pathfindQueued) > 0;
    public int PathfindNumQueued => Volatile.Read(ref _pathfindQueued);

    public async Task<Vector3?> NearestPointAsync(Vector3 p, float halfExtentXZ, float halfExtentY, bool reachableOnly)
    {
        var key = _currentKey;
        if (key.Length == 0)
            return null;
        var resp = await _client.NearestPointAsync(key, [p.X, p.Y, p.Z], halfExtentXZ, halfExtentY, reachableOnly).ConfigureAwait(false);
        return resp is { Ok: true, Found: true, Point: { Length: >= 3 } pt } ? new Vector3(pt[0], pt[1], pt[2]) : null;
    }

    public async Task<bool> IsPointOnMeshAsync(Vector3 p, float halfExtentY, bool allowUnreachable)
    {
        var key = _currentKey;
        if (key.Length == 0)
            return false;
        var resp = await _client.IsPointOnMeshAsync(key, [p.X, p.Y, p.Z], halfExtentY, allowUnreachable).ConfigureAwait(false);
        return resp is { Ok: true, OnMesh: true };
    }

    public async Task<Vector3?> PointOnFloorAsync(Vector3 p, float halfExtentXZ, bool allowUnreachable)
    {
        var key = _currentKey;
        if (key.Length == 0)
            return null;
        var resp = await _client.PointOnFloorAsync(key, [p.X, p.Y, p.Z], halfExtentXZ, allowUnreachable).ConfigureAwait(false);
        return resp is { Ok: true, Found: true, Point: { Length: >= 3 } pt } ? new Vector3(pt[0], pt[1], pt[2]) : null;
    }

    /// <summary>Returns the written path (Mnemosyne owns the output directory), or "" on
    /// failure. vnavmesh returns void here; the path is strictly more useful.</summary>
    public async Task<string> BuildBitmapAsync(List<Vector3> startingPoints, string filename, float pixelSize,
        Vector3? minBounds = null, Vector3? maxBounds = null)
    {
        var key = _currentKey;
        if (key.Length == 0 || startingPoints.Count == 0)
            return "";
        var starts = new float[startingPoints.Count][];
        for (var i = 0; i < startingPoints.Count; ++i)
            starts[i] = [startingPoints[i].X, startingPoints[i].Y, startingPoints[i].Z];
        var resp = await _client.BuildBitmapAsync(key, starts, filename, pixelSize,
            minBounds is { } lo ? [lo.X, lo.Y, lo.Z] : null,
            maxBounds is { } hi ? [hi.X, hi.Y, hi.Z] : null).ConfigureAwait(false);
        Activity($"buildBitmap '{filename}': {(resp is { Ok: true } ? resp.Path : resp?.Error ?? "unavailable")}");
        return resp is { Ok: true, Path: { } outPath } ? outPath : "";
    }

    /// <summary>Forward execution feedback (traversal succeeded off-mesh / planned leg
    /// failed) into Mnemosyne's learning channel. Best-effort; no-op when zone not ready.</summary>
    public async Task<bool> ReportTraversalAsync(Vector3 from, Vector3 to, string mode, bool success)
    {
        var key = _currentKey;
        if (key.Length == 0)
            return false;
        var resp = await _client.ReportTraversalAsync(key, [from.X, from.Y, from.Z], [to.X, to.Y, to.Z], mode, success).ConfigureAwait(false);
        Activity($"reportTraversal {mode} {(success ? "success" : "failure")}: {(resp is { Ok: true } ? "recorded" : resp?.Error ?? "unavailable")}");
        return resp is { Ok: true };
    }

    private async Task QueryAsync(string cacheKey)
    {
        var sw = Stopwatch.StartNew();
        Snapshot snapshot;
        var attempt = 1;
        while (true)
        {
            snapshot = await BuildSnapshotAsync(cacheKey).ConfigureAwait(false);
            if (_currentKey != cacheKey)
                return; // zone changed while we were querying — stale result, drop it

            // Zone entry is peak contention on the mesh file: vnavmesh kicking its build,
            // Mnemosyne's viewer auto-loading the same zone off the player push. An
            // exclusive hold anywhere in that chain makes the server honestly answer
            // "missing" (proven by locking the file and probing), and a viewer load can
            // outlast a fixed retry window. So: a few quick retries always, and while
            // vnavmesh is still building keep asking — a seed stays profitable for the
            // whole build, since the Nav.Reload nudge converts it to a cache load.
            if (snapshot.Status is not (ZoneMeshStatus.Missing or ZoneMeshStatus.MnemosyneUnavailable))
                break;
            var buildRunning = _vnav.IsAvailable && _vnav.BuildProgress >= 0;
            if ((attempt >= 3 && !buildRunning) || attempt >= 90)
                break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 2))).ConfigureAwait(false);
            if (_currentKey != cacheKey)
                return;
            attempt++;
        }

        Current = snapshot;
        Activity($"zone '{cacheKey}': {snapshot.Status} ({sw.Elapsed.TotalMilliseconds:0.0}ms{(attempt > 1 ? $", attempt {attempt}" : "")})");

        if (snapshot.Status == ZoneMeshStatus.MnemosyneCached && _autoSeed())
            await SeedAsync(cacheKey).ConfigureAwait(false);
        else if (snapshot.Status == ZoneMeshStatus.Missing && _buildOnMiss())
            await RequestBuildAsync(cacheKey).ConfigureAwait(false);
    }

    // The vnavmesh-replacement path: nobody has a mesh, so capture the live scene (only
    // the game process sees active festival layers / SG states / live instances) and let
    // Mnemosyne build the exact variant out of process. vnavmesh may be building in-game
    // at the same time — whoever finishes first wins, the other becomes the cache.
    private async Task RequestBuildAsync(string cacheKey)
    {
        lock (_activityLock)
        {
            if (!_buildRequested.Add(cacheKey))
                return; // already asked this session — don't spam multi-second builds
        }

        var capture = await _captureScene(cacheKey).ConfigureAwait(false);
        if (capture == null || capture.CacheKey != cacheKey)
        {
            Activity("build capture failed (layout gone?) — will retry on next visit");
            lock (_activityLock)
                _buildRequested.Remove(cacheKey);
            return;
        }

        Activity($"requesting out-of-process build: {capture.InstanceCount} instances, {capture.Terrains.Length} terrains, {capture.MeshPaths.Length} collision meshes");
        var resp = await _client.BuildZoneAsync(capture).ConfigureAwait(false);
        if (resp is not { Ok: true })
        {
            Activity($"buildZone declined: {resp?.Error ?? "Mnemosyne unavailable"}");
            return; // stays in _buildRequested — a declining server won't change its mind this session
        }

        // build runs server-side (tens of seconds); poll until it lands, then re-query → seed
        for (var waited = 0; waited < 300_000 && _currentKey == cacheKey; waited += 3000)
        {
            await Task.Delay(3000).ConfigureAwait(false);
            var status = await _client.ZoneStatusAsync(cacheKey).ConfigureAwait(false);
            if (status is { Ok: true, Status: "cached" })
            {
                Activity($"out-of-process build complete (~{(waited + 3000) / 1000}s)");
                await QueryAsync(cacheKey).ConfigureAwait(false); // re-enters as MnemosyneCached → auto-seed
                return;
            }
        }
        if (_currentKey == cacheKey)
            Activity("out-of-process build did not finish within 5min — giving up for this visit");
    }

    private async Task<Snapshot> BuildSnapshotAsync(string cacheKey)
    {
        if (_seeder.LocalStatus(cacheKey) == LocalMeshStatus.Current)
            return new Snapshot(cacheKey, ZoneMeshStatus.LocalCurrent, _seeder.TargetPath(cacheKey), DateTime.UtcNow);

        var status = await _client.ZoneStatusAsync(cacheKey).ConfigureAwait(false);
        _buildProgress = status?.Progress ?? -1;
        if (status == null)
            return new Snapshot(cacheKey, ZoneMeshStatus.MnemosyneUnavailable, null, DateTime.UtcNow);
        if (status is not { Ok: true, Status: "cached" })
            return new Snapshot(cacheKey, ZoneMeshStatus.Missing, null, DateTime.UtcNow);

        var mesh = await _client.GetMeshAsync(cacheKey).ConfigureAwait(false);
        if (mesh is { Ok: true, Path: { } path } && NavmeshHeader.TryRead(path, out var header) && header.IsCurrent)
            return new Snapshot(cacheKey, ZoneMeshStatus.MnemosyneCached, path, DateTime.UtcNow);

        return new Snapshot(cacheKey, ZoneMeshStatus.Missing, null, DateTime.UtcNow);
    }

    private async Task<bool> SeedAsync(string cacheKey)
    {
        var sw = Stopwatch.StartNew();
        var sourcePath = Current is { MeshPath: { } p, Status: ZoneMeshStatus.MnemosyneCached } ? p : null;
        if (sourcePath == null)
        {
            var mesh = await _client.GetMeshAsync(cacheKey).ConfigureAwait(false);
            if (mesh is { Ok: true, Path: { } path })
                sourcePath = path;
        }
        if (sourcePath == null)
        {
            Activity("seed failed: no mesh available from Mnemosyne");
            return _seeder.LocalStatus(cacheKey) == LocalMeshStatus.Current;
        }

        var result = _seeder.Seed(cacheKey, sourcePath);
        Activity($"seed '{cacheKey}': {result} ({sw.Elapsed.TotalMilliseconds:0.0}ms)");

        if (result == SeedResult.Seeded)
        {
            // only nudge when the seed actually delivered something vnavmesh lacked —
            // Reload discards partial build progress, so a gratuitous call is a regression
            if (_vnav.IsAvailable && !_vnav.IsReady)
            {
                _vnav.Reload();
                Activity("vnavmesh reload nudged (build was not finished)");
            }
            if (_currentKey == cacheKey)
                Current = new Snapshot(cacheKey, ZoneMeshStatus.LocalCurrent, _seeder.TargetPath(cacheKey), DateTime.UtcNow);
        }

        return result is SeedResult.Seeded or SeedResult.AlreadyCurrent;
    }

    private void Activity(string message)
    {
        _log($"[Broker] {message}");
        lock (_activityLock)
        {
            _activity.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
            while (_activity.Count > MaxActivity)
                _activity.Dequeue();
        }
    }
}
