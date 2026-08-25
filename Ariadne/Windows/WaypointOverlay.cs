using Ariadne.Config;
using Ariadne.Movement;
using Dalamud.Bindings.ImGui;
using System;
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
        if (!_config.ShowWaypoints || !_follower.IsRunning)
            return;

        var waypoints = _follower.Waypoints;
        var drawList = ImGui.GetBackgroundDrawList();

        // player → first waypoint, then waypoint chain; WorldToScreen fails for
        // off-screen points, so each segment draws only when both ends project
        var prevProjected = false;
        var prev = default(Vector2);
        if (_playerPosition() is { } playerPos)
            prevProjected = Service.GameGui.WorldToScreen(playerPos, out prev);

        for (var i = 0; i < waypoints.Count; i++)
        {
            var visible = Service.GameGui.WorldToScreen(waypoints[i], out var screen);
            if (visible)
            {
                if (prevProjected)
                    drawList.AddLine(prev, screen, LineColor, 2f);

                var isDest = i == waypoints.Count - 1;
                var color = isDest ? DestColor : i == 0 ? CurrentColor : PointColor;
                drawList.AddCircleFilled(screen, isDest ? 6f : 4f, color);
                drawList.AddCircle(screen, isDest ? 6f : 4f, 0xFF000000, 0, 1.5f);
            }
            prevProjected = visible;
            prev = screen;
        }
    }
}
