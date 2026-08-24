using Ariadne.Movement;

namespace Ariadne.Tests;

public class ProgressBudgetTests
{
    [Fact]
    public void SteadyClosing_NeverFaults()
    {
        var b = new ProgressBudget(minGain: 10, windowMs: 15000);
        var remaining = 500f;
        for (var i = 0; i < 100; i++)
        {
            Assert.False(b.Update(remaining, 1000));
            remaining -= 12; // 12y/s — always re-earns the window
        }
    }

    [Fact]
    public void WobbleWithoutClosing_Faults()
    {
        // the ±4y tree wobble: plenty of displacement, no gain — must fault after the window
        var b = new ProgressBudget(10, 15000);
        Assert.False(b.Update(300, 0)); // prime
        var faulted = false;
        for (var t = 0; t < 20000 && !faulted; t += 500)
            faulted = b.Update(300 + t / 500 % 2 * 4, 500);
        Assert.True(faulted);
    }

    [Fact]
    public void MovingAway_DoesNotExtendTheClock()
    {
        var b = new ProgressBudget(10, 5000);
        Assert.False(b.Update(100, 0));
        Assert.False(b.Update(150, 2500)); // running the wrong way
        Assert.False(b.Update(120, 2400));
        Assert.True(b.Update(115, 200)); // 5.1s, never closed 10y below the 100 baseline
    }

    [Fact]
    public void GainBuysTheClockBack()
    {
        var b = new ProgressBudget(10, 5000);
        Assert.False(b.Update(100, 0));
        Assert.False(b.Update(95, 4900));  // only 5y gained — clock still running
        Assert.False(b.Update(89, 50));    // 11y total — window resets at 89
        Assert.False(b.Update(88, 4900));  // fresh window
        Assert.True(b.Update(88, 200));    // and exhausts again without another 10
    }

    [Fact]
    public void PersistentFault_ReReports()
    {
        var b = new ProgressBudget(10, 1000);
        b.Update(50, 0);
        Assert.True(b.Update(50, 1100));
        Assert.False(b.Update(50, 900));
        Assert.True(b.Update(50, 200)); // re-armed and faulted again
    }

    [Fact]
    public void Reset_StartsFresh()
    {
        var b = new ProgressBudget(10, 1000);
        b.Update(50, 0);
        b.Update(50, 900);
        b.Reset();
        Assert.False(b.Update(50, 900)); // primes anew; 900ms into a fresh window
        Assert.True(b.Update(50, 1100));
    }
}
