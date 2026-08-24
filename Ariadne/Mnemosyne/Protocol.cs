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
}

internal class Response
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
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
}

internal sealed class GetMeshResponse : Response
{
    public string? Path { get; set; }
    public int Version { get; set; }
    public int Customization { get; set; }
    public long Size { get; set; }
}

internal sealed class FindPathResponse : Response
{
    public float[][]? Waypoints { get; set; }
    public bool Partial { get; set; }

    // classified answers (spec 2026-08-23); null from legacy servers
    public string? Result { get; set; } // "ok" | "targetOffMesh" | "noRouteOnMesh" | "meshNotReady" | "unreachable"
    public float[]? Nearest { get; set; }
}
