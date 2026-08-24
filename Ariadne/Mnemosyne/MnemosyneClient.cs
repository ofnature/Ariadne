using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ariadne.Mnemosyne;

// Named-pipe JSON client for Mnemosyne (spec: docs/mnemosyne-protocol.md). One request in
// flight at a time — traffic is a couple of ops per zone change, so a semaphore beats
// correlation bookkeeping. All ops degrade to null when Mnemosyne is unreachable; callers
// treat null as "unavailable", never as an error the user must see.
//
// Log delegates are injected instead of using Service so tests can run this against an
// in-process pipe server without loading Dalamud.
internal sealed class MnemosyneClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public bool IsConnected => _pipe?.IsConnected == true;
    public string? ServerApp { get; private set; }

    private readonly string _pipeName;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarning;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private DateTime _nextConnectAttempt = DateTime.MinValue;
    private TimeSpan _backoff = InitialBackoff;

    public MnemosyneClient(Action<string> logInfo, Action<string> logWarning, string pipeName = Protocol.PipeName)
    {
        _pipeName = pipeName;
        _logInfo = logInfo;
        _logWarning = logWarning;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _disposeCts.Cancel();
        Drop();
        _disposeCts.Dispose();
    }

    public Task<ZoneStatusResponse?> ZoneStatusAsync(string cacheKey, CancellationToken cancel = default)
        => SendAsync<ZoneStatusResponse>(new Request { Op = "zoneStatus", CacheKey = cacheKey }, cancel);

    public Task<GetMeshResponse?> GetMeshAsync(string cacheKey, CancellationToken cancel = default)
        => SendAsync<GetMeshResponse>(new Request { Op = "getMesh", CacheKey = cacheKey }, cancel);

    public Task<FindPathResponse?> FindPathAsync(string cacheKey, float[] from, float[] to, bool fly, CancellationToken cancel = default)
        => SendAsync<FindPathResponse>(new Request { Op = "findPath", CacheKey = cacheKey, From = from, To = to, Fly = fly }, cancel);

    public Task<Response?> NotifyMeshBuiltAsync(string cacheKey, string path, CancellationToken cancel = default)
        => SendAsync<Response>(new Request { Op = "notifyMeshBuilt", CacheKey = cacheKey, Path = path }, cancel);

    /// <summary>Ship a live scene capture for an out-of-process build of the exact zone
    /// variant. Server must ack immediately (build runs async; poll zoneStatus).</summary>
    public Task<Response?> BuildZoneAsync(Zone.SceneCaptureDto scene, CancellationToken cancel = default)
        => SendAsync<Response>(new Request { Op = "buildZone", CacheKey = scene.CacheKey, Scene = scene }, cancel);

    /// <summary>Feedback into the mesh-learning channel: a traversal that succeeded where
    /// the mesh said no ("direct"), or a planned leg that failed. Fire-and-forget.</summary>
    public Task<Response?> ReportTraversalAsync(string cacheKey, float[] from, float[] to, string mode, bool success, CancellationToken cancel = default)
        => SendAsync<Response>(new Request { Op = "reportTraversal", CacheKey = cacheKey, From = from, To = to, Mode = mode, Success = success }, cancel);

    /// <summary>Best-effort ~10 Hz player-position push for Mnemosyne's viewer; dropped
    /// samples are harmless (server keeps only the latest).</summary>
    public Task<Response?> UpdateGameStateAsync(string cacheKey, uint territoryId, float[] pos, float rotation, bool flying, CancellationToken cancel = default)
        => SendAsync<Response>(new Request
        {
            Op = "updateGameState",
            CacheKey = cacheKey,
            TerritoryId = territoryId,
            Pos = pos,
            Rotation = rotation,
            Flying = flying,
        }, cancel);

    private async Task<TResp?> SendAsync<TResp>(Request request, CancellationToken cancel) where TResp : Response
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancel);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync(linked.Token).ConfigureAwait(false))
                return null;
            request.Id = ++_nextId;
            return await ExchangeAsync<TResp>(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A request timeout usually means the server went away but the pipe handle is a
            // zombie (IsConnected stays true until an I/O op fails, and writes can buffer
            // silently) - without dropping, every later request hangs for the full timeout
            // and the client never reconnects. Drop is idempotent, so the dispose path is fine.
            Drop();
            return null;
        }
        catch (Exception ex)
        {
            _logWarning($"[Mnemosyne] Request '{request.Op}' failed, dropping connection: {ex.Message}");
            Drop();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private async Task<TResp?> ExchangeAsync<TResp>(Request request, CancellationToken cancel) where TResp : Response
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);

        await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.JsonOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
        await _writer.FlushAsync(timeout.Token).ConfigureAwait(false);

        while (true)
        {
            var line = await _reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line == null)
                throw new IOException("pipe closed by server");
            var resp = JsonSerializer.Deserialize<TResp>(line, Protocol.JsonOptions);
            if (resp != null && resp.Id == request.Id)
                return resp;
            // stale answer to a request that timed out earlier — skip and keep reading
        }
    }

    // Caller holds _gate.
    private async Task<bool> EnsureConnectedAsync(CancellationToken cancel)
    {
        if (IsConnected)
            return true;
        Drop();

        if (DateTime.UtcNow < _nextConnectAttempt)
            return false;

        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            _pipe = pipe;
            _reader = new StreamReader(pipe, leaveOpen: true);
            _writer = new StreamWriter(pipe, leaveOpen: true) { NewLine = "\n" };

            var hello = await ExchangeAsync<HelloResponse>(new Request { Id = ++_nextId, Op = "hello" }, cancel).ConfigureAwait(false);
            if (hello is not { Ok: true } || hello.Protocol != Protocol.Version)
            {
                _logWarning($"[Mnemosyne] Incompatible server (protocol {hello?.Protocol.ToString() ?? "none"}, expected {Protocol.Version})");
                Drop();
                _nextConnectAttempt = DateTime.UtcNow + MaxBackoff; // wrong server won't fix itself quickly
                return false;
            }

            ServerApp = $"{hello.App} {hello.Version}";
            _backoff = InitialBackoff;
            _nextConnectAttempt = DateTime.MinValue;
            _logInfo($"[Mnemosyne] Connected to {ServerApp} (protocol {hello.Protocol}, mesh v{hello.MeshVersion})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Drop();
            _nextConnectAttempt = DateTime.UtcNow + _backoff;
            _logInfo($"[Mnemosyne] Connect failed ({ex.GetType().Name}), next attempt in {_backoff.TotalSeconds:0}s");
            _backoff = _backoff + _backoff <= MaxBackoff ? _backoff + _backoff : MaxBackoff;
            return false;
        }
    }

    // Simulates connection loss (e.g. server restart) without waiting for an IO error.
    internal void DropForTest() => Drop();

    private void Drop()
    {
        // Every disposal here can itself throw on a broken pipe — StreamWriter.Dispose
        // flushes, and flushing a dead pipe raises IOException. Drop runs precisely when
        // the connection is broken, so swallowing these is correct, and letting them out
        // was a real bug: it faulted the pusher's fire-and-forget task at 10 Hz and made
        // plugin Dispose fail ("unload error") when the client was torn down mid-outage.
        ServerApp = null;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }
}
