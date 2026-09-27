using System;

namespace Ariadne;

/// <summary>
/// How hard the broker asks again after a negative zone answer, and how long it waits between
/// attempts. Extracted from <see cref="MeshBroker"/>'s query loop so the contention policy can be
/// tested without the game — the loop it came out of was the one piece of the broker whose
/// behaviour nobody could check.
///
/// <para>Zone entry is peak contention on the mesh file: vnavmesh kicking its build, Mnemosyne's
/// viewer auto-loading the same zone off the player push. An exclusive hold anywhere in that chain
/// makes the server honestly answer "missing" (proven by locking the file and probing), and a
/// viewer load can outlast a fixed retry window — which is why a negative answer is retried at
/// all. The two cases differ:</para>
///
/// <list type="bullet">
/// <item>Nobody is building: a couple of quick retries cover the contention window and then the
/// answer stands. Asking forever would leave a dead zone looking like it is still loading.</item>
/// <item>vnavmesh is building: a seed stays profitable for the whole build, because the
/// <c>Nav.Reload</c> nudge converts a late seed into a cache load — so keep asking until the build
/// is over, up to a ceiling that stops an unlucky zone from polling all session.</item>
/// </list>
///
/// Pure and clock-free: the broker owns the waiting, this owns the decision.
/// </summary>
internal sealed class MeshRetryPolicy(int maxAttempts = MeshRetryPolicy.DefaultMaxAttempts,
    int attemptsWhileIdle = MeshRetryPolicy.DefaultAttemptsWhileIdle,
    int maxDelaySeconds = MeshRetryPolicy.DefaultMaxDelaySeconds)
{
    public const int DefaultMaxAttempts = 90;
    public const int DefaultAttemptsWhileIdle = 3;
    public const int DefaultMaxDelaySeconds = 2;

    public int MaxAttempts { get; } = maxAttempts;
    public int AttemptsWhileIdle { get; } = attemptsWhileIdle;
    public int MaxDelaySeconds { get; } = maxDelaySeconds;

    /// <summary>Polls made so far, including the one the last <see cref="Record"/> answered. This is
    /// the "attempt N" the activity log reports.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// Record an answer and say whether to ask again. False ends the loop: either the answer is
    /// worth acting on, or the budget is spent, and the caller reports what it actually has.
    /// </summary>
    /// <param name="status">What the last poll concluded.</param>
    /// <param name="buildRunning">vnavmesh has a build in flight for this zone, so a seed can still
    /// save the build it is watching.</param>
    public bool Record(ZoneMeshStatus status, bool buildRunning)
    {
        ++Attempts;

        // Not a negative answer: a current mesh, or one to seed, is the thing we were asking for.
        if (status is not (ZoneMeshStatus.Missing or ZoneMeshStatus.MnemosyneUnavailable))
            return false;
        if (Attempts >= MaxAttempts)
            return false;

        return buildRunning || Attempts < AttemptsWhileIdle;
    }

    /// <summary>How long to wait before the retry <see cref="Record"/> just asked for.</summary>
    public TimeSpan Delay => TimeSpan.FromSeconds(Math.Min(Attempts, MaxDelaySeconds));
}
