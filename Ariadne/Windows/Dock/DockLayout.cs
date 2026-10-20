using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;

namespace Ariadne.Windows.Dock;

/// <summary>One tab as the shared layout sees it.</summary>
/// <param name="Key">"Owner/TabId" — the owner is the plugin ("Daedalus", "Charon", "Ariadne").</param>
/// <param name="Edge">0 = left edge, 1 = right edge, 2 = top, 3 = bottom (<see cref="DockEdge"/>).</param>
/// <param name="Along">Where the tab wants its centre, as a fraction of the edge (0 = top / left end, 1 = the far end).</param>
/// <param name="Length">How long the tab is along the edge this frame, in pixels.</param>
/// <param name="Order">Settles ties between tabs wanting the same spot, smallest first: the plugin's order × 100 + the tab's index.</param>
/// <param name="SeenMs">When it last published, on <see cref="Environment.TickCount64"/>.</param>
public readonly record struct DockSlot(string Key, int Edge, float Along, float Length, int Order, long SeenMs)
{
    public string Owner => Key.IndexOf('/') is var i and >= 0 ? Key[..i] : Key;
}

/// <summary>
/// Where every edge tab goes, across every plugin that docks tabs. Pure.
/// <para>
/// Each tab sits where it was put (<see cref="DockSlot.Along"/>; a tab never moved wants the middle). Tabs
/// that would overlap form a group laid end to end, centred on the average of where its tabs want to be —
/// so several tabs all wanting the middle stack centred, and one tab dragged near another nudges both
/// apart rather than covering it. Groups are kept on screen. A tab that stops publishing drops out after
/// <see cref="StaleMs"/> and the rest close up.
/// </para>
/// <para>
/// Ported from Daedalus (<c>Daedalus/Windows/Dock/DockLayout.cs</c>, 2026-10-19), which shares it with
/// Charon. All three must agree on the share tag and the slot layout, or each lays its tabs out on its own.
/// </para>
/// </summary>
public static class DockLayout
{
    /// <summary>A tab that has not published for this long is gone.</summary>
    public const long StaleMs = 1000;

    /// <summary>Space between two tabs of one plugin.</summary>
    public const float TabGap = 4f;

    /// <summary>Space between two plugins' tabs — wider, so you can see where one plugin's run ends.</summary>
    public const float GroupGap = 10f;

    public static float GapBetween(DockSlot a, DockSlot b) => a.Owner == b.Owner ? TabGap : GroupGap;

    /// <summary>Where each live tab on <paramref name="edge"/> starts along it, keyed by <see cref="DockSlot.Key"/>.</summary>
    public static Dictionary<string, float> Resolve(
        int edge, IEnumerable<DockSlot> slots, long nowMs, float workStart, float workLength)
    {
        var live = slots
            .Where(s => s.Edge == edge && s.Length > 0f && nowMs - s.SeenMs <= StaleMs)
            .OrderBy(s => s.Along)
            .ThenBy(s => s.Order)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var groups = new List<List<DockSlot>>();
        var starts = new List<float>();
        foreach (var slot in live)
        {
            groups.Add([slot]);
            starts.Add(StartOf(groups[^1], workStart, workLength));

            // Overlapping the group before it (or too close): join it, and settle the joined group afresh.
            while (groups.Count >= 2)
            {
                var a = groups.Count - 2;
                var b = a + 1;
                if (starts[a] + LengthOf(groups[a]) + GapBetween(groups[a][^1], groups[b][0]) <= starts[b])
                    break;

                groups[a].AddRange(groups[b]);
                groups.RemoveAt(b);
                starts.RemoveAt(b);
                starts[a] = StartOf(groups[a], workStart, workLength);
            }
        }

        var result = new Dictionary<string, float>();
        for (var g = 0; g < groups.Count; g++)
        {
            var at = starts[g];
            var group = groups[g];
            for (var i = 0; i < group.Count; i++)
            {
                result[group[i].Key] = at;
                if (i + 1 < group.Count)
                    at += group[i].Length + GapBetween(group[i], group[i + 1]);
            }
        }

        return result;
    }

    private static float LengthOf(List<DockSlot> group)
    {
        var length = 0f;
        for (var i = 0; i < group.Count; i++)
            length += group[i].Length + (i + 1 < group.Count ? GapBetween(group[i], group[i + 1]) : 0f);
        return length;
    }

