using Ariadne.Config;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ariadne.Windows;

// The game's own collision around the player, drawn as wireframe - what vnavmesh's collision
// debug view shows, for checking a route against what the character actually bumps into.
// Found needed 2026-10-02: Kholusia's mesh ran straight through a rock cluster a capture had
// missed, and nothing in game showed that the rock was solid while the mesh said it was floor.
//
// Read from the live collision scenes (the same source vnavmesh's DebugGameCollision uses), so it
// shows what is loaded right now, not what any mesh was built from. Same ImGui projection as the
// waypoint overlay - no 3D renderer. Gathering walks every collider, so it runs at most twice a
// second, or when the player has moved; each frame only draws the cached lines.
//
// Colours: green = floor (gentle enough to stand on), grey = wall or steep, red = marked never
// walkable (zone edges, arena holes), blue = fly-through, magenta = boxes/cylinders/spheres/planes.
internal sealed unsafe class CollisionOverlay
{
    private const uint FloorColor = 0x7000C000;
    private const uint WallColor = 0x60B0B0B0;
    private const uint UnwalkableColor = 0xA02020FF;
    private const uint FlyThroughColor = 0x80FF8020;
    private const uint ShapeColor = 0xB0FF40FF;
    private const int MaxLines = 20_000;
    private const float FloorNormalY = 0.7f; // ~45 degrees

    // vnavmesh's own layout fix for streamed (terrain) colliders: FFXIVClientStructs' offsets
    // moved in 7.4 ("FIXME: fields moved +8 bytes in 7.4", DebugGameCollision.cs)
    [StructLayout(LayoutKind.Explicit, Size = 0x1F0)]
    private struct ColliderStreamedEx
    {
        [FieldOffset(0x1D0)] public ColliderStreamed.FileHeader* Header;
        [FieldOffset(0x1D8)] public ColliderStreamed.FileEntry* Entries;
        [FieldOffset(0x1E0)] public ColliderStreamed.Element* Elements;
    }

    private readonly AriadneConfig _config;
    private readonly Func<Vector3?> _playerPosition;
    private readonly List<(Vector3 A, Vector3 B, uint Color)> _lines = [];
    private long _gatheredAtMs;
    private Vector3 _gatheredAt;
    private bool _truncated;

    // local radius of each mesh collider's vertices, so far-away meshes are skipped without
    // transforming them; keyed by collider and mesh pointer, rebuilt when either changes
    private readonly Dictionary<nint, (nint Mesh, float Radius)> _meshRadius = [];

    public CollisionOverlay(AriadneConfig config, Func<Vector3?> playerPosition)
    {
        _config = config;
        _playerPosition = playerPosition;
    }

    public int LineCount => _lines.Count;
    public bool Truncated => _truncated;

    public void Draw()
    {
        if (!_config.ShowCollision || _playerPosition() is not { } player)
            return;

        var now = Environment.TickCount64;
        if (now - _gatheredAtMs > 500 || Vector3.DistanceSquared(player, _gatheredAt) > 9)
        {
            Gather(player, Math.Clamp(_config.CollisionRadius, 5f, 100f));
            _gatheredAtMs = now;
            _gatheredAt = player;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var (a, b, color) in _lines)
            if (Service.GameGui.WorldToScreen(a, out var sa) && Service.GameGui.WorldToScreen(b, out var sb))
                drawList.AddLine(sa, sb, color, 1f);
    }

    private void Gather(Vector3 player, float radius)
    {
        _lines.Clear();
        _truncated = false;
        var module = Framework.Instance()->BGCollisionModule;
        if (module == null || module->SceneManager == null)
            return;

        foreach (var wrapper in module->SceneManager->Scenes)
        {
            if (wrapper == null || wrapper->Scene == null)
                continue;
            foreach (var coll in wrapper->Scene->Colliders)
            {
                if (coll == null || _lines.Count >= MaxLines)
                    continue;
                switch (coll->GetColliderType())
                {
                    case ColliderType.Streamed:
                        AddStreamed((ColliderStreamedEx*)coll, player, radius);
                        break;
                    case ColliderType.Mesh:
                        AddMesh((ColliderMesh*)coll, player, radius);
                        break;
                    case ColliderType.Box:
                        AddBox(ref ((ColliderBox*)coll)->World, player, radius);
                        break;
                    case ColliderType.Cylinder:
                        AddCylinder(ref ((ColliderCylinder*)coll)->World, player, radius);
                        break;
                    case ColliderType.Plane:
                    case ColliderType.PlaneTwoSided:
                        AddPlane(ref ((ColliderPlane*)coll)->World, player, radius);
                        break;
                }
            }
        }
        _truncated = _lines.Count >= MaxLines;
    }

    // terrain: a grid of meshes, each with world bounds in its file entry
    private void AddStreamed(ColliderStreamedEx* coll, Vector3 player, float radius)
    {
        if (coll->Header == null || coll->Entries == null || coll->Elements == null)
            return;
        for (int i = 0; i < coll->Header->NumMeshes; ++i)
        {
            ref var bounds = ref coll->Entries[i].Bounds;
            if (player.X < bounds.Min.X - radius || player.X > bounds.Max.X + radius
                || player.Z < bounds.Min.Z - radius || player.Z > bounds.Max.Z + radius)
                continue;
            AddMesh(coll->Elements[i].Mesh, player, radius);
        }
    }

