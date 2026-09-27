using Ariadne.Mnemosyne;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ariadne.Tests;

// The bitmap op's answer shape. It is the one gate whose client-side fallback used to be a lie:
// vnavmesh's BuildBitmap* return the rasterized (min,max) synchronously, ours is a server-side
// rasterization behind a bounded wait, and every way of not answering — the wait running out, a
// refusal, a success that reported no bounds on an unbounded request — used to come back as
// (0,0,0)-(0,0,0). That is a plausible spot in most zones, cannot be told apart from a real
// degenerate bitmap, and describes a file nobody wrote.
//
// These tests pin both halves: the fields the server sends and the request carries (on the wire,
// through the real client), and the sentinel the client answers with when there is nothing to
// report. The broker's rule — request bounds when it has them, NaN otherwise — is one line inside a
// Dalamud-backed type, so what is asserted here is the wire and the sentinel it picks.
public class BuildBitmapWireTests
{
    private static async Task ServeOnce(string pipeName, Func<JsonElement, object> handler)
    {
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        try
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            await using var writer = new StreamWriter(server, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line)
            {
                var req = JsonDocument.Parse(line).RootElement;
                await writer.WriteLineAsync(JsonSerializer.Serialize(handler(req)));
            }
        }
        catch (IOException)
        {
            // client dropped the pipe mid-read — how these tests end a connection
        }
    }

    /// <summary>Hello, then the bitmap op as the server variant asks for it. Records every request.</summary>
    private static Func<JsonElement, object> Server(List<string> seen, bool withBounds = true, bool ok = true)
        => req =>
        {
            lock (seen)
                seen.Add(req.GetRawText());
            var id = req.GetProperty("id").GetInt32();
            return req.GetProperty("op").GetString() switch
            {
                "hello" => new { id, ok = true, protocol = 1, app = "test-server", version = "0.0.1", meshVersion = 25 },
                "buildBitmap" when !ok => new { id, ok = false, error = "the server cannot rasterize here" },
                "buildBitmap" when withBounds => new
                {
                    id, ok = true, path = @"C:\bitmaps\zone__0.bmp",
                    min = new[] { -10f, 0f, -20f },
                    max = new[] { 30f, 0f, 40f },
                },
                "buildBitmap" => new { id, ok = true, path = @"C:\bitmaps\zone__0.bmp" }, // an older server: no bounds
                var op => new { id, ok = false, error = $"unknown op '{op}'" },
            };
        };

    private static JsonElement LastRequest(List<string> seen, string op)
    {
        lock (seen)
        {
            for (var i = seen.Count - 1; i >= 0; i--)
            {
                var req = JsonDocument.Parse(seen[i]).RootElement;
                if (req.GetProperty("op").GetString() == op)
                    return req;
            }
        }
        throw new InvalidOperationException($"no '{op}' request was seen");
    }

    [Fact]
    public async Task BuildBitmap_BindsThroughTheRealClient()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, Server(seen));
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.BuildBitmapAsync("some_zone__1F__0__0", [[1f, 2f, 3f], [4f, 5f, 6f]],
            "zone__0.bmp", 0.5f, [-10f, 0f, -20f], [30f, 0f, 40f]);

        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Equal(@"C:\bitmaps\zone__0.bmp", resp.Path);
        Assert.Equal(-10f, resp.Min![0]);
        Assert.Equal(40f, resp.Max![2]);

        var request = LastRequest(seen, "buildBitmap");
        Assert.Equal(2, request.GetProperty("startingPoints").GetArrayLength());
        Assert.Equal(5f, request.GetProperty("startingPoints")[1][1].GetSingle());
        Assert.Equal("zone__0.bmp", request.GetProperty("filename").GetString());
        Assert.Equal(0.5f, request.GetProperty("pixelSize").GetSingle());
        Assert.Equal("some_zone__1F__0__0", request.GetProperty("cacheKey").GetString());

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task NoRequestBounds_AreOmittedFromTheRequest()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, Server(seen));
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        Assert.NotNull(await client.BuildBitmapAsync("zone", [[0f, 0f, 0f]], "unbounded.bmp", 1f, null, null));
        var unbounded = LastRequest(seen, "buildBitmap");
        Assert.False(unbounded.TryGetProperty("minBounds", out _));
        Assert.False(unbounded.TryGetProperty("maxBounds", out _));

        Assert.NotNull(await client.BuildBitmapAsync("zone", [[0f, 0f, 0f]], "bounded.bmp", 1f, [-5f, 0f, -5f], [5f, 0f, 5f]));
        var bounded = LastRequest(seen, "buildBitmap");
        Assert.Equal(-5f, bounded.GetProperty("minBounds")[0].GetSingle());
        Assert.Equal(5f, bounded.GetProperty("maxBounds")[2].GetSingle());

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task ServerWithoutBounds_ReportsNone_RatherThanZeros()
    {
        // an older server answers `{ ok, path }` alone: the client must report "no bounds", not
        // invent a region — the broker turns a missing answer into the request bounds (which the
        // caller supplied) or BitmapBounds.NoAnswer, never into (0,0,0)
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, Server(seen, withBounds: false));
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.BuildBitmapAsync("zone", [[0f, 0f, 0f]], "zone.bmp", 1f, null, null);

        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Path);
        Assert.Null(resp.Min);
        Assert.Null(resp.Max);

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task RefusedBitmap_IsNotOkAndCarriesNoBounds()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, Server(seen, ok: false));
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.BuildBitmapAsync("zone", [[0f, 0f, 0f]], "zone.bmp", 1f, null, null);

        Assert.NotNull(resp);
        Assert.False(resp.Ok);
        Assert.Contains("cannot rasterize", resp.Error);
        Assert.Null(resp.Path);
        Assert.Null(resp.Min);
        Assert.Null(resp.Max);

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public void NoAnswer_IsNaN_AndIsNotTheOldZeroFallback()
    {
        var none = BitmapBounds.NoAnswer;

        // NaN on every component: no arithmetic on it can quietly produce a region
        Assert.True(float.IsNaN(none.Min.X) && float.IsNaN(none.Min.Y) && float.IsNaN(none.Min.Z));
        Assert.True(float.IsNaN(none.Max.X) && float.IsNaN(none.Max.Y) && float.IsNaN(none.Max.Z));
        Assert.False(BitmapBounds.HasAnswer(none));

        // the regression this exists to stop: (0,0,0)-(0,0,0) as "no answer"
        Assert.NotEqual(default, none);

        Assert.True(BitmapBounds.HasAnswer((new Vector3(-10, 0, -20), new Vector3(30, 0, 40))));
        Assert.True(BitmapBounds.HasAnswer((default, default))); // zero bounds are a real (if useless) answer
    }
}
