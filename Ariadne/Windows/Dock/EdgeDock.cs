using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Windows.Dock;

/// <summary>
/// Which tab's panel is out. Pointing at a tab opens it; it stays while the pointer is on the tab or the
/// panel, and closes a moment after it leaves — the delay is what stops it flickering shut on the way
/// across the gap. Clicking a tab PINS its panel open (click again to unpin).
/// <para>
/// Ported from Daedalus's edge dock (<c>Daedalus/Windows/Dock/EdgeDock.cs</c>), itself a port of Charon's.
/// Pure: no Dalamud or ImGui.
/// </para>
/// </summary>
public sealed class DockHover
{
    private DateTime _lastTouchedUtc;

    public string? Open { get; private set; }
    public string? Pinned { get; private set; }

    /// <param name="hoveredTab">The tab under the pointer this frame, if any.</param>
    /// <param name="panelBusy">The pointer is on the open panel, or the panel is mid-interaction (a combo open, a slider held).</param>
    public void Update(string? hoveredTab, bool panelBusy, DateTime nowUtc, TimeSpan closeDelay, IReadOnlyList<string> visible)
    {
        // A tab that is no longer shown takes its panel (and its pin) with it.
        if (Pinned is { } pinned && !Contains(visible, pinned))
            Pinned = null;
        if (Open is { } open && !Contains(visible, open))
            Open = null;

        if (hoveredTab is { } hovered && Contains(visible, hovered))
        {
            Open = hovered;
            _lastTouchedUtc = nowUtc;
            return;
        }

        if (panelBusy)
        {
            _lastTouchedUtc = nowUtc;
            return;
        }

        if (Open != null && nowUtc - _lastTouchedUtc > closeDelay)
            Open = Pinned; // back to the pinned panel, or to nothing
    }

    /// <summary>A click on a tab: pin it, or unpin it if it already was.</summary>
    public void Click(string tab)
    {
        Pinned = Pinned == tab ? null : tab;
        if (Pinned != null)
            Open = Pinned;
    }

    private static bool Contains(IReadOnlyList<string> tabs, string tab)
    {
        foreach (var t in tabs)
        {
            if (t == tab)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Sideways text. ImGui cannot draw rotated text, so the label is drawn normally and its vertices are then
/// turned a quarter-turn anticlockwise about a pivot. Text of size (w, h) drawn at the pivot lands, once
/// turned, in a box of size (h, w) whose top-left is where the caller asked — reading bottom-to-top.
/// </summary>
public static class DockMath
{
    /// <summary>Where to draw the unrotated text so that, turned, its box has this top-left corner.</summary>
    public static Vector2 PivotFor(Vector2 topLeft, Vector2 textSize) => new(topLeft.X, topLeft.Y + textSize.X);

    /// <summary>A quarter-turn anticlockwise about <paramref name="pivot"/>, in screen space (Y grows downward).</summary>
    public static Vector2 TurnUp(Vector2 point, Vector2 pivot)
    {
        var d = point - pivot;
        return pivot + new Vector2(d.Y, -d.X);
    }

    /// <summary>
    /// A vertex of text DRAWN at <paramref name="drawnAt"/>, turned a quarter-turn and moved so the text's
    /// origin lands on <paramref name="pivot"/>. Drawing somewhere else first is not optional: ImGui drops
    /// glyphs that fall outside the screen BEFORE they are turned, so text drawn at a pivot on the right edge
    /// loses its tail. Draw where the whole line is on screen, then place it with this.
    /// </summary>
    public static Vector2 TurnUpAndPlace(Vector2 point, Vector2 drawnAt, Vector2 pivot)
    {
        var d = point - drawnAt;
        return pivot + new Vector2(d.Y, -d.X);
    }
}
