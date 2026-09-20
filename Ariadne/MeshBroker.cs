using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Movement;
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
    /// <param name="Legs">The server's multi-modal plan for the same waypoints (null/empty =
    /// single mode, or a legacy server). The follower executes the mode switches and the `land`
    /// transition; a consumer that ignores legs still gets the flat list.</param>
    public sealed record PathAnswer(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial,
        IReadOnlyList<PathLeg>? Legs = null);

    /// <summary>A reachability window plus the server's account of it (vnavmesh has no
    /// equivalent of this query). `Result` is never empty; the grid arrays are empty unless the
    /// flood ran, and `States` is the byte form the consumer tuple carries.</summary>
    public sealed record ReachableCellsAnswer(string Result, Vector3 Start, Vector2 Origin, float CellSize,
        int Width, int Depth, int[] Columns, float[] Heights, byte[] States, bool ReachableOutside,
        Vector3? Nearest = null, int ReachablePolys = 0, int WalkablePolys = 0)
    {
        /// <summary>No grid at all: the zone is not ready, nothing answered, the server does
        /// not know the op, or what it sent could not be indexed safely. `from` stands in as
        /// Start so the caller always has a usable point.</summary>
        public static ReachableCellsAnswer Failed(string result, Vector3 from) =>
            new(result, from, default, 0, 0, 0, [], [], [], false);
    }

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
            // A null response means nothing answered the pipe at all - not running, still in
            // backoff, or a timeout that dropped the connection. That is "serviceUnavailable",
            // never "meshNotReady": the latter tells a consumer to wait and retry, which is
            // exactly wrong when there is no service to wait for.
            var why = resp?.Result ?? (resp == null ? "serviceUnavailable" : "unreachable");
            var near = nearest is { } np ? $" nearest {np:0.0}" : "";
            Activity($"findPath [{why}]{near}: {resp?.Error ?? "Mnemosyne unavailable"}");
            return new PathAnswer(why, [], nearest, false);
        }

        var result = resp.Result ?? (waypoints.Length > 0 ? "ok" : "unreachable"); // legacy server
        var qualifiers = (resp.Partial ? " partial" : "") + (result is not "ok" ? $" [{result}]" : "");
        var points = waypoints.Where(w => w.Length >= 3).Select(w => new Vector3(w[0], w[1], w[2])).ToList();
        var legs = ParseLegs(resp.Legs, points.Count, waypoints.Length);
        var legSummary = legs is { Count: > 0 } ? $" ({string.Join("→", legs.Select(l => l.Mode == LegMode.Fly ? "fly" : "walk"))})" : "";
        Activity($"findPath: {points.Count} waypoints{qualifiers}{legSummary} ({sw.Elapsed.TotalMilliseconds:0.0}ms)");
        return new PathAnswer(result, points, nearest, resp.Partial, legs);
    }

    // Legs index into the waypoint array the server sent, so if a malformed entry had to be
    // dropped the indices would point at the wrong waypoints: drop the legs instead. The path
    // still follows, just mode-naive — exactly a legacy server's behaviour.
    private IReadOnlyList<PathLeg>? ParseLegs(FindPathLegResponse[]? wire, int pointsKept, int pointsServed)
    {
        if (wire is not { Length: > 0 })
            return null;
        if (pointsKept != pointsServed)
        {
            Activity($"findPath: {pointsServed - pointsKept} malformed waypoint(s) dropped — legs ignored");
            return null;
        }
        return PathLegs.Parse(wire, pointsKept);
    }

    // ---- reachableCells (Theseus auto-solver's exploration query) ----------------------

    /// <summary>Grid-reachability window: "where can I walk from here?" (spec:
    /// docs/mnemosyne-protocol.md → reachableCells). Task-shaped and never throwing, so a
    /// consumer can await it off the framework thread. Degrades honestly: `meshNotReady` before
    /// the zone is ready, `serviceUnavailable` when nothing answers the pipe, `failed` when the
    /// server does not know the op (an older Mnemosyne) or sent a grid that cannot be indexed
    /// safely — an empty grid would read as "no walkable ground here", which is a different and
    /// much more dangerous claim.</summary>
    public async Task<ReachableCellsAnswer> ReachableCellsAsync(Vector3 from, float radius, float cellSize,
        float minY, float maxY)
    {
        var key = _currentKey;
        if (key.Length == 0)
        {
            Activity("reachableCells rejected: zone not ready");
            return ReachableCellsAnswer.Failed("meshNotReady", from);
        }

        var sw = Stopwatch.StartNew();
        var resp = await _client.ReachableCellsAsync(key, [from.X, from.Y, from.Z], radius, cellSize, minY, maxY)
            .ConfigureAwait(false);
        if (resp is not { Ok: true })
        {
            var why = resp == null ? "serviceUnavailable" : resp.Result ?? "failed";
            Activity($"reachableCells [{why}]: {resp?.Error ?? "Mnemosyne unavailable"}");
            return ReachableCellsAnswer.Failed(why, from);
        }

        var result = resp.Result ?? "ok"; // a legacy server that says nothing and sent a grid
        var columns = resp.Columns ?? [];
        var heights = resp.Heights ?? [];
        var states = resp.States ?? [];
        var start = Point(resp.Start) ?? from; // no snap reported: the caller's own point back

        if (columns.Length > 0 && !ReachableGrid.TryValidate(columns, heights, states, resp.Width, resp.Depth, out var whyNot))
        {
            Activity($"reachableCells: unusable grid {whyNot} — refusing it");
            return ReachableCellsAnswer.Failed("failed", start);
        }
        if (columns.Length == 0 && result == "ok")
            Activity($"reachableCells: empty window {resp.Width}×{resp.Depth} — no walkable surface in it");

        var origin = resp.Origin is { Length: >= 2 } o ? new Vector2(o[0], o[1]) : default;
        var nearest = Point(resp.Nearest); // only startOffMesh carries one, and it is the useful part of that answer
        var near = nearest is { } np ? $" nearest {np:0.0}" : "";
        Activity($"reachableCells: {columns.Length} surfaces on {resp.Width}×{resp.Depth} @ {resp.CellSize:0.##}y "
            + $"[{result}{near}{(resp.ReachableOutside ? ", reachable outside" : "")}] ({sw.Elapsed.TotalMilliseconds:0.0}ms)");

        return new ReachableCellsAnswer(result, start, origin, resp.CellSize, resp.Width, resp.Depth,
            columns, heights, ReachableGrid.ToStates(states), resp.ReachableOutside,
            nearest, resp.Stats?.ReachablePolys ?? 0, resp.Stats?.WalkablePolys ?? 0);
    }

    private static Vector3? Point(float[]? v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : null;

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
        var resp = await BuildBitmapCoreAsync(startingPoints, filename, pixelSize, minBounds, maxBounds).ConfigureAwait(false);
        return resp is { Ok: true, Path: { } outPath } ? outPath : "";
    }

    /// <summary>vnavmesh's BuildBitmap return shape: the rasterized (min,max) bounds. Falls
    /// back to the request bounds when the server doesn't report bounds yet.</summary>
    public async Task<(Vector3 min, Vector3 max)> BuildBitmapBoundsAsync(List<Vector3> startingPoints, string filename, float pixelSize,
        Vector3? minBounds = null, Vector3? maxBounds = null)
    {
        var resp = await BuildBitmapCoreAsync(startingPoints, filename, pixelSize, minBounds, maxBounds).ConfigureAwait(false);
        if (resp is { Ok: true, Min: { Length: >= 3 } lo, Max: { Length: >= 3 } hi })
            return (new Vector3(lo[0], lo[1], lo[2]), new Vector3(hi[0], hi[1], hi[2]));
        return (minBounds ?? default, maxBounds ?? default);
    }

    private async Task<BitmapResponse?> BuildBitmapCoreAsync(List<Vector3> startingPoints, string filename, float pixelSize,
        Vector3? minBounds, Vector3? maxBounds)
    {
        var key = _currentKey;
        if (key.Length == 0 || startingPoints.Count == 0)
            return null;
        var starts = new float[startingPoints.Count][];
        for (var i = 0; i < startingPoints.Count; ++i)
            starts[i] = [startingPoints[i].X, startingPoints[i].Y, startingPoints[i].Z];
        var resp = await _client.BuildBitmapAsync(key, starts, filename, pixelSize,
            minBounds is { } lo ? [lo.X, lo.Y, lo.Z] : null,
            maxBounds is { } hi ? [hi.X, hi.Y, hi.Z] : null).ConfigureAwait(false);
        Activity($"buildBitmap '{filename}': {(resp is { Ok: true } ? resp.Path : resp?.Error ?? "unavailable")}");
        return resp;
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
    /// <summary>Capture the live scene and rebuild this zone even though a mesh already
    /// exists. The automatic path only fires on a cache miss, which means a zone vnavmesh has
    /// cached can never be captured - and the cached copy may be the wrong variant. Festival
    /// layers and shared-group states only exist in the game process, so this is the only way
    /// to get the exact variant a player is standing in. Deliberate, so it also clears the
    /// once-per-session guard.</summary>
    public async Task<bool> CaptureCurrentZoneAsync()
    {
        var key = _currentKey;
        if (key.Length == 0)
        {
            Activity("capture rejected: zone not ready");
            return false;
        }
        lock (_activityLock)
            _buildRequested.Remove(key); // an explicit ask overrides "already asked this session"
        Activity($"capturing '{key}' on request");
        await RequestBuildAsync(key).ConfigureAwait(false);
        return true;
    }

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

        // Warn before seeding, not after: vnavmesh silently rejects a file whose customization
        // version is not the one it expects, so a mismatch means this copy is about to
        // accomplish nothing at all. Not fatal - the seed still goes ahead, since our version
        // may be the newer one and vnavmesh may be the thing that is behind.
        var compatibility = _seeder.CheckCustomization(cacheKey, sourcePath, out var ours, out var theirs);
        if (compatibility == SeedCompatibility.Differs)
            Activity($"WARNING customization drift: seeding v{ours} where vnavmesh built v{theirs}. "
                + "vnavmesh will reject this file and rebuild. Re-vendor the customizations.");

        var result = _seeder.Seed(cacheKey, sourcePath);
        Activity($"seed '{cacheKey}': {result} ({sw.Elapsed.TotalMilliseconds:0.0}ms"
            + (compatibility == SeedCompatibility.Matches ? $", customization v{ours}" : "") + ")");

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