    /// <summary>
    /// Each tab wants its centre at its <see cref="DockSlot.Along"/>; the group is centred on the average of
    /// those, kept on screen. Tabs that all want one spot are therefore centred on it as one run.
    /// </summary>
    private static float StartOf(List<DockSlot> group, float workStart, float workLength)
    {
        var wanted = 0f;
        foreach (var slot in group)
            wanted += workStart + Math.Clamp(slot.Along, 0f, 1f) * workLength;
        wanted /= group.Count;

        var length = LengthOf(group);
        var latest = MathF.Max(workStart, workStart + workLength - length);
        return Math.Clamp(wanted - length / 2f, workStart, latest);
    }
}

/// <summary>One of this plugin's tabs, asking for a place.</summary>
public readonly record struct DockRequest(string Id, DockEdge Edge, float Along, float Length);

/// <summary>
/// The shared list of tabs, held in Dalamud's data share so every plugin's dock sees the same one.
/// <para>
/// Only framework types cross the boundary — a <c>ConcurrentDictionary&lt;string, long[]&gt;</c> keyed by
/// "Owner/TabId", each value <c>[edge, along × 10000, length, order, seenMs]</c> — because the data share
/// hands every plugin the SAME object and a type defined in one plugin's assembly would not match another's.
/// If the share cannot be had (a type clash with a different version), the tabs are laid out on their own.
/// </para>
/// </summary>
public sealed class DockLayoutShare : IDisposable
{
    /// <summary>The data-share tag. Daedalus and Charon use the same one; change all or none.</summary>
    public const string Tag = "EdgeDock.Layout.v2";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _owner;
    private readonly int _order;
    private readonly bool _shared;
    private readonly ConcurrentDictionary<string, long[]> _slots;

    public DockLayoutShare(IDalamudPluginInterface pluginInterface, string owner, int order)
    {
        _pluginInterface = pluginInterface;
        _owner = owner;
        _order = order;
        ConcurrentDictionary<string, long[]>? slots = null;
        try
        {
            slots = pluginInterface.GetOrCreateData(Tag, () => new ConcurrentDictionary<string, long[]>());
        }
        catch
        {
            // Laid out alone, below.
        }

        _shared = slots != null;
        _slots = slots ?? new ConcurrentDictionary<string, long[]>();
    }

    /// <summary>
    /// Publish this frame's tabs and get back where each one starts along its edge, keyed by tab id: its top
    /// on the left and right edges, its left on the top and bottom ones.
    /// </summary>
    public Dictionary<string, float> Place(IReadOnlyList<DockRequest> tabs, Vector2 workPos, Vector2 workSize)
    {
        var now = Environment.TickCount64;
        var prefix = _owner + "/";
        var mine = new HashSet<string>();
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            var key = prefix + t.Id;
            mine.Add(key);
            _slots[key] = [(int)t.Edge, (long)MathF.Round(Math.Clamp(t.Along, 0f, 1f) * 10000f),
                           (long)MathF.Ceiling(t.Length), _order * 100 + i, now];
        }

        // A tab of ours that is gone this frame leaves at once, rather than holding its place for a second.
        foreach (var key in _slots.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && !mine.Contains(key))
                _slots.TryRemove(key, out _);
        }

        var all = Read(_slots).ToList();
        var result = new Dictionary<string, float>();
        foreach (var edge in tabs.Select(t => t.Edge).Distinct())
        {
            var horizontal = DockMath.IsHorizontal(edge);
            var starts = DockLayout.Resolve((int)edge, all, now,
                horizontal ? workPos.X : workPos.Y, horizontal ? workSize.X : workSize.Y);
            foreach (var t in tabs.Where(t => t.Edge == edge))
            {
                if (starts.TryGetValue(prefix + t.Id, out var start))
                    result[t.Id] = start;
            }
        }

        return result;
    }

    private static IEnumerable<DockSlot> Read(ConcurrentDictionary<string, long[]> slots)
    {
        foreach (var (key, v) in slots)
        {
            if (v is { Length: >= 5 })
                yield return new DockSlot(key, (int)v[0], v[1] / 10000f, v[2], (int)v[3], v[4]);
        }
    }

    public void Dispose()
    {
        var prefix = _owner + "/";
        foreach (var key in _slots.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _slots.TryRemove(key, out _);
        }

        if (_shared)
            _pluginInterface.RelinquishData(Tag);
    }
}
