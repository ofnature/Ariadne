namespace Ariadne.Tests;

// The watcher runs on its own thread against wall-clock time, so these are timing tests with
// generous margins. One class, run in order: the trace is process-wide state.
public class MainThreadTraceTests
{
    private static string[] RunTrace(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ariadne-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            MainThreadTrace.Start(dir);
            body(dir);
            MainThreadTrace.Stop();
            Thread.Sleep(MainThreadTrace.FreezeMs + 800); // long enough for a watcher that outlived Stop to speak
            return File.ReadAllLines(MainThreadTrace.FilePath!);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Trace_ReportsWhereTheMainThreadStopped_AndNothingAfterStop()
    {
        var lines = RunTrace(_ =>
        {
            // a healthy main thread
            for (var i = 0; i < 10; i++)
            {
                MainThreadTrace.Heartbeat();
                Thread.Sleep(20);
            }

            // a section that runs long is recorded on the way out
            MainThreadTrace.Heartbeat();
            using (MainThreadTrace.Enter("a slow section"))
                Thread.Sleep(MainThreadTrace.SlowMs + 40);

            // the main thread enters a section and does not come back for a while
            MainThreadTrace.Heartbeat();
            using (MainThreadTrace.Enter("the stuck section"))
                Thread.Sleep(MainThreadTrace.FreezeMs + 700);
            MainThreadTrace.Heartbeat();
            Thread.Sleep(450); // let the watcher see the recovery
        });

        Assert.Contains(lines, l => l.Contains("slow: 'a slow section'"));
        Assert.Contains(lines, l => l.Contains("FREEZE") && l.Contains("'the stuck section'"));
        Assert.Contains(lines, l => l.Contains("RECOVERED"));

        // unloading stops the heartbeat for good; that must not be reported as a freeze
        var stopped = Array.FindIndex(lines, l => l.Contains("trace stopped"));
        Assert.True(stopped >= 0);
        Assert.DoesNotContain(lines.Skip(stopped + 1), l => l.Contains("FREEZE"));
    }
}
