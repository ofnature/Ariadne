using System.Linq;
using System.Numerics;

namespace Ariadne.Zone;

// The wire form of a live scene capture — what `buildZone` ships to Mnemosyne so its
// out-of-process builder can reproduce the exact variant the game is showing (festival
// layers, shared-group states, live instances). Plain data, System.Text.Json-friendly,
// camelCase on the wire via Protocol.JsonOptions. Both ends are C#, so ulong keys are
// fine as JSON numbers.

public sealed class TransformDto
{
    public float[] T { get; set; } = []; // translation xyz
    public float[] R { get; set; } = []; // rotation quaternion xyzw
    public float[] S { get; set; } = []; // scale xyz

    /// <summary>Analytic collider kind (0 box, 1 sphere, 2 cylinder, 3 plane), carried on
    /// analytic-shape transforms only. SceneExtractor switches on this to decide what to
    /// rasterize — omitting it turns every sphere and cylinder in the zone into a box.
    /// Added 2026-08-24 after an offline round-trip built a mesh 60 polys light.</summary>
    public int Type { get; set; }
}

public sealed class AnalyticShapeDto
{
    public uint Crc { get; set; }
    public TransformDto Transform { get; set; } = new();
    public float[] BbMin { get; set; } = [];
    public float[] BbMax { get; set; } = [];
}

public sealed class MeshPathDto
{
    public uint Crc { get; set; }
    public string Path { get; set; } = "";
}

public sealed class BgPartDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
    public uint Crc { get; set; }
    public ulong MatId { get; set; }
    public ulong MatMask { get; set; }
    public bool Analytic { get; set; }
}

public sealed class ColliderDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
    public uint Crc { get; set; }
    public ulong MatId { get; set; }
    public ulong MatMask { get; set; }
    public int Type { get; set; } // FFXIVClientStructs ColliderType
}

public sealed class ExitRangeDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
}

public sealed class SceneCaptureDto
{
    public string CacheKey { get; set; } = "";
    public uint TerritoryId { get; set; }
    public uint CfcId { get; set; }
    public uint[] FestivalLayers { get; set; } = [];
    public uint[] ZoneSGs { get; set; } = [];
    public string[] Terrains { get; set; } = [];
    public AnalyticShapeDto[] AnalyticShapes { get; set; } = [];
    public MeshPathDto[] MeshPaths { get; set; } = [];
    public BgPartDto[] BgParts { get; set; } = [];
    public ColliderDto[] Colliders { get; set; } = [];
    public ExitRangeDto[] ExitRanges { get; set; } = [];

    public int InstanceCount => BgParts.Length + Colliders.Length;
}

internal static class SceneCapture
{
    /// <summary>Capture the active layout as a wire-ready DTO. MUST run on the framework
    /// thread (reads live game memory). Returns null when the layout isn't ready.</summary>
    public static SceneCaptureDto? CaptureActive(string cacheKey)
    {
        if (cacheKey.Length == 0)
            return null;

        var scene = new SceneDefinition();
        scene.FillFromActiveLayout();
        if (scene.TerritoryID == 0 && scene.Terrains.Count == 0 && scene.BgParts.Count == 0)
            return null; // nothing captured — layout wasn't actually ready

        return new SceneCaptureDto
        {
            CacheKey = cacheKey,
            TerritoryId = scene.TerritoryID,
            CfcId = scene.CFCID,
            FestivalLayers = [.. scene.FestivalLayers],
            ZoneSGs = [.. scene.ZoneSGs],
            Terrains = [.. scene.Terrains],
            AnalyticShapes = [.. scene.AnalyticShapes.Select(kv => new AnalyticShapeDto
            {
                Crc = kv.Key,
                Transform = Convert(kv.Value.transform),
                BbMin = ToArray(kv.Value.bbMin),
                BbMax = ToArray(kv.Value.bbMax),
            })],
            MeshPaths = [.. scene.MeshPaths.Select(kv => new MeshPathDto { Crc = kv.Key, Path = kv.Value })],
            BgParts = [.. scene.BgParts.Select(p => new BgPartDto
            {
                Key = p.key, Transform = Convert(p.transform), Crc = p.crc,
                MatId = p.matId, MatMask = p.matMask, Analytic = p.analytic,
            })],
            Colliders = [.. scene.Colliders.Select(c => new ColliderDto
            {
                Key = c.key, Transform = Convert(c.transform), Crc = c.crc,
                MatId = c.matId, MatMask = c.matMask, Type = (int)c.type,
            })],
            ExitRanges = [.. scene.ExitRanges.Select(e => new ExitRangeDto { Key = e.key, Transform = Convert(e.transform) })],
        };
    }

    private static TransformDto Convert(FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform t) => new()
    {
        T = ToArray(t.Translation),
        R = [t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W],
        S = ToArray(t.Scale),
        Type = t.Type,
    };

    private static float[] ToArray(Vector3 v) => [v.X, v.Y, v.Z];
}
