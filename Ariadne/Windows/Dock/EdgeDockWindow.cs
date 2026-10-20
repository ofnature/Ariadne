using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Ariadne.Windows.Dock;

/// <summary>
/// The edge dock: tabs pinned to the screen edges, each popping its panel out beside it while pointed at
/// (click to pin). Ported from Daedalus's <c>EdgeDockWindow</c> (2026-10-19), itself ported from Charon's, so
/// the three docks look and behave the same and share one layout. Every tab has its own edge and its own
/// spot along it; dragging a tab and letting go puts it on the nearest edge, where it was let go.
/// <para>
/// This window itself is an invisible 1×1 host. Each tab is its own small ImGui window, so tabs can sit
/// anywhere on any edge, and <see cref="DockLayoutShare"/> keeps them from covering each other or the other
/// plugins' tabs.
/// </para>
/// <para>
/// A tab's panel is the BODY of the window it stands for — that window's own <c>Draw()</c>, called inside
/// the pop-out — so the dock and the window cannot drift apart.
/// </para>
/// <para>
/// On the left and right edges the label is ordinary text whose vertices are turned after it is drawn
/// (<see cref="DockMath"/>). It is drawn under a full-screen clip rect: ImGui culls glyphs against the clip
/// rect BEFORE they are turned, and the unturned text runs far outside a 34-pixel tab. On the top and bottom
/// edges the label reads normally.
/// </para>
/// </summary>
public sealed unsafe class EdgeDockWindow : Window
{
    /// <param name="Id">Stable id; also the ImGui id suffix and the layout key.</param>
    /// <param name="Label">What the tab reads.</param>
    /// <param name="Applies">Whether the tab is shown right now.</param>
    /// <param name="DrawPanel">The panel body — normally the target window's own <c>Draw()</c>.</param>
    /// <param name="PanelWidth">
    /// A fixed width with the height left to the content. Needed for bodies that size items off the space left
    /// in the window (full-width inputs, tables): in a window that sizes itself to its content, those chase
    /// each other. Null lets the panel size itself both ways.
    /// </param>
    public sealed record Tab(string Id, string Label, Func<bool> Applies, Action DrawPanel, float? PanelWidth = null);

    private const float TabThickness = 34f;
    private const float TabPadding = 14f;
    private const float PanelGap = 6f;
    private const float LabelScale = 1.25f;
    private const float DragThreshold = 6f;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(450);

    private const ImGuiWindowFlags TabFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    // Ariadne's thread: the teal the DTR entry and the waypoint overlay use, on the same dark tab fills as
    // the other two docks so a run of three reads as one strip.
    private static readonly Vector4 Accent = new(0.35f, 0.85f, 0.80f, 1f);
    private static readonly Vector4 TextSecondary = new(0.70f, 0.70f, 0.72f, 1f);
    private static readonly Vector4 StatusRed = new(0.9f, 0.35f, 0.35f, 1f);

    private readonly IReadOnlyList<Tab> _tabs;
    private readonly Func<string, (DockEdge Edge, float Along)> _placement;
    private readonly Action<string, DockEdge, float> _moveTab;
    private readonly DockLayoutShare _layout;
    private readonly DockHover _hover = new();
    private List<Tab> _visible = new();
    private Vector2 _panelSize = new(560f, 240f);
    private string? _dragging;

    /// <param name="placement">Where a tab is set to sit: its edge, and its centre as a fraction along it.</param>
    /// <param name="moveTab">A tab was dragged and let go: store its new edge and spot.</param>
    /// <param name="layout">The layout shared with other plugins' tabs, so none cover each other.</param>
    public EdgeDockWindow(
        IReadOnlyList<Tab> tabs, Func<string, (DockEdge Edge, float Along)> placement,
        Action<string, DockEdge, float> moveTab, DockLayoutShare layout)
        : base("##AriadneEdgeDock")
    {
        _tabs = tabs;
        _placement = placement;
        _moveTab = moveTab;
        _layout = layout;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
        PositionCondition = ImGuiCond.Always;
        SizeCondition = ImGuiCond.Always;
        Size = Vector2.One;
        IsOpen = false;
    }

    /// <summary>What the dock is showing, for diagnostics.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>Whether any tab applies right now — the caller opens the window only then.</summary>
    public bool AnyTab()
    {
        _visible = _tabs.Where(t => t.Applies()).ToList();
        if (_visible.Count == 0)
            Status = "no tab applies here";
        return _visible.Count > 0;
    }

    public override void PreDraw() => Position = ImGui.GetMainViewport().WorkPos;

