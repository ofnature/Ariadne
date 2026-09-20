using System;
using Ariadne.Movement;
using Xunit;

namespace Ariadne.Tests;

/// <summary>The waiting policy for `meshNotReady`: wait and retry, but not forever, and never
/// mask a real failure.</summary>
public class MeshWaitTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WaitingStartsOnlyWhenTheServiceSaysTheMeshIsNotReady()
    {
        var wait = new MeshWait();
        Assert.False(wait.Waiting);

        Assert.True(wait.Record("meshNotReady", T0));
        Assert.True(wait.Waiting);
        Assert.Equal(1, wait.Retries);
    }

    [Fact]
    public void OtherAnswersNeverWait()
    {
        var wait = new MeshWait();

        foreach (var result in new[] { "ok", "noRouteOnMesh", "budgetExhausted", "targetOffMesh", "failed", "serviceUnavailable" })
        {
            Assert.False(wait.Record(result, T0));
            Assert.False(wait.Waiting);
            Assert.Equal(0, wait.Retries);
        }
    }

    [Fact]
    public void TheRetryFiresOnlyOnceTheDelayHasElapsed()
    {
        var wait = new MeshWait();
        Assert.True(wait.Record("meshNotReady", T0));

        Assert.False(wait.Due(T0.AddMilliseconds(249)));
        Assert.True(wait.Due(T0.AddMilliseconds(250)));
    }

    [Fact]
    public void TheBudgetSpansTheWholeWaitAndThenGivesUpHonestly()
    {
        var wait = new MeshWait();
        var now = T0;

        for (var i = 1; i <= MeshWait.DefaultMaxRetries; ++i)
        {
            Assert.True(wait.Record("meshNotReady", now));
            Assert.Equal(i, wait.Retries); // the budget spans the whole wait, not one try
            Assert.True(wait.Due(now.AddMilliseconds(MeshWait.DefaultRetryDelayMs)));
            wait.ClearTimer(); // the caller re-requests: the timer clears, the budget does not
            Assert.False(wait.Waiting, "timer cleared — the caller's own request is what keeps the move in flight now");
            Assert.Equal(i, wait.Retries);
            now = now.AddMilliseconds(MeshWait.DefaultRetryDelayMs);
        }

        // Budget spent: the next "still loading" answer is reported, not waited on any further.
        Assert.False(wait.Record("meshNotReady", now));
        Assert.False(wait.Waiting);
        Assert.Equal(0, wait.Retries);
    }

    [Fact]
    public void SuccessMidWaitEndsTheWaitAndTheBudget()
    {
        var wait = new MeshWait();
        var now = T0;

        Assert.True(wait.Record("meshNotReady", now));
        wait.ClearTimer();
        now = now.AddMilliseconds(MeshWait.DefaultRetryDelayMs);
        Assert.True(wait.Record("meshNotReady", now));
        Assert.Equal(2, wait.Retries);

        // The volume finished loading: a real route. Next zone change starts from a full budget.
        Assert.False(wait.Record("ok", now));
        Assert.False(wait.Waiting);
        Assert.Equal(0, wait.Retries);
        Assert.False(wait.Due(now.AddSeconds(10)));
    }

    [Fact]
    public void ResetClearsAPendingWait()
    {
        var wait = new MeshWait();
        Assert.True(wait.Record("meshNotReady", T0));

        wait.Reset();

        Assert.False(wait.Waiting);
        Assert.Equal(0, wait.Retries);
        Assert.False(wait.Due(T0.AddSeconds(10)));
    }

    [Fact]
    public void TheDelayAndBudgetAreTunable()
    {
        var wait = new MeshWait(retryDelayMs: 100, maxRetries: 2);

        Assert.True(wait.Record("meshNotReady", T0));
        Assert.False(wait.Due(T0.AddMilliseconds(99)));
        Assert.True(wait.Due(T0.AddMilliseconds(100)));

        wait.ClearTimer();
        Assert.True(wait.Record("meshNotReady", T0));
        Assert.False(wait.Record("meshNotReady", T0)); // budget of 2 spent
        Assert.False(wait.Waiting);
    }
}
