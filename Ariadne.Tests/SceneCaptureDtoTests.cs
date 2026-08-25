using Ariadne.Mnemosyne;
using Ariadne.Zone;
using System.Text.Json;

namespace Ariadne.Tests;

// The scene capture crosses the pipe as one JSON line — this locks the wire shape both
// sessions build against (camelCase, ulong keys as numbers, transforms as arrays).
public class SceneCaptureDtoTests
{
    private static SceneCaptureDto Sample() => new()
    {
        CacheKey = "ffxiv_sea_s1_twn_s1t2_level_s1t2__11F3A__27__0",
        TerritoryId = 129,
        CfcId = 0,
        FestivalLayers = [0x27u],
        ZoneSGs = [1, 6, 0, 1, 0],
        Terrains = ["bg/ffxiv/sea_s1/twn/s1t2/collision"],
        // type 2 = cylinder: the field the extractor switches on to decide what to rasterize
        AnalyticShapes = [new() { Crc = 0xDEAD, Transform = T(2), BbMin = [-1, -2, -3], BbMax = [1, 2, 3] }],
        MeshPaths = [new() { Crc = 0xBEEF, Path = "bg/ffxiv/sea_s1/twn/s1t2/collision/tr0001.pcb" }],
        BgParts = [new() { Key = 0xFFFF_FFFF_FFFF_FFFEul, Transform = T(), Crc = 0xBEEF, MatId = 5, MatMask = ulong.MaxValue, Analytic = false }],
        Colliders = [new() { Key = 42, Transform = T(), Crc = 0xBEEF, MatId = 0, MatMask = 0, Type = 2 }],
        ExitRanges = [new() { Key = 7, Transform = T() }],
    };

    private static TransformDto T(int type = 0) =>
        new() { T = [1.5f, -2.5f, 3.5f], R = [0, 0, 0, 1], S = [1, 1, 1], Type = type };

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var json = JsonSerializer.Serialize(Sample(), Protocol.JsonOptions);
        var back = JsonSerializer.Deserialize<SceneCaptureDto>(json, Protocol.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal("ffxiv_sea_s1_twn_s1t2_level_s1t2__11F3A__27__0", back.CacheKey);
        Assert.Equal(129u, back.TerritoryId);
        Assert.Equal(new uint[] { 0x27u }, back.FestivalLayers);
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFEul, back.BgParts[0].Key); // ulong survives as JSON number (C# both ends)
        Assert.Equal(ulong.MaxValue, back.BgParts[0].MatMask);
        Assert.Equal(new float[] { 1.5f, -2.5f, 3.5f }, back.Colliders[0].Transform.T);
        Assert.Equal(2, back.Colliders[0].Type);
        // Transform.Type rides along on analytic shapes. It was missing from the wire until
        // 2026-08-24, which silently rasterized every sphere and cylinder as a box.
        Assert.Equal(2, back.AnalyticShapes[0].Transform.Type);
        Assert.Equal(2, back.InstanceCount);
    }

    [Fact]
    public void Wire_IsCamelCaseAndSingleLine()
    {
        var json = JsonSerializer.Serialize(Sample(), Protocol.JsonOptions);
        Assert.Contains("\"cacheKey\"", json);
        Assert.Contains("\"bgParts\"", json);
        Assert.Contains("\"bbMin\"", json);
        Assert.DoesNotContain('\n', json); // newline-delimited protocol: one capture, one line
    }

    [Fact]
    public void BuildZoneRequest_EmbedsScene()
    {
        var req = new Request { Id = 9, Op = "buildZone", CacheKey = Sample().CacheKey, Scene = Sample() };
        var json = JsonSerializer.Serialize(req, Protocol.JsonOptions);
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("buildZone", doc.GetProperty("op").GetString());
        Assert.Equal(129u, doc.GetProperty("scene").GetProperty("territoryId").GetUInt32());
        Assert.False(doc.TryGetProperty("from", out _)); // null fields stay off the wire
    }
}
