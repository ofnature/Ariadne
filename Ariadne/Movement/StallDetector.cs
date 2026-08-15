using System.Numerics;

namespace Ariadne.Movement;

// No-progress detection, pure logic (unit-testable). vnavmesh's version measured
// instantaneous speed per frame, which false-triggers on a single hitchy frame; this one
// measures displacement over a sliding window instead: stalled = moved less than
// MinProgress over the last WindowMs. Reset whenever a new path starts.
internal sealed class StallDetector
{
    public float MinProgress { get; }
    public int WindowMs { get; }

    private Vector3 _anchor;
    private int _elapsedMs;
    private bool _primed;

    public StallDetector(float minProgress, int windowMs)
    {
        MinProgress = minProgress;
        WindowMs = windowMs;
    }

    public void Reset()
    {
        _primed = false;
        _elapsedMs = 0;
    }

    /// <summary>Feed once per frame; returns true on the frame a stall is detected (and
    /// re-arms, so a persistent stall reports again after another window).</summary>
    public bool Update(Vector3 position, int deltaMs)
    {
        if (!_primed)
        {
            _anchor = position;
            _elapsedMs = 0;
            _primed = true;
            return false;
        }

        if ((position - _anchor).Length() >= MinProgress)
        {
            _anchor = position;
            _elapsedMs = 0;
            return false;
        }

        _elapsedMs += deltaMs;
        if (_elapsedMs < WindowMs)
            return false;

        _anchor = position;
        _elapsedMs = 0;
        return true;
    }
}
