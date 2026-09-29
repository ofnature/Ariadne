using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ariadne;

/// <summary>
/// Says where the game's main thread was when the game stopped ticking, per game instance.
///
/// <para>Why it exists: with several clients on one machine only one of them gets to write
/// Dalamud's log, so an instance that freezes leaves no record at all — and a frozen game
/// cannot be asked anything from inside. Reported 2026-09-28: one instance locking up, always
/// during an Ariadne move, with nothing in any log to say where.</para>
///
/// <para>How: code that runs on the main thread names the section it is in. A background
/// thread watches the framework heartbeat; when it has been silent for a second, it writes
/// down the section the main thread was last seen entering and how long ago. "outside
/// Ariadne" is an answer too: the freeze is somewhere else. Sections that simply run long are
/// recorded as well. One file per process: <c>trace-&lt;pid&gt;.log</c> in Ariadne's config
/// directory.</para>
///
/// Cost: a volatile write and a timestamp per section, nothing allocated. The file is only
/// written when something is slow, frozen, or noted.
/// </summary>
internal static class MainThreadTrace
{
    public const int SlowMs = 50;
    public const int FreezeMs = 1000;
    public const string Outside = "outside Ariadne";
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly object WriteLock = new();
    private static string? _path;
    private static Thread? _watch;
    private static volatile bool _stop;
    private static int _mainThreadId;
    private static volatile string _section = Outside;
    private static long _sectionSince;
    private static long _lastTick;

    public static string? FilePath => _path;

    public static void Start(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"trace-{Environment.ProcessId}.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
                File.Delete(_path);
        }
        catch
        {
            _path = null; // tracing is a convenience; never the reason the plugin fails to load
            return;
        }

        _stop = false;
        Volatile.Write(ref _lastTick, 0);
        _section = Outside;
        Write($"--- trace started, pid {Environment.ProcessId} ---");
        _watch = new Thread(Watch) { IsBackground = true, Name = "Ariadne main-thread watch" };
        _watch.Start();
    }

    public static void Stop()
    {
        _stop = true;
        Write("--- trace stopped ---");
    }

    /// <summary>First thing in the framework tick: the main thread is alive, and this is it.</summary>
    public static void Heartbeat()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _lastTick, Stopwatch.GetTimestamp());
    }

    /// <summary>Name the code the main thread is about to run. Off the main thread this does
    /// nothing, so shared code (the pipe client, the broker) can call it freely.</summary>
    /// <param name="anyThread">Record the section whichever thread runs it. For the unload:
    /// it is the one thing that must be traceable even if Dalamud calls it from elsewhere.</param>
    public static Scope Enter(string section, bool anyThread = false)
    {
        if (_path == null || (!anyThread && Environment.CurrentManagedThreadId != _mainThreadId))
            return default;
        var previous = _section;
        var previousSince = Volatile.Read(ref _sectionSince);
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _sectionSince, now);
        _section = section;
        return new Scope(section, previous, previousSince, now);
    }

    /// <summary>A line of context for whoever reads the trace after a freeze (move requests,
    /// path answers). Written from any thread.</summary>
    public static void Note(string message)
    {
        if (_path != null)
            Write(message);
    }

    internal static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly string? _previous;
        private readonly long _previousSince;
        private readonly long _started;

        internal Scope(string name, string previous, long previousSince, long started)
        {
            _name = name;
            _previous = previous;
            _previousSince = previousSince;
            _started = started;
        }

        public void Dispose()
        {
            if (_name == null)
                return; // the default scope: entered off the main thread, or tracing is off
            var took = Ms(_started, Stopwatch.GetTimestamp());
            _section = _previous ?? Outside;
            Volatile.Write(ref _sectionSince, _previousSince);
            if (took >= SlowMs)
                Write($"slow: '{_name}' held the main thread for {took:0} ms");
        }
    }

    private static void Watch()
    {
        var frozen = false;
        var frozenSection = "";
        long frozenAtTick = 0;
        var nextProgress = 0.0;

        while (!_stop)
        {
            Thread.Sleep(200);
            // Unloading a plugin pauses the whole process for about a second. A watcher that was
            // asleep when Stop was called wakes from that pause to a heartbeat a second old -
            // which is the unload, not a freeze. So look again before judging.
            if (_stop)
                break;
            var last = Volatile.Read(ref _lastTick);
            if (last == 0)
                continue; // no tick seen yet
            var silent = Ms(last, Stopwatch.GetTimestamp());

            if (!frozen && silent >= FreezeMs)
            {
                frozen = true;
                frozenAtTick = last;
                frozenSection = _section;
                var inSection = Ms(Volatile.Read(ref _sectionSince), Stopwatch.GetTimestamp());
                nextProgress = silent + 2000;
                Write(frozenSection == Outside
                    ? $"FREEZE: no framework tick for {silent:0} ms; the main thread is {Outside}"
                    : $"FREEZE: no framework tick for {silent:0} ms; the main thread entered '{frozenSection}' {inSection:0} ms ago and has not left");
            }
            else if (frozen && last == frozenAtTick && silent >= nextProgress)
            {
                nextProgress = silent + 2000;
                Write($"  still frozen after {silent / 1000:0.0} s, section '{_section}'");
            }
            else if (frozen && last != frozenAtTick)
            {
                frozen = false;
                Write($"RECOVERED: ticking again after about {Ms(frozenAtTick, last) / 1000:0.0} s (was in '{frozenSection}')");
            }
        }
    }

    private static void Write(string line)
    {
        var path = _path;
        if (path == null)
            return;
        try
        {
            lock (WriteLock)
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // a locked or missing file must never reach the game's main thread as an exception
        }
    }
}
