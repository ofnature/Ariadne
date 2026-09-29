using Ariadne.Movement;

namespace Ariadne.Tests;

public class FlightPreferenceTests
{
    [Theory]
    [InlineData(200f, 50f, true, true)]   // far, and the zone allows it
    [InlineData(50f, 50f, true, true)]    // exactly the threshold counts
    [InlineData(49f, 50f, true, false)]   // too short to be worth the mount
    [InlineData(900f, 50f, false, false)] // flight not unlocked here, however far
    public void ShouldUpgrade(float distance, float minDistance, bool canFlyHere, bool expected)
    {
        Assert.Equal(expected, FlightPreference.ShouldUpgrade(distance, minDistance, canFlyHere));
    }

    private static PathLeg Leg(LegMode mode, int first, int count, LegTransition? enter = null)
        => new(mode, enter, 0, first, count);

    /// <summary>
    /// A ground route handed over mid-flight comes down first; one with a flown leg, or while on
    /// the ground, does not. The hopping this stops: a "groundFaster" answer steered as a flight.
    /// </summary>
    [Fact]
    public void LandFirst_only_for_a_ground_route_while_airborne()
    {
        Assert.True(FlightPreference.LandFirst(followAsFlight: false, [Leg(LegMode.Walk, 0, 7)], inFlight: true));
        Assert.True(FlightPreference.LandFirst(followAsFlight: false, null, inFlight: true));
        Assert.False(FlightPreference.LandFirst(followAsFlight: false, [Leg(LegMode.Walk, 0, 7)], inFlight: false));
        Assert.False(FlightPreference.LandFirst(followAsFlight: true, null, inFlight: true));
        Assert.False(FlightPreference.LandFirst(followAsFlight: false, [Leg(LegMode.Fly, 0, 5), Leg(LegMode.Walk, 5, 3, LegTransition.Land)], inFlight: true));
    }

    [Fact]
    public void NeedsFlight_WhenAnyLegFlies()
    {
        var legs = new[] { Leg(LegMode.Fly, 0, 6), Leg(LegMode.Walk, 6, 3, LegTransition.Land) };
        Assert.True(FlightPreference.NeedsFlight(legs, "walkedTail", flyRequested: true));
    }

    [Fact]
    public void NeedsFlight_NotWhenThePlannerChoseTheGround()
    {
        // a fly request the planner answered with the ground route: walking was quicker
        Assert.False(FlightPreference.NeedsFlight([Leg(LegMode.Walk, 0, 7)], "groundFaster", flyRequested: true));
        // the same from a server that sends no legs
        Assert.False(FlightPreference.NeedsFlight(null, "groundFaster", flyRequested: true));
    }

    [Fact]
    public void NeedsFlight_PlainWaypointsForAFlyRequestAreAFlight()
    {
        Assert.True(FlightPreference.NeedsFlight(null, "ok", flyRequested: true));
        Assert.True(FlightPreference.NeedsFlight([], "ok", flyRequested: true));
    }

    [Fact]
    public void NeedsFlight_NeverForAWalkRequest()
    {
        Assert.False(FlightPreference.NeedsFlight(null, "ok", flyRequested: false));
    }

    [Fact]
    public void BudgetedRetry_AsksAtTheInterval_ThenGivesUp()
    {
        var retry = new BudgetedRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        var t0 = new DateTime(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc);

        Assert.True(retry.Due(t0));                        // at once
        Assert.False(retry.Due(t0.AddMilliseconds(400)));  // not yet
        Assert.True(retry.Due(t0.AddSeconds(1)));
        Assert.True(retry.Due(t0.AddSeconds(2.1)));
        Assert.False(retry.GaveUp);

        Assert.False(retry.Due(t0.AddSeconds(3.5)));       // the budget is spent
        Assert.True(retry.GaveUp);
        Assert.False(retry.Due(t0.AddSeconds(10)));        // and it stays spent
    }

    [Fact]
    public void BudgetedRetry_ResetStartsOver()
    {
        var retry = new BudgetedRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var t0 = new DateTime(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc);
        retry.Due(t0);
        Assert.False(retry.Due(t0.AddSeconds(5)));
        Assert.True(retry.GaveUp);

        retry.Reset();
        Assert.False(retry.GaveUp);
        Assert.True(retry.Due(t0.AddSeconds(6)));
    }
}
