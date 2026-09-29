using System;
using System.Text.RegularExpressions;

namespace Ariadne;

/// <summary>
/// Lets a message through once, and then counts its repeats instead of writing them.
///
/// <para>The broker logged every path answer. A consumer that polls — Theseus's solver asks
/// whether a gate has opened by asking for a route to it — sends the same question many times
/// a minute, and each answer was a line: 94 "findPath: N waypoints partial [noRouteOnMesh]"
/// in seventeen minutes of one dungeon run (2026-09-29), 246 "zone is building" in ninety
/// seconds the day before. The window's activity list holds a hundred entries, so a burst
/// like that also pushed out everything worth reading.</para>
///
/// <para>Two messages are the same when they differ only in their numbers: waypoint counts,
/// timings and coordinates vary between answers that say the same thing. A repeat is held back
/// for <see cref="Window"/>; the first different message, or the first repeat after the window,
/// carries the count of what was held back.</para>
///
/// Clock-injected and free of IO, so the broker's log and the window's list share one.
/// </summary>
internal sealed partial class RepeatFilter(TimeSpan window)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    public TimeSpan Window { get; } = window;

    private string _shape = "";
    private string _last = "";
    private DateTime _firstAt;
    private int _held;

    public RepeatFilter() : this(DefaultWindow) { }

    /// <summary>What to write for this message, if anything. The returned text is the message
    /// itself, preceded by a line accounting for repeats that were held back before it.</summary>
    public string[] Admit(string message, DateTime now)
    {
        var shape = Numbers().Replace(message, "#");
        if (shape == _shape && now - _firstAt < Window)
        {
            ++_held;
            _last = message;
            return [];
        }

        var summary = Summary();
        _shape = shape;
        _last = message;
        _firstAt = now;
        _held = 0;
        return summary == null ? [message] : [summary, message];
    }

    /// <summary>Repeats still being held, for a caller that is about to go quiet.</summary>
    public string? Flush()
    {
        var summary = Summary();
        _held = 0;
        return summary;
    }

    private string? Summary()
        => _held == 0 ? null
            : _held == 1 ? $"  (once more: {_last})"
            : $"  ({_held} more like it, the last: {_last})";

    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex Numbers();
}
