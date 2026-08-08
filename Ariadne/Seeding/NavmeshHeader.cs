using System;
using System.IO;

namespace Ariadne.Seeding;

// The uncompressed 12-byte prefix of a vnavmesh .navmesh cache file (the rest is Brotli).
// Layout per vnavmesh's Navmesh.Serialize: uint32 magic "NVMD", uint32 serializer version,
// int32 per-zone customization version. vnavmesh only loads files whose serializer version
// matches its current one, so anything else is dead weight to hand around — real cache
// survey (2026-08-08): 528 of 571 files were stale v22–v24.
internal readonly record struct NavmeshHeader(uint Magic, int Version, int Customization)
{
    public const uint ExpectedMagic = 0x444D564E; // "NVMD"
    public const int CurrentVersion = 25;

    public bool IsValid => Magic == ExpectedMagic;

    /// <summary>Loadable by the vnavmesh version we interoperate with (customization is
    /// vnavmesh's own rebuild trigger, not part of this check — see Mnemosyne PLAN.md).</summary>
    public bool IsCurrent => IsValid && Version == CurrentVersion;

    public static bool TryRead(string path, out NavmeshHeader header)
    {
        header = default;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            header = new NavmeshHeader(reader.ReadUInt32(), (int)reader.ReadUInt32(), reader.ReadInt32());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }
}
