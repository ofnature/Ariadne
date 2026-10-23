using System;

namespace Ariadne;

/// <summary>
/// When a live capture is complete enough to send: once what it holds has stopped growing.
///
/// <para>A zone's layout streams in after the zone change. The broker used to capture the moment the
/// mesh was missing, about 3 s after arriving, and two captures came out short that way on
/// 2026-10-18 and 22: Mount Rokkon (286 objects of 1,683; Mnemosyne now refuses captures that
/// short) and Saar's island (2,694 objects, then 2,717 six minutes later; the 23 the first one
/// missed were the island's own buildings and fences, which load last, and she stalled on them).
/// The second is 99% complete, so no size check on the service can catch it.</para>
///
/// <para>So the broker captures once a second and sends when the counts have held for
/// <see cref="Hold"/>, or after <see cref="MaxWait"/> with whatever it has. Pure and clock-free:
/// the broker owns the waiting.</para>
/// </summary>
internal sealed class CaptureSettle
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private (int Instances, int Meshes)? _last;
    private DateTime _started;
    private DateTime _since;

    /// <summary>How the counts moved from the first capture to the last.</summary>
    public (int Instances, int Meshes)? First { get; private set; }

    /// <summary>Feed one capture's counts; true when it is the one to send.</summary>
    public bool Observe(int instances, int meshes, DateTime now)
    {
        if (_last == null)
        {
            _started = now;
            _since = now;
            First = (instances, meshes);
        }
        else if (_last != (instances, meshes))
        {
            _since = now;
        }
        _last = (instances, meshes);
        return now - _since >= Hold || now - _started >= MaxWait;
    }
}
