using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Travel;

/// <summary>
/// Every teleportable aetheryte with its world position, from the game's sheets. The
/// Aetheryte sheet's Level row places aethernet shards but not the main crystals (found
/// 2026-09-13: 0 of 108 attuned crystals had one), so the crystals come from the map's
/// aetheryte markers (MapMarker DataType 3), converted with the map's size factor and
/// offset per ffxiv-datamining's MapCoordinates: pixel = (world + offset) * factor/100 +
/// 1024. Markers carry no height, so the Y of a marker-placed aetheryte is 0 — every
/// distance taken against one must be horizontal. Attunement is not stored here: it is per
/// character and read live from the aetheryte list at planning time.
/// </summary>
internal sealed class AetheryteCatalog
{
    public sealed record Entry(uint Id, string Name, uint TerritoryId, Vector3 Position);

    private readonly List<Entry> _entries;

    public AetheryteCatalog(IEnumerable<Entry> entries) => _entries = new List<Entry>(entries);

    public int Count => _entries.Count;

    public static AetheryteCatalog FromSheet(IDataManager data, Action<string> log)
    {
        var entries = new List<Entry>();
        try
        {
            var markers = MarkerPositions(data);
            foreach (var a in data.GetExcelSheet<Aetheryte>())
            {
                if (!a.IsAetheryte || a.Territory.RowId == 0)
                    continue;
                var pos = LevelPosition(a);
                if (pos == null && markers.TryGetValue(a.RowId, out var marked))
                    pos = marked;
                if (pos is not { } p)
                    continue;
                var name = a.PlaceName.ValueNullable?.Name.ExtractText() ?? $"aetheryte {a.RowId}";
                entries.Add(new Entry(a.RowId, name, a.Territory.RowId, p));
            }
        }
        catch (Exception ex)
        {
            log($"Aetheryte catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
        return new AetheryteCatalog(entries);
    }

    /// <summary>Aetheryte row id → world XZ (Y = 0) from every map's aetheryte markers,
    /// each converted with its own map's scale and offset (a territory can span several
    /// maps, and a marker only makes sense against the map it is drawn on).</summary>
    private static Dictionary<uint, Vector3> MarkerPositions(IDataManager data)
    {
        const byte aetheryteMarker = 3;
        var mapsByRange = new Dictionary<uint, Map>();
        foreach (var map in data.GetExcelSheet<Map>())
            mapsByRange.TryAdd(map.MapMarkerRange, map);

        var result = new Dictionary<uint, Vector3>();
        foreach (var m in data.GetSubrowExcelSheet<MapMarker>().Flatten())
        {
            if (m.DataType != aetheryteMarker || !mapsByRange.TryGetValue(m.RowId, out var map))
                continue;
            var factor = (map.SizeFactor == 0 ? 100 : map.SizeFactor) / 100f;
            var wx = (m.X - 1024f) / factor - map.OffsetX;
            var wz = (m.Y - 1024f) / factor - map.OffsetY;
            result.TryAdd(m.DataKey.RowId, new Vector3(wx, 0, wz));
        }
        return result;
    }

    private static Vector3? LevelPosition(Aetheryte a)
    {
        try
        {
            foreach (var l in a.Level)
                if (l.RowId != 0 && l.ValueNullable is { } lv)
                    return new Vector3(lv.X, lv.Y, lv.Z);
        }
        catch
        {
            // a missing Level row is a missing position, not a missing aetheryte
        }
        return null;
    }

    public IEnumerable<Entry> InTerritory(uint territoryId)
    {
        foreach (var e in _entries)
            if (e.TerritoryId == territoryId)
                yield return e;
    }
}
