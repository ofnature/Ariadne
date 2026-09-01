using System;
using System.IO;
using System.Linq;

namespace Ariadne.Seeding;

internal enum LocalMeshStatus { Missing, Stale, Current }

internal enum SeedResult { Seeded, AlreadyCurrent, SourceInvalid, Failed }

/// <summary>What a seed's customization version looked like against vnavmesh's own builds.
/// Not fatal either way — the seed still goes ahead — but a mismatch means vnavmesh will
/// reject the file on load and rebuild, and nothing else would ever say so.</summary>
internal enum SeedCompatibility { Unknown, Matches, Differs }

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

    /// <summary>Compare the customization version of a mesh about to be seeded against what
    /// vnavmesh's own builds of the same zone used.
    ///
    /// vnavmesh rejects a cache file whose customization version is not the one its compiled-in
    /// customization expects, catches the throw, and quietly rebuilds. Our header check only
    /// looks at the format version, so a seed with the wrong customization version copies fine,
    /// reports success, and accomplishes nothing. That was the state of every customized zone
    /// until the customizations were vendored - 38 of them, silently.
    ///
    /// This cannot know vnavmesh's expected version directly, so it infers it from any cached
    /// build vnavmesh made of the same zone. A first-ever seed has nothing to compare against
    /// and reports Unknown; the case worth catching is drift after an update, which does.</summary>
    public SeedCompatibility CheckCustomization(string cacheKey, string sourcePath, out int ours, out int theirs)
    {
        ours = theirs = -1;
        if (!NavmeshHeader.TryRead(sourcePath, out var source))
            return SeedCompatibility.Unknown;
        ours = source.Customization;

        // any variant of the same zone: the customization is per territory, not per variant
        var bgKey = cacheKey.Split(["__"], StringSplitOptions.None)[0];
        string[] siblings;
        try
        {
            siblings = Directory.Exists(_vnavCacheDir)
                ? Directory.GetFiles(_vnavCacheDir, bgKey + "*.navmesh")
                : [];
        }
        catch (IOException)
        {
            return SeedCompatibility.Unknown;
        }

        foreach (var sibling in siblings.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (string.Equals(sibling, TargetPath(cacheKey), StringComparison.OrdinalIgnoreCase))
                continue; // that is the slot we are about to write, not evidence
            if (!NavmeshHeader.TryRead(sibling, out var other) || !other.IsCurrent)
                continue; // a stale sibling says nothing about the current expectation
            theirs = other.Customization;
            return theirs == ours ? SeedCompatibility.Matches : SeedCompatibility.Differs;
        }
        return SeedCompatibility.Unknown;
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