    public override void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        var workPos = viewport.WorkPos;
        var workSize = viewport.WorkSize;

        var requests = _visible.Select(t =>
        {
            var (edge, along) = _placement(t.Id);
            return new DockRequest(t.Id, edge, along, TabLength(t.Label));
        }).ToList();
        var starts = _layout.Place(requests, workPos, workSize);

        string? hovered = null;
        Tab? openTab = null;
        var openMin = Vector2.Zero;
        var openSize = Vector2.Zero;
        var openEdge = DockEdge.Left;

        for (var i = 0; i < _visible.Count; i++)
        {
            var tab = _visible[i];
            var edge = requests[i].Edge;
            if (!starts.TryGetValue(tab.Id, out var start))
                continue;

            var (min, size) = DockMath.TabRect(edge, start, requests[i].Length, TabThickness, workPos, workSize);
            if (DrawTabWindow(tab, edge, min, size, ref hovered))
            {
                openTab = tab;
                openMin = min;
                openSize = size;
                openEdge = edge;
            }
        }

        if (_dragging != null)
            DrawDrag(workPos, workSize);

        var panelBusy = openTab != null && DrawPanel(openTab, openEdge, openMin, openSize);
        _hover.Update(hovered, panelBusy, DateTime.UtcNow, CloseDelay, _visible.Select(t => t.Id).ToList());

