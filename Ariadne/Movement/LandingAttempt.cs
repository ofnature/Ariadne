using System;

namespace Ariadne.Movement;

// A `land` leg transition — a fly route the volume could not finish, ending in a walk leg —
// means: be on the ground before walking. The character lands by itself once it descends onto
// terrain, so this is a gate, not a controller: hold the walk leg while the game still reports
// flight, and drive at the walk leg's first waypoint meanwhile. The wait is budgeted because
// an unlandable spot (nothing under the waypoints, flight the game will not end) must not
// freeze the path forever — after the budget the leg is followed on foot anyway, and the log
// says so.
//
// Mirrors TakeoffAttempt: the clock only runs while the condition being waited on is actually
// true, so a hold that is released early does not leave time on the meter for the next one.
internal sealed class LandingAttempt
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    /// <summary>True once the budget ran out — stop holding, follow the leg anyway. Stays true
    /// until <see cref="Reset"/>, so the follower cannot re-enter the hold further down the
    /// same leg (the next waypoint would re-arm it, which is the bug wearing a different hat).</summary>
    public bool GaveUp { get; private set; }

    private readonly TimeSpan _budget;
    private DateTime? _startedAt;

    public LandingAttempt(TimeSpan? budget = null) => _budget = budget ?? DefaultBudget;

    /// <summary>New path, or a new leg: the wait is worth starting again.</summary>
    public void Reset()
    {
        _startedAt = null;
        GaveUp = false;
    }

    /// <summary>Call every frame while holding. <paramref name="airborne"/> is the game's own
    /// "in flight" flag (diving is not a landing wait). Returns true while the caller should
    /// keep descending instead of starting to walk.</summary>
    public bool Update(bool airborne, DateTime now)
    {
        if (GaveUp)
            return false;
        if (!airborne)
        {
            _startedAt = null; // on the ground already, or landed: nothing to wait for
            return false;
        }

        _startedAt ??= now;
        if (now - _startedAt.Value < _budget)
            return true;

        GaveUp = true;
        return false;
    }
}
