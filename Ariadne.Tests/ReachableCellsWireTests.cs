using Ariadne.Mnemosyne;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ariadne.Tests;

// reachableCells is a spec'd-but-not-yet-served op (docs/mnemosyne-protocol.md), so the wire
// shape is verified against a server that answers it the way Mnemosyne will: camelCase fields,
// the nested stats object, and an absent height band when no band was asked for. The chain the
// gate runs — wire → DTO → validation → the byte[] the consumer tuple carries — is exercised
// end to end on the answer rather than on the DTO by eye.
public class ReachableCellsWireTests
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

    private static object Grid(JsonElement req)
    {
        var id = req.GetProperty("id").GetInt32();
        return req.GetProperty("op").GetString() switch
        {
            "hello" => new { id, ok = true, protocol = 1, app = "test-server", version = "0.0.1", meshVersion = 25 },
            "reachableCells" => new
            {
                id,
                ok = true,
                result = "ok",
                start = new[] { 1f, 12.4f, 3f },
                origin = new[] { -8f, -4f },
                cellSize = 2f,
                width = 3,
                depth = 2,
                columns = new[] { 0, 4, 5 },
                heights = new[] { 12.4f, 12.4f, 20f },
                states = new[] { 1, 2, 1 },
                reachableOutside = false,
                stats = new { reachablePolys = 12, walkablePolys = 15 },
            },
            var op => new { id, ok = false, error = $"unknown op '{op}'" },
        };
    }

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
    public async Task ReachableCells_BindsThroughTheRealClient()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, req =>
        {
            lock (seen)
                seen.Add(req.GetRawText());
            return Grid(req);
        });
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.ReachableCellsAsync("some_zone__1F__0__0", [1, 12.4f, 3], 120, 2, float.NaN, float.NaN);

        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Equal("ok", resp.Result);
        Assert.Equal(2f, resp.CellSize);
        Assert.Equal(3, resp.Width);
        Assert.Equal(2, resp.Depth);
        Assert.Equal(new[] { 0, 4, 5 }, resp.Columns);
        Assert.Equal(20f, resp.Heights![2]);
        Assert.False(resp.ReachableOutside);
        Assert.Equal(12, resp.Stats!.ReachablePolys);
        Assert.Equal(15, resp.Stats.WalkablePolys);
        Assert.Equal(1f, resp.Start![0]);
        Assert.Equal(-8f, resp.Origin![0]);

        // the grid the gate would hand a consumer: validated, then states as bytes
        Assert.True(ReachableGrid.TryValidate(resp.Columns!, resp.Heights!, resp.States!, resp.Width, resp.Depth, out _));
        Assert.Equal(new byte[] { 1, 2, 1 }, ReachableGrid.ToStates(resp.States!));

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task NoHeightBand_IsOmittedFromTheRequest()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var seen = new List<string>();
        var serverTask = ServeOnce(pipeName, req =>
        {
            lock (seen)
                seen.Add(req.GetRawText());
            return Grid(req);
        });
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        // NaN = "no band". It must not reach the wire as NaN: the serializer refuses to write
        // one at all, so a request carrying it would fail rather than degrade.
        Assert.NotNull(await client.ReachableCellsAsync("zone", [0, 0, 0], 120, 2, float.NaN, float.NaN));
        var unbounded = LastRequest(seen, "reachableCells");
        Assert.False(unbounded.TryGetProperty("minY", out _));
        Assert.False(unbounded.TryGetProperty("maxY", out _));

        // a real band is sent as given
        Assert.NotNull(await client.ReachableCellsAsync("zone", [0, 0, 0], 120, 2, 10, 40));
        var bounded = LastRequest(seen, "reachableCells");
        Assert.Equal(10f, bounded.GetProperty("minY").GetSingle());
        Assert.Equal(40f, bounded.GetProperty("maxY").GetSingle());
        Assert.Equal(120f, bounded.GetProperty("radius").GetSingle());
        Assert.Equal(2f, bounded.GetProperty("cellSize").GetSingle());

        client.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task UnknownOp_ComesBackNotOk()
    {
        // what an older Mnemosyne does with the op: the broker turns this into `failed`, and
        // the test pins the server side of that contract
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var serverTask = ServeOnce(pipeName, req => req.GetProperty("op").GetString() == "hello"
            ? Grid(req)
            : new { id = req.GetProperty("id").GetInt32(), ok = false, error = "unknown op 'reachableCells'" });
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.ReachableCellsAsync("zone", [0, 0, 0], 120, 2, float.NaN, float.NaN);

        Assert.NotNull(resp);
        Assert.False(resp.Ok);
        Assert.Contains("unknown op", resp.Error);

        client.Dispose();
        await serverTask;
    }
}
