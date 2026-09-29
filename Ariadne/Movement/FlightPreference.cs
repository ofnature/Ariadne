using System;
using System.Collections.Generic;

namespace Ariadne.Movement;

/// <summary>
/// "Fly when you can": the decisions, without the game. A caller that asks for a walk gets a
/// flight instead when the zone allows one and the trip is long enough to be worth the mount.
/// Whether the route then actually flies is the planner's call — it compares travel times and
/// answers with the ground route where walking is quicker — so the mount only comes out for a
/// route that really has a leg in the air.
/// </summary>
internal static class FlightPreference
{
    /// <summary>A walk request becomes a flight request.</summary>
    /// <param name="distance">Horizontal distance to the goal, in yalms.</param>
    /// <param name="minDistance">Shorter trips stay on foot: calling the mount and taking off
    /// costs a few seconds, and landing on a doorstep is clumsier than walking to it.</param>
    public static bool ShouldUpgrade(float distance, float minDistance, bool canFlyHere)
        => canFlyHere && distance >= minDistance;

    /// <summary>The route has a part that has to be flown, so a mount is needed before it
    /// starts. A server without legs answers a fly request with plain waypoints, and those are
    /// a flight unless it says it chose the ground.</summary>
    public static bool NeedsFlight(IReadOnlyList<PathLeg>? legs, string result, bool flyRequested)
    {
        if (!flyRequested)
            return false;
        if (legs is { Count: > 0 })
        {
            foreach (var leg in legs)
                if (leg.Mode == LegMode.Fly)
                    return true;
            return false;
        }
        return !string.Equals(result, "groundFaster", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A route with no leg in the air, handed over while the character is flying: come down
    /// before following it. Without this a ground route was steered as a flight — every waypoint
    /// a little higher than the feet asked for a takeoff, so the character hopped the whole way,
    /// overshot the mark, and turned back (Odysseus, 2026-09-29, on "groundFaster" answers).
    /// </summary>
    public static bool LandFirst(bool followAsFlight, IReadOnlyList<PathLeg>? legs, bool inFlight)
    {
        if (followAsFlight || !inFlight)
            return false;
        if (legs is { Count: > 0 })
            foreach (var leg in legs)
                if (leg.Mode == LegMode.Fly)
                    return false;
        return true;
    }
}

/// <summary>
/// Ask the game for something again every so often, for so long, then stop asking. Mounting
/// and dismounting are requests the game can refuse for a moment (mid-cast, mid-jump, a
/// dialog closing) and refuse for good (no mount allowed here); the first wants a retry, the
/// second must not become a loop. Clock-injected, like the takeoff and landing budgets.
/// </summary>
internal sealed class BudgetedRetry(TimeSpan interval, TimeSpan budget)
{
    private DateTime? _startedAt;
    private DateTime _nextAt = DateTime.MinValue;

    /// <summary>The budget ran out. Stays true until <see cref="Reset"/>.</summary>
    public bool GaveUp { get; private set; }

    public void Reset()
    {
        _startedAt = null;
        _nextAt = DateTime.MinValue;
        GaveUp = false;
    }

    /// <summary>True when the request should be made now.</summary>
    public bool Due(DateTime now)
    {
        if (GaveUp)
            return false;
        _startedAt ??= now;
        if (now - _startedAt.Value > budget)
        {
            GaveUp = true;
            return false;
        }
        if (now < _nextAt)
            return false;
        _nextAt = now + interval;
        return true;
    }
}