        Status = $"{_visible.Count} tab(s): {string.Join(", ", requests.Select(r => $"{r.Id} {r.Edge} {r.Along:0.00}"))}"
                 + (_hover.Open is { } open ? $" · {open} out{(_hover.Pinned == open ? " (pinned)" : "")}" : "");
    }

    /// <summary>One tab, in its own little window. True when its panel is out.</summary>
    private bool DrawTabWindow(Tab tab, DockEdge edge, Vector2 min, Vector2 size, ref string? hovered)
    {
        ImGui.SetNextWindowPos(min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        var isOpen = false;
        if (ImGui.Begin($"##AriadneEdgeDockTab{tab.Id}", TabFlags))
        {
            // A press that moves becomes a drag; only a press that does not is a click (pin / unpin).
            var clicked = ImGui.InvisibleButton($"##dock{tab.Id}", size);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, DragThreshold))
                _dragging = tab.Id;
            if (clicked && _dragging != tab.Id)
                _hover.Click(tab.Id);
            if (ImGui.IsItemHovered() && _dragging == null)
                hovered = tab.Id;

            isOpen = _hover.Open == tab.Id && _dragging == null;
            var lit = isOpen || hovered == tab.Id || _dragging == tab.Id;
            DrawTab(ImGui.GetWindowDrawList(), min, size, tab.Label, lit, _hover.Pinned == tab.Id, edge);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
        return isOpen;
    }

    /// <summary>
    /// A tab being dragged: the edge it would land on lit in the accent, an outline where it would sit, and
    /// on release the move.
    /// </summary>
    private void DrawDrag(Vector2 workPos, Vector2 workSize)
    {
        var mouse = ImGui.GetMousePos();
        var target = DockMath.NearestEdge(mouse, workPos, workSize);
        var along = DockMath.AlongFraction(target, mouse, workPos, workSize);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var id = _dragging!;
            _dragging = null;
            _moveTab(id, target, along);
            return;
        }

        var fg = ImGui.GetForegroundDrawList();
        var accent = ImGui.GetColorU32(Accent);
        var a = workPos;
        var b = workPos + workSize;
        const float bar = 3f;
        switch (target)
        {
            case DockEdge.Left: fg.AddRectFilled(a, new Vector2(a.X + bar, b.Y), accent); break;
            case DockEdge.Right: fg.AddRectFilled(new Vector2(b.X - bar, a.Y), b, accent); break;
            case DockEdge.Top: fg.AddRectFilled(a, new Vector2(b.X, a.Y + bar), accent); break;
            case DockEdge.Bottom: fg.AddRectFilled(new Vector2(a.X, b.Y - bar), b, accent); break;
        }

        // The outline of where it would land (before any nudge from its neighbours).
        var label = _visible.FirstOrDefault(t => t.Id == _dragging)?.Label ?? _dragging!;
        var length = TabLength(label);
        var horizontal = DockMath.IsHorizontal(target);
        var edgeStart = horizontal ? workPos.X : workPos.Y;
        var edgeLength = horizontal ? workSize.X : workSize.Y;
        var start = Math.Clamp(edgeStart + along * edgeLength - length / 2f, edgeStart,
            MathF.Max(edgeStart, edgeStart + edgeLength - length));
        var (min, size) = DockMath.TabRect(target, start, length, TabThickness, workPos, workSize);
        fg.AddRect(min, min + size, accent, 6f, ImDrawFlags.None, 2f);
    }

    private static float TabLength(string label) =>
        MathF.Max(90f, ImGui.CalcTextSize(label).X * LabelScale + TabPadding * 2f);

    private static void DrawTab(ImDrawListPtr drawList, Vector2 min, Vector2 size, string label, bool lit, bool pinned, DockEdge edge)
    {
        var max = min + size;
        var fill = lit ? new Vector4(0.12f, 0.16f, 0.16f, 0.96f) : new Vector4(0.10f, 0.10f, 0.12f, 0.88f);
        // Rounded on the side facing the screen, square against the edge it is pinned to.
        var corners = edge switch
        {
            DockEdge.Right => ImDrawFlags.RoundCornersLeft,
            DockEdge.Top => ImDrawFlags.RoundCornersBottom,
            DockEdge.Bottom => ImDrawFlags.RoundCornersTop,
            _ => ImDrawFlags.RoundCornersRight,
        };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f, corners);

        if (pinned)
        {
            // The accent on the tab's screen-facing side marks a pinned panel.
            var (barMin, barSize) = edge switch
            {
                DockEdge.Right => (min, new Vector2(2f, size.Y)),
                DockEdge.Top => (new Vector2(min.X, max.Y - 2f), new Vector2(size.X, 2f)),
                DockEdge.Bottom => (min, new Vector2(size.X, 2f)),
                _ => (new Vector2(max.X - 2f, min.Y), new Vector2(2f, size.Y)),
            };
            drawList.AddRectFilled(barMin, barMin + barSize, ImGui.GetColorU32(Accent));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * LabelScale;
        var textSize = ImGui.CalcTextSize(label) * LabelScale;
        var color = ImGui.GetColorU32(lit ? Accent : TextSecondary);

        if (DockMath.IsHorizontal(edge))
        {
            drawList.AddText(font, fontSize, min + (size - textSize) / 2f, color, label);
            return;
        }

        // Turned, the label is textSize.Y wide and textSize.X tall — centred in the tab.
        var topLeft = min + new Vector2((size.X - textSize.Y) / 2f, (size.Y - textSize.X) / 2f);
        var pivot = DockMath.PivotFor(topLeft, textSize);

        // Drawn first near the top-left of the screen, where the whole unturned line fits, then turned and
        // moved into the tab.
        var drawnAt = ImGui.GetMainViewport().WorkPos + new Vector2(8f, 8f);
        drawList.PushClipRectFullScreen();
        var first = drawList.VtxBuffer.Size;
        drawList.AddText(font, fontSize, drawnAt, color, label);
        var last = drawList.VtxBuffer.Size;
        var vertices = drawList.VtxBuffer.Data;
        for (var i = first; i < last; i++)
            vertices[i].Pos = DockMath.TurnUpAndPlace(vertices[i].Pos, drawnAt, pivot);
        drawList.PopClipRect();
    }

    /// <summary>Draws the open tab's panel beside it. True while the pointer is on it or it is mid-interaction.</summary>
    private bool DrawPanel(Tab tab, DockEdge edge, Vector2 tabMin, Vector2 tabSize)
    {
        var viewport = ImGui.GetMainViewport();
        var (pos, pivot) = DockMath.PanelPlacement(
            edge, tabMin, tabSize, _panelSize, viewport.WorkPos, viewport.WorkSize, PanelGap);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
        if (tab.PanelWidth is { } width)
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));

        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        var busy = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        if (ImGui.Begin($"##AriadneEdgeDockPanel{tab.Id}", flags))
        {
            try
            {
                tab.DrawPanel();
            }
            catch (Exception ex)
            {
                // Caught HERE: an exception leaving between Begin and End would skip the End and corrupt
                // ImGui's window stack for every plugin drawing after this one.
                ImGui.TextColored(StatusRed, $"This panel could not be drawn: {ex.Message}");
            }

            _panelSize = ImGui.GetWindowSize();

            // The panel also counts as "in use" while one of its own pop-ups is open or a control is held:
            // a combo's list is a separate window, and losing the panel under it closes the combo.
            busy = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                         | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                         | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                   || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                   || ImGui.IsAnyItemActive();
        }

        ImGui.End();
        ImGui.PopStyleVar();
        return busy;
    }
}