    private void AddMesh(ColliderMesh* coll, Vector3 player, float radius)
    {
        if (coll == null || coll->MeshIsSimple || coll->Mesh == null)
            return;
        var mesh = (MeshPCB*)coll->Mesh;
        ref var world = ref coll->World;

        // skip meshes whose every vertex is out of range, judged from the translation and a
        // cached local radius scaled by the transform's largest axis
        if (!_meshRadius.TryGetValue((nint)coll, out var known) || known.Mesh != (nint)coll->Mesh)
        {
            known = ((nint)coll->Mesh, LocalRadius(mesh->RootNode));
            _meshRadius[(nint)coll] = known;
        }
        var scale = MathF.Max(world.Row0.Length(), MathF.Max(world.Row1.Length(), world.Row2.Length()));
        if (Horizontal(world.Row3, player) > radius + known.Radius * scale)
            return;

        var objMatId = coll->Collider.ObjectMaterialValue & coll->Collider.ObjectMaterialMask;
        var objMatInvMask = ~coll->Collider.ObjectMaterialMask;
        AddNode(mesh->RootNode, ref world, objMatId, objMatInvMask, player, radius);
    }

    private void AddNode(MeshPCB.FileNode* node, ref Matrix4x3 world, ulong objMatId, ulong objMatInvMask, Vector3 player, float radius)
    {
        if (node == null || _lines.Count >= MaxLines)
            return;
        foreach (ref var prim in node->Primitives)
        {
            var a = world.TransformCoordinate(node->Vertex(prim.V1));
            var b = world.TransformCoordinate(node->Vertex(prim.V2));
            var c = world.TransformCoordinate(node->Vertex(prim.V3));
            if (!Near(a, player, radius) && !Near(b, player, radius) && !Near(c, player, radius))
                continue;
            var color = Classify(objMatId | (objMatInvMask & prim.Material), a, b, c);
            _lines.Add((a, b, color));
            _lines.Add((b, c, color));
            _lines.Add((c, a, color));
            if (_lines.Count >= MaxLines)
                return;
        }
        AddNode(node->Child1, ref world, objMatId, objMatInvMask, player, radius);
        AddNode(node->Child2, ref world, objMatId, objMatInvMask, player, radius);
    }

    // material bits as vnavmesh's SceneExtractor reads them
    private static uint Classify(ulong material, Vector3 a, Vector3 b, Vector3 c)
    {
        if ((material & 0x2000000) != 0 || (material & 0x1F) == 0x11)
            return UnwalkableColor;
        if ((material & (0x100000 | 0x1000000 | 0x800000)) != 0)
            return FlyThroughColor;
        var normal = Vector3.Cross(b - a, c - a);
        var length = normal.Length();
        return length > 1e-6f && MathF.Abs(normal.Y / length) >= FloorNormalY ? FloorColor : WallColor;
    }

    private void AddBox(ref Matrix4x3 world, Vector3 player, float radius)
    {
        if (Horizontal(world.Row3, player) > radius)
            return;
        Span<Vector3> v = stackalloc Vector3[8];
        for (int i = 0; i < 8; ++i)
            v[i] = world.TransformCoordinate(new((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
        ReadOnlySpan<(int, int)> edges = [(0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7)];
        foreach (var (i, j) in edges)
            _lines.Add((v[i], v[j], ShapeColor));
    }

    private void AddCylinder(ref Matrix4x3 world, Vector3 player, float radius)
    {
        if (Horizontal(world.Row3, player) > radius)
            return;
        const int segments = 16;
        var prevTop = world.TransformCoordinate(new(0, 1, 1));
        var prevBottom = world.TransformCoordinate(new(0, -1, 1));
        for (int i = 1; i <= segments; ++i)
        {
            var angle = i * MathF.Tau / segments;
            var top = world.TransformCoordinate(new(MathF.Sin(angle), 1, MathF.Cos(angle)));
            var bottom = world.TransformCoordinate(new(MathF.Sin(angle), -1, MathF.Cos(angle)));
            _lines.Add((prevTop, top, ShapeColor));
            _lines.Add((prevBottom, bottom, ShapeColor));
            if (i % 4 == 0)
                _lines.Add((top, bottom, ShapeColor));
            (prevTop, prevBottom) = (top, bottom);
        }
    }

    private void AddPlane(ref Matrix4x3 world, Vector3 player, float radius)
    {
        if (Horizontal(world.Row3, player) > radius)
            return;
        var a = world.TransformCoordinate(new(-1, 1, 0));
        var b = world.TransformCoordinate(new(-1, -1, 0));
        var c = world.TransformCoordinate(new(1, -1, 0));
        var d = world.TransformCoordinate(new(1, 1, 0));
        _lines.Add((a, b, ShapeColor));
        _lines.Add((b, c, ShapeColor));
        _lines.Add((c, d, ShapeColor));
        _lines.Add((d, a, ShapeColor));
    }

    private static float LocalRadius(MeshPCB.FileNode* node)
    {
        if (node == null)
            return 0;
        var r = 0f;
        for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
            r = MathF.Max(r, node->Vertex(i).Length());
        return MathF.Max(r, MathF.Max(LocalRadius(node->Child1), LocalRadius(node->Child2)));
    }

    private static bool Near(Vector3 p, Vector3 player, float radius) =>
        Horizontal(p, player) <= radius && MathF.Abs(p.Y - player.Y) <= radius;

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
