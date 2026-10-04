using Ariadne.Config;
using Ariadne.Mnemosyne;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Windows;

// The navmesh Mnemosyne serves, drawn over the world around the player - vnavmesh's mesh debug
// view, for seeing why a route goes where it goes: whether the floor under a snag is mesh at all,
// whether a ledge or a table top is joined to the floor (Eulmore's 1.0 y step height put routes on
// the Canopy's tables), and where the mesh's walls are. Asked for 2026-10-04.
//
// The mesh lives in the service, so this asks it for the polys near the player (meshNear) at most
// once a second, or after 3 y of movement, one request at a time; each frame only draws the cached
// answer. Same ImGui projection as the collision view - no depth test, so it shows through walls.
//
// Colours: green = reachable from where you stand, yellow = walkable but cut off from you,
// red = blocked (an override or the step-height limit), grey = walkable when you are off the mesh.
// Bright edges are the mesh's walls, cyan lines are links, magenta rings are the obstacles route
// padding keeps clear of (posts, poles).
internal sealed class MeshOverlay
{
    private const uint LinkColor = 0xE0FFFF00;
    private const uint ObstacleColor = 0xC0FF40FF;

    private static readonly (uint Fill, uint Edge, uint Wall)[] StateColors =
    [
        (0x18A0A0A0, 0x40A0A0A0, 0xC0D0D0D0), // walkable, no start
        (0x2000C000, 0x5000C000, 0xE000FF00), // reachable
        (0x2000C0E0, 0x5000C0E0, 0xE000E0FF), // cut off
        (0x302020E0, 0x602020E0, 0xE02020FF), // blocked
    ];

    private readonly AriadneConfig _config;
    private readonly MeshBroker _broker;
    private readonly Func<Vector3?> _playerPosition;
    private volatile Snapshot? _snapshot;
    private Task? _pending;
    private long _askedAtMs;
    private Vector3 _askedAt;

    private sealed record Snapshot(List<Vector3[]> Polys, List<int> States, List<int> Walls,
        List<(Vector3 A, Vector3 B)> Links, List<(Vector3 Base, float Radius, float Height)> Obstacles,
        bool Truncated, bool OnMesh);

    public MeshOverlay(AriadneConfig config, MeshBroker broker, Func<Vector3?> playerPosition)
    {
        _config = config;
        _broker = broker;
        _playerPosition = playerPosition;
    }

    public int PolyCount => _snapshot?.Polys.Count ?? 0;
    public bool Truncated => _snapshot?.Truncated == true;
    public bool OnMesh => _snapshot?.OnMesh != false;

    public void Draw()
    {
        if (!_config.ShowMesh || _playerPosition() is not { } player)
        {
            _snapshot = null;
            return;
        }

        var now = Environment.TickCount64;
        if (_pending is not { IsCompleted: false }
            && (now - _askedAtMs > 1000 || Vector3.DistanceSquared(player, _askedAt) > 9))
        {
            _askedAtMs = now;
            _askedAt = player;
            _pending = Fetch(player, Math.Clamp(_config.MeshRadius, 5f, 100f));
        }

        if (_snapshot is not { } snap)
            return;
        var drawList = ImGui.GetBackgroundDrawList();
        Span<Vector2> screen = stackalloc Vector2[16];
        for (int p = 0; p < snap.Polys.Count; ++p)
        {
            var poly = snap.Polys[p];
            var colors = StateColors[Math.Clamp(snap.States[p], 0, StateColors.Length - 1)];
            var all = poly.Length <= screen.Length;
            for (int i = 0; i < poly.Length && i < screen.Length; ++i)
                all &= Service.GameGui.WorldToScreen(poly[i], out screen[i]);
            if (all && _config.MeshFill)
                drawList.AddConvexPolyFilled(ref screen[0], poly.Length, colors.Fill);
            for (int i = 0; i < poly.Length; ++i)
            {
                var wall = (snap.Walls[p] & (1 << i)) != 0;
                Line(drawList, poly[i], poly[(i + 1) % poly.Length], wall ? colors.Wall : colors.Edge, wall ? 2f : 1f);
            }
        }
        foreach (var (a, b) in snap.Links)
            Line(drawList, a, b, LinkColor, 2f);
        foreach (var (center, radius, height) in snap.Obstacles)
        {
            const int segments = 12;
            for (int i = 0; i < segments; ++i)
            {
                float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                var p0 = center + new Vector3(MathF.Sin(a0) * radius, 0, MathF.Cos(a0) * radius);
                var p1 = center + new Vector3(MathF.Sin(a1) * radius, 0, MathF.Cos(a1) * radius);
                Line(drawList, p0, p1, ObstacleColor, 1.5f);
                if (i % 3 == 0)
                    Line(drawList, p0, p0 + new Vector3(0, height, 0), ObstacleColor, 1f);
            }
        }
    }

    private static void Line(ImDrawListPtr drawList, Vector3 a, Vector3 b, uint color, float thickness)
    {
        if (Service.GameGui.WorldToScreen(a, out var sa) && Service.GameGui.WorldToScreen(b, out var sb))
            drawList.AddLine(sa, sb, color, thickness);
    }

    private async Task Fetch(Vector3 player, float radius)
    {
        var resp = await _broker.MeshNearAsync(player, radius).ConfigureAwait(false);
        if (resp == null || !_config.ShowMesh)
        {
            _snapshot = null;
            return;
        }

        var counts = resp.Counts ?? [];
        var verts = resp.Verts ?? [];
        var states = resp.States ?? [];
        var walls = resp.Walls ?? [];
        var polys = new List<Vector3[]>(counts.Length);
        int v = 0;
        for (int p = 0; p < counts.Length && p < states.Length && p < walls.Length; ++p)
        {
            if (counts[p] < 3 || v + counts[p] * 3 > verts.Length)
                break; // a malformed answer: draw what was whole
            var poly = new Vector3[counts[p]];
            for (int i = 0; i < poly.Length; ++i, v += 3)
                poly[i] = new Vector3(verts[v], verts[v + 1], verts[v + 2]);
            polys.Add(poly);
        }

        var links = new List<(Vector3, Vector3)>();
        foreach (var l in resp.Links ?? [])
            if (l is { Length: >= 6 })
                links.Add((new Vector3(l[0], l[1], l[2]), new Vector3(l[3], l[4], l[5])));
        var obstacles = new List<(Vector3, float, float)>();
        foreach (var o in resp.Obstacles ?? [])
            if (o is { Length: >= 5 })
                obstacles.Add((new Vector3(o[0], o[1], o[2]), o[3], o[4]));

        _snapshot = new Snapshot(polys, [.. states[..polys.Count]], [.. walls[..polys.Count]], links, obstacles,
            resp.Truncated, resp.Start != null);
    }
}
