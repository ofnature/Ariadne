using System;
using System.IO;

namespace Ariadne.Seeding;

internal enum LocalMeshStatus { Missing, Stale, Current }

internal enum SeedResult { Seeded, AlreadyCurrent, SourceInvalid, Failed }

// Writes mesh files into vnavmesh's meshcache — the hand-off seam: vnavmesh checks
// {cacheKey}.navmesh before every build and loads it instead. Pure file logic, no Dalamud
// deps, so it's unit-testable with temp directories.
internal sealed class CacheSeeder
{
    private readonly string _vnavCacheDir;

    public CacheSeeder(string vnavCacheDir) => _vnavCacheDir = vnavCacheDir;

    public string TargetPath(string cacheKey) => Path.Combine(_vnavCacheDir, cacheKey + ".navmesh");

    public LocalMeshStatus LocalStatus(string cacheKey)
    {
        var path = TargetPath(cacheKey);
        if (!File.Exists(path))
            return LocalMeshStatus.Missing;
        return NavmeshHeader.TryRead(path, out var header) && header.IsCurrent
            ? LocalMeshStatus.Current
            : LocalMeshStatus.Stale;
    }

    public SeedResult Seed(string cacheKey, string sourcePath)
    {
        if (!NavmeshHeader.TryRead(sourcePath, out var header) || !header.IsCurrent)
            return SeedResult.SourceInvalid;

        if (LocalStatus(cacheKey) == LocalMeshStatus.Current)
            return SeedResult.AlreadyCurrent;

        var target = TargetPath(cacheKey);
        var temp = target + ".ariadne-tmp";
        try
        {
            Directory.CreateDirectory(_vnavCacheDir);
            // copy-then-move so vnavmesh can never observe a half-written cache file
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
            return SeedResult.Seeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            return SeedResult.Failed;
        }
    }
}
