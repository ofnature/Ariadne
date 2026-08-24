using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ariadne.Zone;

// Vendored from vnavmesh (external/ffxiv_navmesh/vnavmesh/LayoutUtils.cs), trimmed to the
// helpers the cache-key logic needs. Must stay behaviorally identical to vnavmesh's copy —
// the cache key it produces has to match vnavmesh's meshcache filenames byte for byte.
// Re-check on every vnavmesh update (see PLAN.md "Key-parity drift").

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ExdZoneSharedGroup
{
    private uint LGBSharedGroup;
    private uint RequirementRow0;
    private uint RequirementRow1;
    private uint RequirementRow2;
    private uint RequirementRow3;
    private uint RequirementRow4;
    private uint RequirementRow5;
    private uint Unknown0;
    private uint RequirementQuestSequence0;
    private uint RequirementQuestSequence1;
    private uint RequirementQuestSequence2;
    private uint RequirementQuestSequence3;
    private uint RequirementQuestSequence4;
    private uint RequirementQuestSequence5;
    private uint Unknown1;
    private byte RequirementType0;
    private byte RequirementType1;
    private byte RequirementType2;
    private byte RequirementType3;
    private byte RequirementType4;
    private byte RequirementType5;
    private byte Unknown8;
    private byte Unknown9;
    private byte Unknown10;
    private byte Unknown11;
    private byte Unknown12;
    private byte Unknown13;
    private byte Unknown14;
    private byte Unknown15;

    public static implicit operator ExdZoneSharedGroup(Lumina.Excel.Sheets.ZoneSharedGroup sg) => new()
    {
        LGBSharedGroup = sg.LGBSharedGroup,
        RequirementRow0 = sg.RequirementRow[0].RowId,
        RequirementRow1 = sg.RequirementRow[1].RowId,
        RequirementRow2 = sg.RequirementRow[2].RowId,
        RequirementRow3 = sg.RequirementRow[3].RowId,
        RequirementRow4 = sg.RequirementRow[4].RowId,
        RequirementRow5 = sg.RequirementRow[5].RowId,
        Unknown0 = sg.Unknown0,
        RequirementQuestSequence0 = sg.RequirementQuestSequence[0],
        RequirementQuestSequence1 = sg.RequirementQuestSequence[1],
        RequirementQuestSequence2 = sg.RequirementQuestSequence[2],
        RequirementQuestSequence3 = sg.RequirementQuestSequence[3],
        RequirementQuestSequence4 = sg.RequirementQuestSequence[4],
        RequirementQuestSequence5 = sg.RequirementQuestSequence[5],
        Unknown1 = sg.Unknown1,
        RequirementType0 = sg.RequirementType[0],
        RequirementType1 = sg.RequirementType[1],
        RequirementType2 = sg.RequirementType[2],
        RequirementType3 = sg.RequirementType[3],
        RequirementType4 = sg.RequirementType[4],
        RequirementType5 = sg.RequirementType[5],
        Unknown8 = sg.Unknown8,
        Unknown9 = sg.Unknown9 ? (byte)1 : (byte)0,
        Unknown10 = sg.Unknown10 ? (byte)1 : (byte)0,
        Unknown11 = sg.Unknown11 ? (byte)1 : (byte)0,
        Unknown12 = sg.Unknown12 ? (byte)1 : (byte)0,
        Unknown13 = sg.Unknown13 ? (byte)1 : (byte)0,
        Unknown14 = sg.Unknown14 ? (byte)1 : (byte)0,
        Unknown15 = sg.Unknown15 ? (byte)1 : (byte)0,
    };
}

internal static unsafe class LayoutUtils
{
    private static readonly delegate* unmanaged<ExdZoneSharedGroup*, uint> _getEnabledRequirementIndex;

    static LayoutUtils()
    {
        _getEnabledRequirementIndex = (delegate* unmanaged<ExdZoneSharedGroup*, uint>)Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 0F B6 53 6C");
    }

    public static uint[] GetZoneSharedGroupsEnabled(uint territoryType)
    {
        var tt = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territoryType);
        if (tt == null)
            return [];

        var rows = tt.Value.ZoneSharedGroup.Value.ToList();
        var indices = new uint[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            ExdZoneSharedGroup exd = rows[i];
            indices[i] = _getEnabledRequirementIndex(&exd);
        }
        return indices;
    }

    public static string ReadString(byte* data) => data != null ? MemoryHelper.ReadStringNullTerminated((nint)data) : "";
    public static string ReadString(RefCountedString* data) => data != null ? data->DataString : "";

    public static V* FindPtr<K, V>(ref this StdMap<K, Pointer<V>> map, K key) where K : unmanaged, IComparable where V : unmanaged
    {
        return map.TryGetValuePointer(key, out var ptr) && ptr != null ? ptr->Value : null;
    }

    public static LayoutManager.Filter* FindFilter(LayoutManager* layout)
    {
        if (layout->CfcId != 0) // note: some code paths check cfcid match only if TerritoryTypeId != 0; don't think it actually matters
            foreach (var (k, v) in layout->Filters)
                if (v.Value->CfcId == layout->CfcId)
                    return v.Value;
        if (layout->TerritoryTypeId != 0)
            foreach (var (k, v) in layout->Filters)
                if (v.Value->TerritoryTypeId == layout->TerritoryTypeId)
                    return v.Value;
        return layout->TerritoryTypeId == 0 ? FindPtr(ref layout->Filters, layout->LayerFilterKey) : null;
    }

    public static string FestivalString(GameMain.Festival f) => $"{(uint)(f.Phase << 16) | f.Id:X}";
    public static string FestivalsString(ReadOnlySpan<GameMain.Festival> f) => $"{FestivalString(f[0])}.{FestivalString(f[1])}.{FestivalString(f[2])}.{FestivalString(f[3])}";
}
