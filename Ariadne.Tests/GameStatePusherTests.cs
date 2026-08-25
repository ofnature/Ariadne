using System.Numerics;

namespace Ariadne.Tests;

public class GameStatePusherTests
{
    private static GameStateSample Sample(float x = 0) =>
        new("zone_a", 129, new Vector3(x, 0, 0), 1.5f, false, "Tester@Testworld");

    [Fact]
    public void Throttles_ToOnePushPerInterval()
    {
        long now = 0;
        var sent = new List<GameStateSample>();
        var pusher = new GameStatePusher(() => Sample(), s => { sent.Add(s); return Task.CompletedTask; }, () => now);

        for (var i = 0; i < 10; i++)
        {
            pusher.Tick(); // 10 ticks within one interval → one push
            now += 10;
        }
        SpinWait.SpinUntil(() => sent.Count > 0, 1000);
        Assert.Single(sent);

        now += 100; // past the interval → next tick pushes again
        pusher.Tick();
        SpinWait.SpinUntil(() => sent.Count > 1, 1000);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void NoSample_NothingSent()
    {
        long now = 0;
        var sent = 0;
        var pusher = new GameStatePusher(() => null, _ => { Interlocked.Increment(ref sent); return Task.CompletedTask; }, () => now);
        for (var i = 0; i < 5; i++)
        {
            pusher.Tick();
            now += 200;
        }
        Assert.Equal(0, sent);
        Assert.False(pusher.IsActive);
    }

    [Fact]
    public void SlowSend_SkipsInsteadOfQueueing()
    {
        long now = 0;
        var release = new TaskCompletionSource();
        var started = 0;
        var pusher = new GameStatePusher(() => Sample(), _ => { Interlocked.Increment(ref started); return release.Task; }, () => now);

        pusher.Tick();
        SpinWait.SpinUntil(() => Volatile.Read(ref started) == 1, 1000);
        now += 500; // several intervals elapse while the first send hangs
        pusher.Tick();
        now += 500;
        pusher.Tick();
        Assert.Equal(1, Volatile.Read(ref started)); // no pile-up behind the stuck send

        release.SetResult();
        now += 100;
        SpinWait.SpinUntil(() =>
        {
            pusher.Tick();
            return Volatile.Read(ref started) == 2; // in-flight slot frees asynchronously after release
        }, 1000);
        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public void ThrowingSend_ReleasesInFlightAndKeepsPushing()
    {
        long now = 0;
        var attempts = 0;
        var pusher = new GameStatePusher(
            () => Sample(),
            _ => { Interlocked.Increment(ref attempts); throw new IOException("Pipe is broken."); },
            () => now);

        pusher.Tick();
        SpinWait.SpinUntil(() => Volatile.Read(ref attempts) == 1, 1000);

        now += 200;
        SpinWait.SpinUntil(() =>
        {
            pusher.Tick();
            return Volatile.Read(ref attempts) >= 2; // the failed send released its slot
        }, 1000);
        Assert.True(Volatile.Read(ref attempts) >= 2);
    }

    [Fact]
    public void IsActive_ReflectsRecentPushes()
    {
        long now = 0;
        var pusher = new GameStatePusher(() => Sample(), _ => Task.CompletedTask, () => now);
        Assert.False(pusher.IsActive);
        pusher.Tick();
        Assert.True(pusher.IsActive);
        now += 6000;
        Assert.False(pusher.IsActive); // stale — matches the server's 5 s presence window
    }
}
