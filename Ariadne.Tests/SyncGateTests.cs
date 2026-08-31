using Ariadne.Ipc;

namespace Ariadne.Tests;

public class SyncGateTests
{
    [Fact]
    public void CompletedTask_ReturnsValueImmediately()
    {
        Assert.Equal(42, SyncGate.Wait(Task.FromResult(42), 100, -1));
    }

    [Fact]
    public void FastTask_ReturnsValueWithinBudget()
    {
        var result = SyncGate.Wait(Task.Run(async () => { await Task.Delay(10); return "hi"; }), 1000, "fallback");
        Assert.Equal("hi", result);
    }

    [Fact]
    public void SlowTask_ReturnsFallback_AndTaskKeepsRunning()
    {
        var tcs = new TaskCompletionSource<int>();
        Assert.Equal(-1, SyncGate.Wait(tcs.Task, 50, -1));
        tcs.SetResult(7); // late completion must be harmless (completes into nothing)
        Assert.Equal(7, tcs.Task.Result);
    }

    [Fact]
    public void FaultedTask_ReturnsFallback_NeverThrows()
    {
        var task = Task.FromException<int>(new IOException("pipe is broken"));
        Assert.Equal(-1, SyncGate.Wait(task, 100, -1));
    }

    [Fact]
    public void CancelledTask_ReturnsFallback()
    {
        var task = Task.FromCanceled<bool>(new CancellationToken(canceled: true));
        Assert.False(SyncGate.Wait(task, 100, false));
    }
}
