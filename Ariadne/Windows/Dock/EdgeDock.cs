using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Windows.Dock;

/// <summary>
/// The screen edge a tab is pinned to. The values are the shared layout's edge numbers, the same in
/// Daedalus and Charon.
/// </summary>
public enum DockEdge
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

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
/// Sideways text and edge geometry. ImGui cannot draw rotated text, so the label is drawn normally and its
/// vertices are then turned a quarter-turn anticlockwise about a pivot. Text of size (w, h) drawn at the
/// pivot lands, once turned, in a box of size (h, w) whose top-left is where the caller asked — reading
/// bottom-to-top.
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

    /// <summary>Top and bottom tabs run along the edge; left and right ones stand up the edge.</summary>
    public static bool IsHorizontal(DockEdge edge) => edge is DockEdge.Top or DockEdge.Bottom;

    /// <summary>The screen edge closest to <paramref name="point"/> — where a dragged tab lands when let go.</summary>
    public static DockEdge NearestEdge(Vector2 point, Vector2 workPos, Vector2 workSize)
    {
        var best = DockEdge.Left;
        var bestDistance = point.X - workPos.X;

        void Consider(DockEdge edge, float distance)
        {
            if (distance < bestDistance)
            {
                best = edge;
                bestDistance = distance;
            }
        }

        Consider(DockEdge.Right, workPos.X + workSize.X - point.X);
        Consider(DockEdge.Top, point.Y - workPos.Y);
        Consider(DockEdge.Bottom, workPos.Y + workSize.Y - point.Y);
        return best;
    }

    /// <summary>How far along <paramref name="edge"/> a point is, as a fraction of the edge (0..1).</summary>
    public static float AlongFraction(DockEdge edge, Vector2 point, Vector2 workPos, Vector2 workSize)
    {
        var fraction = IsHorizontal(edge)
            ? (point.X - workPos.X) / MathF.Max(1f, workSize.X)
            : (point.Y - workPos.Y) / MathF.Max(1f, workSize.Y);
        return Math.Clamp(fraction, 0f, 1f);
    }

    /// <summary>
    /// A tab's rectangle: <paramref name="start"/> along the edge (its top, or its left on the top and bottom
    /// edges), <paramref name="length"/> long, <paramref name="thickness"/> out from the edge.
    /// </summary>
    public static (Vector2 Min, Vector2 Size) TabRect(
        DockEdge edge, float start, float length, float thickness, Vector2 workPos, Vector2 workSize) => edge switch
    {
        DockEdge.Right => (new Vector2(workPos.X + workSize.X - thickness, start), new Vector2(thickness, length)),
        DockEdge.Top => (new Vector2(start, workPos.Y), new Vector2(length, thickness)),
        DockEdge.Bottom => (new Vector2(start, workPos.Y + workSize.Y - thickness), new Vector2(length, thickness)),
        _ => (new Vector2(workPos.X, start), new Vector2(thickness, length)),
    };

    /// <summary>
    /// Where an open tab's panel goes: beside the tab, on the screen side of it, level with it — slid back
    /// along the edge if <paramref name="panelSize"/> (last frame's) would run it off the screen.
    /// </summary>
    /// <returns>The window position and the pivot that position refers to (as for SetNextWindowPos).</returns>
    public static (Vector2 Pos, Vector2 Pivot) PanelPlacement(
        DockEdge edge, Vector2 tabMin, Vector2 tabSize, Vector2 panelSize, Vector2 workPos, Vector2 workSize, float gap)
    {
        const float margin = 4f;
        var y = MathF.Max(workPos.Y + margin, MathF.Min(tabMin.Y, workPos.Y + workSize.Y - panelSize.Y - margin));
        var x = MathF.Max(workPos.X + margin, MathF.Min(tabMin.X, workPos.X + workSize.X - panelSize.X - margin));

        return edge switch
        {
            DockEdge.Right => (new Vector2(tabMin.X - gap, y), new Vector2(1f, 0f)),
            DockEdge.Top => (new Vector2(x, tabMin.Y + tabSize.Y + gap), Vector2.Zero),
            DockEdge.Bottom => (new Vector2(x, tabMin.Y - gap), new Vector2(0f, 1f)),
            _ => (new Vector2(tabMin.X + tabSize.X + gap, y), Vector2.Zero),
        };
    }
}
