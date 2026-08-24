using System;

namespace Ariadne.Movement;

// Progress-toward-destination watchdog, pure logic (unit-testable). Complements
// StallDetector: displacement catches hard stalls (frozen against a wall), but a
// character wobbling ±4y against a tree *displaces* plenty while gaining nothing —
// the Odysseus field runs showed exactly that defeating displacement-only detection.
// Rule (Odysseus's, field-verified): gaining MinGain toward the destination buys the
// clock back; a leg that stops closing faults.
internal sealed class ProgressBudget
{
    public float MinGain { get; }
    public int WindowMs { get; }

    private float _bestAtReset = float.MaxValue;
    private int _elapsedMs;
    private bool _primed;

    public ProgressBudget(float minGain, int windowMs)
    {
        MinGain = minGain;
        WindowMs = windowMs;
    }

    public void Reset()
    {
        _primed = false;
        _elapsedMs = 0;
    }

    /// <summary>Feed once per frame with the remaining distance to the destination.
    /// Returns true on the frame the budget is exhausted (no MinGain closed within
    /// WindowMs); re-arms from the current remaining so a persistent fault re-reports.</summary>
    public bool Update(float remainingDistance, int deltaMs)
    {
        if (!_primed)
        {
            _bestAtReset = remainingDistance;
            _elapsedMs = 0;
            _primed = true;
            return false;
        }

        // moving away doesn't extend the clock — only closing on the goal does
        if (_bestAtReset - remainingDistance >= MinGain)
        {
            _bestAtReset = remainingDistance;
            _elapsedMs = 0;
            return false;
        }

        _elapsedMs += deltaMs;
        if (_elapsedMs < WindowMs)
            return false;

        _bestAtReset = remainingDistance;
        _elapsedMs = 0;
        return true;
    }
}
