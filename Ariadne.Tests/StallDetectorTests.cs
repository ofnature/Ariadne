using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

public class StallDetectorTests
{
    [Fact]
    public void SteadyProgress_NeverStalls()
    {
        var d = new StallDetector(minProgress: 0.5f, windowMs: 1000);
        for (var i = 0; i < 100; i++)
            Assert.False(d.Update(new Vector3(i * 0.6f, 0, 0), 100)); // 0.6 per 100ms, always past threshold
    }

    [Fact]
    public void NoMovement_StallsAfterWindow()
    {
        var d = new StallDetector(0.5f, 1000);
        var pos = new Vector3(3, 0, 3);
        Assert.False(d.Update(pos, 100)); // primes
        for (var t = 100; t < 1000; t += 100)
            Assert.False(d.Update(pos, 100));
        Assert.True(d.Update(pos, 100)); // 1000ms elapsed with no progress
    }

    [Fact]
    public void Jitter_BelowThreshold_StillStalls()
    {
        // wiggling against a tree: sub-threshold displacement must not reset the window
        var d = new StallDetector(0.5f, 1000);
        var stalled = false;
        for (var i = 0; i < 12 && !stalled; i++)
            stalled = d.Update(new Vector3(i % 2 * 0.2f, 0, 0), 100);
        Assert.True(stalled);
    }

    [Fact]
    public void SingleSlowFrame_DoesNotStall()
    {
        // vnavmesh's per-frame speed check would fire on one 600ms hitch; window-based must not
        var d = new StallDetector(0.5f, 1000);
        Assert.False(d.Update(new Vector3(0, 0, 0), 100));
        Assert.False(d.Update(new Vector3(0, 0, 0), 600)); // one long frame, no movement
        Assert.False(d.Update(new Vector3(1, 0, 0), 100)); // then progress resumes
    }

    [Fact]
    public void Reset_RequiresFullWindowAgain()
    {
        var d = new StallDetector(0.5f, 1000);
        var pos = Vector3.Zero;
        d.Update(pos, 100);
        d.Update(pos, 800);
        d.Reset(); // new path started
        Assert.False(d.Update(pos, 100)); // re-primes
        Assert.False(d.Update(pos, 800));
        Assert.True(d.Update(pos, 200));
    }

    [Fact]
    public void PersistentStall_ReportsAgainAfterAnotherWindow()
    {
        var d = new StallDetector(0.5f, 500);
        var pos = Vector3.Zero;
        d.Update(pos, 100);
        Assert.False(d.Update(pos, 400));
        Assert.True(d.Update(pos, 100));
        Assert.False(d.Update(pos, 400));
        Assert.True(d.Update(pos, 100)); // second report → second retry can fire
    }
}
