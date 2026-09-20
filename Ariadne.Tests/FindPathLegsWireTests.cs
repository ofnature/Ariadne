using Ariadne.Mnemosyne;
using Ariadne.Movement;
using System;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ariadne.Tests;

// The leg shape is what Mnemosyne will actually send (docs/mnemosyne-protocol.md → legs), so
// bind it through the real client against a real pipe rather than trusting the DTO by eye:
// camelCase field names, the optional enter/enterArg, and legs indexing the waypoint array.
public class FindPathLegsWireTests
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

    // The server's first multi-modal case: a fly route the volume could not finish, landing
    // and walking the remainder.
    private static object WalkedTail(JsonElement req)
    {
        var id = req.GetProperty("id").GetInt32();
        return req.GetProperty("op").GetString() switch
        {
            "hello" => new { id, ok = true, protocol = 1, app = "test-server", version = "0.0.1", meshVersion = 25 },
            "findPath" => new
            {
                id,
                ok = true,
                result = "walkedTail",
                waypoints = new[]
                {
                    new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 10f }, new[] { 0f, 0f, 20f }, new[] { 1f, 0f, 30f },
                },
                legs = new object[]
                {
                    new { mode = "fly", first = 0, count = 3 },
                    new { mode = "walk", enter = "land", first = 3, count = 1 },
                },
            },
            var op => new { id, ok = false, error = $"unknown op '{op}'" },
        };
    }

    [Fact]
    public async Task FindPath_Legs_BindThroughTheRealClient()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var serverTask = ServeOnce(pipeName, WalkedTail);
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var resp = await client.FindPathAsync("some_zone__1F__0__0", [0, 0, 0], [1, 0, 30], true);

        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Equal("walkedTail", resp.Result);
        Assert.NotNull(resp.Legs);
        Assert.Equal(2, resp.Legs.Length);
        Assert.Equal("fly", resp.Legs[0].Mode);
        Assert.Equal(0, resp.Legs[0].First);
        Assert.Null(resp.Legs[0].Enter);
        Assert.Equal("walk", resp.Legs[1].Mode);
        Assert.Equal("land", resp.Legs[1].Enter);
        Assert.Equal(3, resp.Legs[1].First);

        // and the wire legs become legs the follower can execute: fly, then walk on landing
        var legs = PathLegs.Parse(resp.Legs, resp.Waypoints!.Length);
        Assert.Equal(2, legs.Count);
        Assert.Equal(LegMode.Fly, legs[0].Mode);
        Assert.Equal(LegMode.Walk, legs[1].Mode);
        Assert.Equal(LegTransition.Land, legs[1].Enter);
        Assert.Equal(0, PathLegs.IndexAt(legs, 0));
        Assert.Equal(1, PathLegs.IndexAt(legs, 3));

        client.Dispose();
        await serverTask;
    }
}
