using Ariadne.Mnemosyne;
using System.IO.Pipes;
using System.Text.Json;

namespace Ariadne.Tests;

// Round-trip tests for MnemosyneClient against an in-process pipe server that speaks the
// protocol the way the stub/Mnemosyne would. Each test uses its own pipe name so a stub
// running on the real "mnemosyne" pipe can't interfere.
public class MnemosyneClientTests
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

    private static object HandleDefault(JsonElement req)
    {
        var id = req.GetProperty("id").GetInt32();
        return req.GetProperty("op").GetString() switch
        {
            "hello" => new { id, ok = true, protocol = 1, app = "test-server", version = "0.0.1", meshVersion = 25 },
            "zoneStatus" => new { id, ok = true, status = "cached", version = 25, customization = 0 },
            "getMesh" => new { id, ok = true, path = @"C:\meshes\test.navmesh", version = 25, customization = 0, size = 12345L },
            var op => new { id, ok = false, error = $"unknown op '{op}'" },
        };
    }

    [Fact]
    public async Task RoundTrip_HelloThenZoneStatusThenGetMesh()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";
        var serverTask = ServeOnce(pipeName, HandleDefault);
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);

        var status = await client.ZoneStatusAsync("some_zone__1F__0__0");
        Assert.NotNull(status);
        Assert.True(status.Ok);
        Assert.Equal("cached", status.Status);
        Assert.Equal(25, status.Version);
        Assert.True(client.IsConnected);
        Assert.Equal("test-server 0.0.1", client.ServerApp);

        var mesh = await client.GetMeshAsync("some_zone__1F__0__0");
        Assert.NotNull(mesh);
        Assert.True(mesh.Ok);
        Assert.Equal(@"C:\meshes\test.navmesh", mesh.Path);
        Assert.Equal(12345L, mesh.Size);

        client.Dispose();
        await serverTask; // server loop ends when the client closes the pipe
    }

    [Fact]
    public async Task NoServer_ReturnsNullWithoutThrowing()
    {
        using var client = new MnemosyneClient(_ => { }, _ => { }, $"ariadne-test-{Guid.NewGuid():N}");
        Assert.Null(await client.ZoneStatusAsync("whatever"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ServerRestart_ClientReconnects()
    {
        var pipeName = $"ariadne-test-{Guid.NewGuid():N}";

        var first = ServeOnce(pipeName, HandleDefault);
        using var client = new MnemosyneClient(_ => { }, _ => { }, pipeName);
        Assert.NotNull(await client.ZoneStatusAsync("zone_a"));

        // simulate a server restart: drop the connection client-side, wait for the first
        // server to fully exit (single-instance pipes can't overlap), then serve again
        client.DropForTest();
        await first;
        var second = ServeOnce(pipeName, HandleDefault);

        Assert.NotNull(await client.ZoneStatusAsync("zone_b")); // immediate reconnect (backoff only arms on failed attempts)
        Assert.True(client.IsConnected);

        client.Dispose();
        await second;
    }
}
