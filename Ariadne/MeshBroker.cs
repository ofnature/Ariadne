using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Seeding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
    private readonly Action<string> _log;

    private readonly object _activityLock = new();
    private readonly Queue<string> _activity = new();
    private const int MaxActivity = 100;

    private volatile string _currentKey = "";

    public MeshBroker(MnemosyneClient client, CacheSeeder seeder, VnavIpc vnav, Func<bool> autoSeed, Action<string> log)
    {
        _client = client;
        _seeder = seeder;
        _vnav = vnav;
        _autoSeed = autoSeed;
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

    public async Task<List<Vector3>> FindPathAsync(Vector3 from, Vector3 to, bool fly)
    {
        var key = _currentKey;
        if (key.Length == 0)
        {
            Activity("findPath rejected: zone not ready");
            return [];
        }

        var sw = Stopwatch.StartNew();
        var resp = await _client.FindPathAsync(key, [from.X, from.Y, from.Z], [to.X, to.Y, to.Z], fly).ConfigureAwait(false);
        if (resp is not { Ok: true, Waypoints: { } waypoints })
        {
            Activity($"findPath failed: {resp?.Error ?? "Mnemosyne unavailable"}");
            return [];
        }

        Activity($"findPath: {waypoints.Length} waypoints ({sw.Elapsed.TotalMilliseconds:0.0}ms)");
        return [.. waypoints.Where(w => w.Length >= 3).Select(w => new Vector3(w[0], w[1], w[2]))];
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
    }

    private async Task<Snapshot> BuildSnapshotAsync(string cacheKey)
    {
        if (_seeder.LocalStatus(cacheKey) == LocalMeshStatus.Current)
            return new Snapshot(cacheKey, ZoneMeshStatus.LocalCurrent, _seeder.TargetPath(cacheKey), DateTime.UtcNow);

        var status = await _client.ZoneStatusAsync(cacheKey).ConfigureAwait(false);
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
