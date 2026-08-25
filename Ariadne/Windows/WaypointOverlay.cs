using Ariadne.Config;
using Ariadne.Movement;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Windows;

// Draws the active path in the world: polyline through the remaining waypoints plus a
// marker per waypoint (current target highlighted). Lightweight ImGui-projection overlay
// (WorldToScreen onto the background draw list) — no 3D renderer to vendor, unlike
// vnavmesh's DX11 debug drawing. Subscribed to UiBuilder.Draw.
internal sealed class WaypointOverlay
{
    private const uint LineColor = 0x8000FFFF;    // translucent yellow (ABGR)
    private const uint PointColor = 0xFFFFB000;   // cyan-ish for pending waypoints
    private const uint CurrentColor = 0xFF00FFFF; // solid yellow for the active target
    private const uint DestColor = 0xFF4040FF;    // red-ish for the final destination
    private const uint PreviewLineColor = 0x80FF8040;  // translucent light blue
    private const uint PreviewPointColor = 0xFFFF8040; // light blue

    /// <summary>A path to draw without following it (FindPath-only preview). Swapped
    /// atomically from any thread; cleared on zone change or the Clear button.</summary>
    public volatile IReadOnlyList<Vector3>? PreviewPath;

    private readonly AriadneConfig _config;
    private readonly PathFollower _follower;
    private readonly Func<Vector3?> _playerPosition;

    public WaypointOverlay(AriadneConfig config, PathFollower follower, Func<Vector3?> playerPosition)
    {
        _config = config;
        _follower = follower;
        _playerPosition = playerPosition;
    }

    public void Draw()
    {
        if (!_config.ShowWaypoints)
            return;

        // preview (FindPath-only) draws even while idle, in its own colour; the active
        // path draws on top of it when both exist
        if (PreviewPath is { Count: > 0 } preview)
            DrawPath(preview, fromPlayer: false, PreviewLineColor, PreviewPointColor, PreviewPointColor, 1.5f);
        if (_follower.IsRunning)
            DrawPath(_follower.Waypoints, fromPlayer: true, LineColor, PointColor, CurrentColor, 2f);
    }

    private void DrawPath(IReadOnlyList<Vector3> waypoints, bool fromPlayer, uint lineColor, uint pointColor, uint firstColor, float thickness)
    {
        var drawList = ImGui.GetBackgroundDrawList();

        // player → first waypoint (active path only), then waypoint chain; WorldToScreen
        // fails for off-screen points, so each segment draws only when both ends project
        var prevProjected = false;
        var prev = default(Vector2);
        if (fromPlayer && _playerPosition() is { } playerPos)
            prevProjected = Service.GameGui.WorldToScreen(playerPos, out prev);

        for (var i = 0; i < waypoints.Count; i++)
        {
            var visible = Service.GameGui.WorldToScreen(waypoints[i], out var screen);
            if (visible)
            {
                if (prevProjected)
                    drawList.AddLine(prev, screen, lineColor, thickness);

                var isDest = i == waypoints.Count - 1;
                var color = isDest ? DestColor : i == 0 ? firstColor : pointColor;
                drawList.AddCircleFilled(screen, isDest ? 6f : 4f, color);
                drawList.AddCircle(screen, isDest ? 6f : 4f, 0xFF000000, 0, 1.5f);
            }
            prevProjected = visible;
            prev = screen;
        }
    }
}
