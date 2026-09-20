using System;

namespace Ariadne.Movement;

// A flying path climbs before it travels, so the follower spams jump to get airborne. That is
// correct where flight is possible and awful where it is not: mounted in a town, every kerb
// and ramp reads as "take off" and the character jumps every 100 ms for the length of the
// path. Nothing in the ported vnavmesh logic ever asked whether the climb was working.
//
// So budget the attempt instead of asking the game whether flight is allowed here. A mounted
// takeoff succeeds on the first jump when it can succeed at all, so a couple of seconds of
// trying is already generous - and a budget also covers the cases a territory lookup would
// miss: indoors, inside an instance, flight not yet unlocked, a duty that grounds you.
//
// Once the budget is spent the path is walked instead. That is the honest fallback: the
// waypoints are still where the consumer wants us to go, we simply cannot fly to them.
internal sealed class TakeoffAttempt
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _budget;
    private DateTime? _startedAt;

    /// <summary>True once the budget ran out. Stays true until <see cref="Reset"/> — without
    /// that the next waypoint below the character would re-arm the attempt and the jumping
    /// would resume, which is the whole bug wearing a different hat.</summary>
    public bool Abandoned { get; private set; }

    public TakeoffAttempt(TimeSpan? budget = null) => _budget = budget ?? DefaultBudget;

    /// <summary>New path: takeoff is worth trying again.</summary>
    public void Reset()
    {
        _startedAt = null;
        Abandoned = false;
    }

    /// <summary>Call every frame while a path is running. <paramref name="wantsTakeoff"/> is
    /// "the next waypoint is above us and we are not airborne". Returns true while the
    /// follower should keep trying to get off the ground.</summary>
    public bool Update(bool wantsTakeoff, DateTime now)
    {
        if (Abandoned)
            return false;
        if (!wantsTakeoff)
        {
            _startedAt = null; // airborne, or heading downhill: the clock only runs while climbing
            return false;
        }

        _startedAt ??= now;
        if (now - _startedAt.Value < _budget)
            return true;

        Abandoned = true;
        return false;
    }
}
