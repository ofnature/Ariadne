using System;

namespace Ariadne.Movement;

/// <summary>Patience for the service's "volume still loading" answer.
///
/// `meshNotReady` is not a failure: MeshBroker's contract says the service replies with it while
/// it decodes a zone's flight volume off the request (measured ~1.9 s for a field zone), and that
/// it "tells a consumer to wait and retry". This is that consumer — without it a fly request sent
/// right after a zone change reads as "no path", indistinguishable from an unreachable
/// destination.
///
/// Pure and clock-injected so the policy is testable without the game: the caller ticks it from
/// the framework thread and passes `now`.</summary>
internal sealed class MeshWait(int retryDelayMs = MeshWait.DefaultRetryDelayMs, int maxRetries = MeshWait.DefaultMaxRetries)
{
    public const int DefaultRetryDelayMs = 250;
    public const int DefaultMaxRetries = 20; // 5 s of patience: a field zone's volume decodes in ~2 s

    public int RetryDelayMs { get; } = retryDelayMs;
    public int MaxRetries { get; } = maxRetries;

    /// <summary>Retries used since the last reset — the budget spans one whole wait, not one try.</summary>
    public int Retries { get; private set; }

    public DateTime NextRetryAt { get; private set; } = DateTime.MinValue;

    /// <summary>A wait is pending: the move is still in flight even though no pathfind is.</summary>
    public bool Waiting => NextRetryAt != DateTime.MinValue;

    /// <summary>Record a pathfind answer. True means keep waiting for the mesh (the goal stays
    /// alive) rather than treating an empty answer as a failure. Anything that is not
    /// `meshNotReady` — including the budget running out — resets the wait and returns false, so
    /// the caller reports whatever it actually got.</summary>
    public bool Record(string result, DateTime now)
    {
        if (result != "meshNotReady" || Retries >= MaxRetries)
        {
            Reset();
            return false;
        }

        ++Retries;
        NextRetryAt = now.AddMilliseconds(RetryDelayMs);
        return true;
    }

    /// <summary>The delay has elapsed: re-request now (and clear the timer; Retries stays, so the
    /// budget covers the whole wait).</summary>
    public bool Due(DateTime now) => Waiting && now >= NextRetryAt;

    public void ClearTimer() => NextRetryAt = DateTime.MinValue;

    public void Reset()
    {
        Retries = 0;
        NextRetryAt = DateTime.MinValue;
    }
}
