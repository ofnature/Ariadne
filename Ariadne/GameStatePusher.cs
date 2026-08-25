using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Ariadne;

// Character is the fleet discriminator: one Mnemosyne service serves every game client
// on the PC, so a sample has to say which toon it describes. It is "Name@World" rather
// than a content id because this Dalamud API version exposes no local content id, and
// name alone is not unique across worlds.
internal sealed record GameStateSample(string CacheKey, uint TerritoryId, Vector3 Pos, float Rotation, bool Flying,
    string Character);

// Feeds the player's live position to Mnemosyne's viewer (~10 Hz, per the updateGameState
// spec in docs/mnemosyne-protocol.md). Tick() runs on the framework thread — the sample
// delegate reads game state there — and the actual send happens off-thread, one at a time:
// if a push is still in flight the tick is skipped, never queued (the server only keeps
// the latest sample anyway).
internal sealed class GameStatePusher
{
    private const long IntervalMs = 100;

    private readonly Func<GameStateSample?> _sample; // null = no player / zone not ready
    private readonly Func<GameStateSample, Task> _send;
    private readonly Func<long> _nowMs;

    private long _nextPushAt;
    private int _inFlight;

    public long LastPushedAtMs { get; private set; } = -1;

    public GameStatePusher(Func<GameStateSample?> sample, Func<GameStateSample, Task> send, Func<long>? nowMs = null)
    {
        _sample = sample;
        _send = send;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Pushing recently enough that the viewer would show a live marker.</summary>
    public bool IsActive => LastPushedAtMs >= 0 && _nowMs() - LastPushedAtMs < 5000;

    public void Tick()
    {
        var now = _nowMs();
        if (now < _nextPushAt)
            return;
        if (_sample() is not { } sample)
            return;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        _nextPushAt = now + IntervalMs;
        LastPushedAtMs = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await _send(sample).ConfigureAwait(false);
            }
            catch
            {
                // best-effort push: the client already logs/degrades, and a fault here
                // would otherwise surface as an unobserved task exception every 100 ms
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
            }
        });
    }
}
