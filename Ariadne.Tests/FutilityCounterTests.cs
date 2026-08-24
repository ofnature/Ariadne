using Ariadne.Movement;

namespace Ariadne.Tests;

public class FutilityCounterTests
{
    [Fact]
    public void ProductiveRecoveries_NeverExhaust()
    {
        // ten stalls, but each recovery gained ground — keep trying forever
        var f = new FutilityCounter(minGain: 10, maxFutile: 3);
        var remaining = 500f;
        for (var i = 0; i < 10; i++)
        {
            Assert.False(f.RecordAttempt(remaining));
            remaining -= 15;
        }
        Assert.Equal(0, f.FutileAttempts); // last productive attempt cleared it
    }

    [Fact]
    public void FutileRecoveries_ExhaustAtMax()
    {
        var f = new FutilityCounter(10, 3);
        Assert.False(f.RecordAttempt(100)); // 1st — no baseline yet, counts as futile #1
        Assert.False(f.RecordAttempt(99));  // #2
        Assert.False(f.RecordAttempt(101)); // #3
        Assert.True(f.RecordAttempt(100));  // #4 > max → give up
    }

    [Fact]
    public void GainResetsTheCount()
    {
        var f = new FutilityCounter(10, 3);
        f.RecordAttempt(100);
        f.RecordAttempt(99);
        Assert.Equal(2, f.FutileAttempts);
        Assert.False(f.RecordAttempt(85)); // 14y gained → count cleared
        Assert.Equal(0, f.FutileAttempts);
        f.RecordAttempt(84);
        f.RecordAttempt(84);
        Assert.False(f.RecordAttempt(84)); // back to 3 — still within budget
        Assert.True(f.RecordAttempt(84));
    }

    [Fact]
    public void Reset_ForNewMove()
    {
        var f = new FutilityCounter(10, 1);
        f.RecordAttempt(100);
        Assert.True(f.RecordAttempt(100));
        f.Reset();
        Assert.False(f.RecordAttempt(100));
    }
}
