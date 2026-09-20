using Ariadne.Movement;
using System;

namespace Ariadne.Tests;

// The landing gate exists so a `land` leg does not start walking in mid-air, and so a spot
// with nothing to land on cannot freeze the path: hold while the game reports flight, give up
// loudly after the budget.
public class LandingAttemptTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LandingAttempt NewGate() => new(TimeSpan.FromSeconds(10));

    [Fact]
    public void AlreadyOnTheGround_DoesNotHold()
    {
        var gate = NewGate();

        Assert.False(gate.Update(false, T0));
        Assert.False(gate.GaveUp);
    }

    [Fact]
    public void Airborne_HoldsUntilTheBudgetRunsOut()
    {
        var gate = NewGate();

        Assert.True(gate.Update(true, T0));
        Assert.True(gate.Update(true, T0.AddSeconds(9.9)));
        Assert.False(gate.Update(true, T0.AddSeconds(10)));
        Assert.True(gate.GaveUp);
    }

    [Fact]
    public void GaveUp_StaysGivenUpUntilReset()
    {
        var gate = NewGate();
        gate.Update(true, T0);
        gate.Update(true, T0.AddSeconds(10));

        Assert.False(gate.Update(true, T0.AddSeconds(11)));
        Assert.False(gate.Update(false, T0.AddSeconds(12)));
        Assert.True(gate.GaveUp);

        gate.Reset();
        Assert.False(gate.GaveUp);
        Assert.True(gate.Update(true, T0.AddSeconds(13)));
    }

    [Fact]
    public void LandingEarly_ReleasesWithoutGivingUp()
    {
        var gate = NewGate();

        Assert.True(gate.Update(true, T0));
        Assert.False(gate.Update(false, T0.AddSeconds(3)));
        Assert.False(gate.GaveUp);
    }

    [Fact]
    public void TheClockOnlyRunsWhileAirborne()
    {
        var gate = NewGate();

        gate.Update(true, T0);                 // starts
        gate.Update(false, T0.AddSeconds(9));  // on the ground: the clock clears
        Assert.True(gate.Update(true, T0.AddSeconds(9.5)));
        Assert.True(gate.Update(true, T0.AddSeconds(19)));    // 9.5 s elapsed, still inside
        Assert.False(gate.Update(true, T0.AddSeconds(19.6))); // 10.1 s: budget spent
        Assert.True(gate.GaveUp);
    }
}
