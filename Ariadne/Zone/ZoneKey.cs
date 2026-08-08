using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ariadne.Zone;

// Vendored key logic from vnavmesh's NavmeshManager.GetCurrentKey/GetCacheKey plus the
// festival-layer/shared-group subset of SceneDefinition.FillFromLayout. The cache key must
// match vnavmesh's meshcache filenames byte for byte — milestone 1's in-game verify step
// (logged key vs. actual filename) is the regression test for this file.
internal static unsafe class ZoneKey
{
    // Non-empty iff the active layout is fully loaded. Changes exactly when vnavmesh's
    // NavmeshManager would start a mesh transition.
    public static string CurrentKey()
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return ""; // layout not ready

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;

        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        // CE always has a festival layer (i hope). the non-festival layout is briefly loaded when entering the zone, which triggers a useless mesh build (which is also expensive because the zone is large)
        if (terrRow?.TerritoryIntendedUse.RowId == 60)
        {
            var fest = layout->ActiveFestivals[0];
            if (fest.Id == 0 && fest.Phase == 0)
                return "";
        }

        var sgs = LayoutUtils.GetZoneSharedGroupsEnabled(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        return $"{terrRow?.Bg}//{filterKey:X}//{LayoutUtils.FestivalsString(layout->ActiveFestivals)}//{string.Join('.', sgs)}";
    }

    // The meshcache filename stem for the current zone. Only meaningful when CurrentKey()
    // is non-empty.
    public static string CurrentCacheKey()
    {
        var (festivalLayers, zoneSGs) = CollectSceneLite();

        var layout = LayoutWorld.Instance()->ActiveLayout;
        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        var terrId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(terrId);

        return $"{terrRow?.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{FormatNumbers(festivalLayers)}__{FormatNumbers(zoneSGs)}";
    }

    internal static string FormatNumbers(IEnumerable<uint> nums)
        => string.Join('.', nums.Select(n => n.ToString("X", CultureInfo.InvariantCulture)));

    // SceneDefinition.FillFromActiveLayout, reduced to the two fields the cache key uses.
    // Same order (global layout first) and same ready-gate per layout as the original.
    private static (SortedSet<uint> festivalLayers, List<uint> zoneSGs) CollectSceneLite()
    {
        SortedSet<uint> festivalLayers = new();
        List<uint> zoneSGs = new();
        Collect(LayoutWorld.Instance()->GlobalLayout, festivalLayers, zoneSGs);
        Collect(LayoutWorld.Instance()->ActiveLayout, festivalLayers, zoneSGs);
        return (festivalLayers, zoneSGs);
    }

    private static void Collect(LayoutManager* layout, SortedSet<uint> festivalLayers, List<uint> zoneSGs)
    {
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return;

        var filter = LayoutUtils.FindFilter(layout);
        var territoryId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
        zoneSGs.AddRange(LayoutUtils.GetZoneSharedGroupsEnabled(territoryId));

        foreach (var (k, v) in layout->Layers)
        {
            if (v.Value->FestivalId != 0)
            {
                festivalLayers.Add(((uint)v.Value->FestivalSubId << 16) | v.Value->FestivalId);
            }
        }
    }
}
