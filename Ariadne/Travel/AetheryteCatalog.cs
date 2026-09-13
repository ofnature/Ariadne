using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Travel;

/// <summary>
/// Every teleportable aetheryte with its world position, from the game's Aetheryte sheet.
/// Position comes from the aetheryte's Level row (the same source ECommons and Odysseus
/// use); an aetheryte with no Level row is dropped — one we cannot place cannot be costed.
/// Attunement is not stored here: it is per character and read live from the aetheryte
/// list at planning time.
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
            foreach (var a in data.GetExcelSheet<Aetheryte>())
            {
                if (!a.IsAetheryte || a.Territory.RowId == 0)
                    continue;
                if (LevelPosition(a) is not { } pos)
                    continue;
                var name = a.PlaceName.ValueNullable?.Name.ExtractText() ?? $"aetheryte {a.RowId}";
                entries.Add(new Entry(a.RowId, name, a.Territory.RowId, pos));
            }
        }
        catch (Exception ex)
        {
            log($"Aetheryte catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
        return new AetheryteCatalog(entries);
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
