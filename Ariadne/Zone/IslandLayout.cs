using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using System;
using System.Collections.Generic;

namespace Ariadne.Zone;

/// <summary>
/// The Island Sanctuary's built layout, as a tag on the zone key.
///
/// <para>Every island is its own place: buildings, cropland and pasture fences, landmarks, the ground
/// the pathfinder levels, slope glamours that swap an incline for steps. None of that is in the
/// layout fields the zone key is made of, so every island shared one mesh (whichever was captured
/// first) and a mesh never refreshed when something was built. Seen 2026-10-19: a pasture fence
/// went up after the mesh was cached, and routes ran straight into it. Requested by Calypso
/// (<c>D:\Dev\Calypso\ariadne-island-layout-request.md</c>).</para>
///
/// <para>On territory 1055 the key carries <c>mji-&lt;hash&gt;</c> of the bytes that change the
/// geometry, read from <c>MJIManager.IslandState</c>. A different island, or the same island after
/// building something, is a different key: a cache miss, captured live by the usual path. Under
/// construction counts, since scaffolding is collision.</para>
/// </summary>
internal static unsafe class IslandLayout
{
    public const uint TerritoryId = 1055;
    public const string Prefix = "mji-";

    /// <summary>The tag for the current island, or null when not on one (or its state is not readable).</summary>
    public static string? Tag(uint territoryId)
    {
        var bytes = Current(territoryId);
        return bytes == null ? null : Prefix + Hash(bytes);
    }

    /// <summary>The bytes of the island's state that change what can be walked.</summary>
    public static byte[]? Current(uint territoryId)
    {
        if (territoryId != TerritoryId)
            return null;
        var mji = MJIManager.Instance();
        if (mji == null)
            return null;
        ref var s = ref mji->IslandState;

        var bytes = new List<byte>(48)
        {
            s.VillageDevelopmentLevel,
            s.PathsGlamourId,
            s.GroundsGlamourId,
            s.SlopesGlamourId,
            s.Farm.Level, Bit(s.Farm.UnderConstruction),
            s.Pasture.Level, Bit(s.Pasture.UnderConstruction),
        };
        for (var i = 0; i < 5; i++)
        {
            bytes.Add(s.LandmarkIds[i]);
            bytes.Add(Bit(s.LandmarkUnderConstruction[i]));
            bytes.Add(Bit(s.LandmarkRotation[i]));
        }
        for (var i = 0; i < 4; i++)
        {
            var w = s.Workshops[i];
            bytes.Add(w.PlaceId);
            bytes.Add(w.BuildingLevel);
            bytes.Add(Bit(w.UnderConstruction));
        }
        for (var i = 0; i < 2; i++)
        {
            var g = s.Granaries[i];
            bytes.Add(g.PlaceId);
            bytes.Add(g.BuildingLevel);
            bytes.Add(Bit(g.UnderConstruction));
        }
        bytes.Add(s.CabinLevel);
        return bytes.ToArray();
    }

    private static byte Bit(bool b) => (byte)(b ? 1 : 0);
    private static byte Bit(byte b) => b;

    /// <summary>FNV-1a, 32 bits, as eight hex digits: short enough for a filename, and a different
    /// byte anywhere in the state is a different tag.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        var h = 2166136261u;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 16777619u;
        }
        return h.ToString("x8");
    }
}

/// <summary>
/// When to act on an island layout change. A zone change is acted on at once, as always. The same
/// zone with a different island tag is construction in progress: the state bytes flip before the
/// new building's collision has streamed in, so a capture taken then would miss it. The new key is
/// adopted once it has held for <see cref="Settle"/> (Calypso waits the same 8 s before it asks
/// for a capture). Pure.
/// </summary>
internal sealed class IslandSettle
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

    private string? _pending;
    private DateTime _since;

    /// <summary>The key without its island tag.</summary>
    public static string Strip(string key)
    {
        var i = key.IndexOf("//" + IslandLayout.Prefix, StringComparison.Ordinal);
        return i < 0 ? key : key[..i];
    }

    /// <summary>Two keys for the same zone that differ only in the island's layout.</summary>
    public static bool SameZoneOtherLayout(string a, string b)
        => a.Length > 0 && b.Length > 0 && a != b && Strip(a) == Strip(b);

    /// <summary>The key to adopt now, or null to stay on <paramref name="current"/>.</summary>
    public string? Adopt(string current, string proposed, DateTime now)
    {
        if (proposed == current)
        {
            _pending = null;
            return null;
        }
        if (!SameZoneOtherLayout(current, proposed))
        {
            _pending = null;
            return proposed;
        }
        if (_pending != proposed)
        {
            _pending = proposed;
            _since = now;
            return null;
        }
        if (now - _since < Settle)
            return null;
        _pending = null;
        return proposed;
    }
}
