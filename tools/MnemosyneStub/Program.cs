using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

// Stand-in for Mnemosyne's pipe server (spec: docs/mnemosyne-protocol.md) so Ariadne can be
// developed before Mnemosyne.Service exists. Serves straight from a directory of vnavmesh
// .navmesh files — by default the live meshcache, or pass a directory as the first argument.
// findPath is intentionally unimplemented; that requires Mnemosyne's real query engine.

const string PipeName = "mnemosyne";
const int ProtocolVersion = 1;
const int MeshVersion = 25;
const uint MeshMagic = 0x444D564E; // "NVMD"

var meshDir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "pluginConfigs", "vnavmesh", "meshcache");

if (!Directory.Exists(meshDir))
{
    Console.Error.WriteLine($"mesh directory not found: {meshDir}");
    return 1;
}

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

Console.WriteLine($"mnemosyne-stub serving '{meshDir}' on \\\\.\\pipe\\{PipeName}");

var gameStateLock = new object();
JsonElement? gameState = null;
var gameStateAt = DateTime.MinValue;

while (true)
{
    var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await server.WaitForConnectionAsync();
    _ = Task.Run(() => HandleClient(server));
}

async Task HandleClient(NamedPipeServerStream pipe)
{
    var id = Environment.CurrentManagedThreadId;
    Console.WriteLine($"[{id}] client connected");
    try
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            object response;
            try
            {
                response = Handle(JsonDocument.Parse(line).RootElement);
            }
            catch (JsonException)
            {
                Console.WriteLine($"[{id}] malformed line, dropping client");
                break; // per spec: malformed JSON drops the connection
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, json));
        }
    }
    catch (IOException)
    {
        // client went away mid-read/write — normal
    }
    finally
    {
        pipe.Dispose();
        Console.WriteLine($"[{id}] client disconnected");
    }
}

object Handle(JsonElement req)
{
    var id = req.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
    var op = req.TryGetProperty("op", out var opProp) ? opProp.GetString() : null;
    Console.WriteLine($"  -> {op} ({id})");
    switch (op)
    {
        case "hello":
            return new { id, ok = true, protocol = ProtocolVersion, app = "mnemosyne-stub", version = "0.1.0", meshVersion = MeshVersion };

        case "listZones":
        {
            var zones = Directory.EnumerateFiles(meshDir, "*.navmesh").Select(f =>
            {
                var (version, customization) = ReadHeader(f);
                var info = new FileInfo(f);
                return new
                {
                    cacheKey = Path.GetFileNameWithoutExtension(f),
                    version,
                    customization,
                    size = info.Length,
                    mtime = info.LastWriteTimeUtc.ToString("O"),
                };
            }).ToList();
            return new { id, ok = true, zones };
        }

        case "zoneStatus":
        case "getMesh":
        {
            if (req.TryGetProperty("cacheKey", out var keyProp) && keyProp.GetString() is { Length: > 0 } cacheKey)
            {
                // cache keys are filename stems built from game data, but never trust them as paths
                if (cacheKey.IndexOfAny(['/', '\\', ':']) >= 0 || cacheKey.Contains(".."))
                    return new { id, ok = false, error = "invalid cacheKey" };

                var path = Path.Combine(meshDir, cacheKey + ".navmesh");
                if (!File.Exists(path))
                    return op == "zoneStatus"
                        ? new { id, ok = true, status = "missing" }
                        : new { id, ok = false, error = "missing" };

                var (version, customization) = ReadHeader(path);
                var stale = version != MeshVersion;
                if (op == "zoneStatus")
                    return new { id, ok = true, status = stale ? "stale" : "cached", version, customization };
                return stale
                    ? new { id, ok = false, error = $"stale (v{version})" }
                    : new { id, ok = true, path, version, customization, size = new FileInfo(path).Length };
            }
            return new { id, ok = false, error = "cacheKey required" };
        }

        case "findPath":
            return new { id, ok = false, error = "findPath not implemented in stub — requires Mnemosyne's query engine" };

        case "notifyMeshBuilt":
            Console.WriteLine($"  notifyMeshBuilt: {req}");
            return new { id, ok = true };

        case "updateGameState":
            lock (gameStateLock)
            {
                gameState = req.Clone();
                gameStateAt = DateTime.UtcNow;
            }
            return new { id, ok = true };

        case "getGameState":
            lock (gameStateLock)
            {
                var age = (DateTime.UtcNow - gameStateAt).TotalMilliseconds;
                if (gameState is not { } gs || age > 5000)
                    return new { id, ok = true, present = false };
                return new
                {
                    id,
                    ok = true,
                    present = true,
                    cacheKey = gs.GetProperty("cacheKey").GetString(),
                    territoryId = gs.TryGetProperty("territoryId", out var t) ? t.GetUInt32() : 0,
                    pos = gs.TryGetProperty("pos", out var pos) ? JsonSerializer.Deserialize<float[]>(pos) : null,
                    rotation = gs.TryGetProperty("rotation", out var r) ? r.GetSingle() : 0f,
                    flying = gs.TryGetProperty("flying", out var f) && f.GetBoolean(),
                    ageMs = (int)age,
                };
            }

        default:
            return new { id, ok = false, error = $"unknown op '{op}'" };
    }
}

(int version, int customization) ReadHeader(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != MeshMagic)
            return (0, 0);
        return ((int)reader.ReadUInt32(), reader.ReadInt32());
    }
    catch (Exception)
    {
        return (0, 0);
    }
}
