using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ariadne.Mnemosyne;

// Wire types for the Mnemosyne named-pipe protocol. Spec: docs/mnemosyne-protocol.md —
// that file is the source of truth shared with the Mnemosyne project; change it first.

internal static class Protocol
{
    public const string PipeName = "mnemosyne";
    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class Request
{
    public int Id { get; set; }
    public string Op { get; set; } = "";
    public string? CacheKey { get; set; }
    public string? Path { get; set; }
    public float[]? From { get; set; }
    public float[]? To { get; set; }
    public bool? Fly { get; set; }

    // updateGameState fields
    public uint? TerritoryId { get; set; }
    public float[]? Pos { get; set; }
    public float? Rotation { get; set; }
    public bool? Flying { get; set; }

    // reportTraversal fields
    public string? Mode { get; set; }
    public bool? Success { get; set; }
    public string? Note { get; set; }

    // buildZone field — the live scene capture
    public Zone.SceneCaptureDto? Scene { get; set; }

    // --- added 2026-08-24 (Mnemosyne session): vnavmesh gate parity, spec'd in the
    // protocol doc. Query.Mesh.* / Nav.BuildBitmap* / pathfind variants / fleet identity.
    public float? Tolerance { get; set; }
    public float[]? AvoidCenter { get; set; }
    public float? AvoidRadius { get; set; }
    public float[]? Point { get; set; }
    public float? HalfExtentXZ { get; set; }
    public float? HalfExtentY { get; set; }
    public bool? ReachableOnly { get; set; }
    public bool? AllowUnreachable { get; set; }
    public float[][]? StartingPoints { get; set; }
    public string? Filename { get; set; }
    public float? PixelSize { get; set; }
    public float[]? MinBounds { get; set; }
    public float[]? MaxBounds { get; set; }
    public string? Character { get; set; }
    public ulong? ContentId { get; set; }
}

internal class Response
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>Why this answer looks the way it does, when ok/error alone would leave the
    /// consumer guessing. Every `ok:false` carries one; `ok:true` carries one only when the
    /// success is qualified. Tolerate unknown values — never treat one as fatal.</summary>
    public string? Result { get; set; }
}

internal sealed class HelloResponse : Response
{
    public int Protocol { get; set; }
    public string? App { get; set; }
    public string? Version { get; set; }
    public int MeshVersion { get; set; }
}

internal sealed class ZoneStatusResponse : Response
{
    public string? Status { get; set; } // "cached" | "missing" | "stale"
    public int Version { get; set; }
    public int Customization { get; set; }
    public float Progress { get; set; } = -1; // 0..1 while building, -1 idle
    public bool Building { get; set; }
    public bool PathfindInProgress { get; set; }
    public int PathfindNumQueued { get; set; }
}

internal sealed class GetMeshResponse : Response
{
    public string? Path { get; set; }
    public int Version { get; set; }
    public int Customization { get; set; }
    public long Size { get; set; }
}

internal sealed class PointResponse : Response
{
    public bool Found { get; set; }
    public float[]? Point { get; set; }
}

internal sealed class OnMeshResponse : Response
{
    public bool OnMesh { get; set; }
}

internal sealed class BitmapResponse : Response
{
    public string? Path { get; set; }
    // rasterized bounds (spec'd 2026-08-25 for vnavmesh's (min,max) return shape; a server
    // that omits them falls back to the request bounds client-side)
    public float[]? Min { get; set; }
    public float[]? Max { get; set; }
}

internal sealed class FindPathResponse : Response
{
    public float[][]? Waypoints { get; set; }
    public bool Partial { get; set; }

    /// <summary>Multi-modal spans of `waypoints` (spec: docs/mnemosyne-protocol.md → legs).
    /// Absent from a server that plans single-mode, and from every server before 2026-08-25 —
    /// absence means "follow the flat list", which is what those answers always meant.</summary>
    public FindPathLegResponse[]? Legs { get; set; }

    // classified answers (spec 2026-08-23, served since 2026-08-24). Result lives on the
    // base Response now, since every ok:false carries one. "ok" | "targetOffMesh" |
    // "startOffMesh" | "noRouteOnMesh" | "meshNotReady" | "unreachable" | "avoidIgnored".
    // The client adds "serviceUnavailable" when nothing answers the pipe - the server
    // cannot report its own absence.
    public float[]? Nearest { get; set; }
}

/// <summary>One span of a multi-modal route: a run of waypoints sharing a movement mode, plus
/// the transition that starts it. `first`/`count` index into the flat `waypoints` array, so a
/// consumer that ignores legs still gets a followable (if mode-naive) path.</summary>
internal sealed class FindPathLegResponse
{
    public string? Mode { get; set; }    // "walk" | "fly"
    public string? Enter { get; set; }   // "mount" | "jumpOff" | "land" | "dismount" | "teleport"
    public ulong? EnterArg { get; set; } // aetheryteId, for enter:"teleport"
    public int First { get; set; }
    public int Count { get; set; }
}
