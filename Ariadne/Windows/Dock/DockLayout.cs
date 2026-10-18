using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace Ariadne.Windows.Dock;

/// <summary>One plugin's dock as the shared layout sees it.</summary>
/// <param name="Owner">The plugin that owns the dock ("Daedalus", "Charon", "Ariadne").</param>
/// <param name="Edge">0 = left edge, 1 = right edge.</param>
/// <param name="Height">How tall its stack of tabs is this frame, in pixels.</param>
/// <param name="Order">Where it sits in the group, smallest first (top). Ties fall back to the owner's name.</param>
/// <param name="SeenMs">When it last published, on <see cref="Environment.TickCount64"/>.</param>
public readonly record struct DockSlot(string Owner, int Edge, float Height, int Order, long SeenMs);

/// <summary>
/// Where each plugin's edge dock goes when several share an edge. Pure.
/// <para>
/// The live docks on an edge are laid out as ONE group — stacked by <see cref="DockSlot.Order"/>, the group
/// centred — and each dock takes its own place in it. A dock that stops publishing (closed, no tab applies,
/// plugin unloaded) drops out after <see cref="StaleMs"/> and the rest close up.
/// </para>
/// <para>
/// Ported from Daedalus (<c>Daedalus/Windows/Dock/DockLayout.cs</c>), which ported it from Charon. All three
/// must agree on the share tag and the slot layout, or each falls back to centring alone.
/// </para>
/// </summary>
public static class DockLayout
{
    /// <summary>A dock that has not published for this long is gone.</summary>
    public const long StaleMs = 1000;

    /// <summary>Space between two plugins' docks — wider than the gap between one dock's own tabs.</summary>
    public const float GroupGap = 10f;

    /// <summary>
    /// The top of <paramref name="owner"/>'s dock. <paramref name="slots"/> must already include the owner's
    /// own slot for this frame; if it does not, the dock is centred on its own.
    /// </summary>
    public static float TopFor(
        string owner, int edge, float ownHeight, IEnumerable<DockSlot> slots, long nowMs,
        float workTop, float workHeight)
    {
        var live = slots
            .Where(s => s.Edge == edge && s.Height > 0f && nowMs - s.SeenMs <= StaleMs)
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Owner, StringComparer.Ordinal)
            .ToList();

        var total = live.Sum(s => s.Height) + GroupGap * Math.Max(0, live.Count - 1);
        var top = workTop + MathF.Max(0f, (workHeight - total) / 2f);
        foreach (var slot in live)
        {
            if (slot.Owner == owner)
                return top;
            top += slot.Height + GroupGap;
        }

        return workTop + MathF.Max(0f, (workHeight - ownHeight) / 2f);
    }
}

/// <summary>
/// The shared list of docks, held in Dalamud's data share so every plugin's dock sees the same one.
/// <para>
/// Only framework types cross the boundary — a <c>ConcurrentDictionary&lt;string, long[]&gt;</c> keyed by
/// owner, each value <c>[edge, height, order, seenMs]</c> — because the data share hands every plugin the
/// SAME object and a type defined in one plugin's assembly would not match another's. If the share cannot be
/// had (a type clash with a different version), the dock simply centres on its own.
/// </para>
/// </summary>
public sealed class DockLayoutShare : IDisposable
{
    /// <summary>The data-share tag. Daedalus and Charon use the same one; change all or none.</summary>
    public const string Tag = "EdgeDock.Layout.v1";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _owner;
    private readonly int _order;
    private readonly ConcurrentDictionary<string, long[]>? _slots;

    public DockLayoutShare(IDalamudPluginInterface pluginInterface, string owner, int order)
    {
        _pluginInterface = pluginInterface;
        _owner = owner;
        _order = order;
        try
        {
            _slots = pluginInterface.GetOrCreateData(Tag, () => new ConcurrentDictionary<string, long[]>());
        }
        catch
        {
            _slots = null;
        }
    }

    /// <summary>Publish this frame's dock and get back where its top goes.</summary>
    public float Place(bool rightEdge, float height, float workTop, float workHeight)
    {
        var edge = rightEdge ? 1 : 0;
        if (_slots is null)
            return workTop + MathF.Max(0f, (workHeight - height) / 2f);

        var now = Environment.TickCount64;
        _slots[_owner] = [edge, (long)MathF.Ceiling(height), _order, now];
        return DockLayout.TopFor(_owner, edge, height, Read(_slots), now, workTop, workHeight);
    }

    private static IEnumerable<DockSlot> Read(ConcurrentDictionary<string, long[]> slots)
    {
        foreach (var (owner, v) in slots)
        {
            if (v is { Length: >= 4 })
                yield return new DockSlot(owner, (int)v[0], v[1], (int)v[2], v[3]);
        }
    }

    public void Dispose()
    {
        if (_slots is null)
            return;

        _slots.TryRemove(_owner, out _);
        _pluginInterface.RelinquishData(Tag);
    }
}
