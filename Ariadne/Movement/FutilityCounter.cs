namespace Ariadne.Movement;

// Counts recovery attempts by ground gained, not by clock — the other Odysseus finding:
// a recovery that closes distance deserves fresh attempts; a recovery that doesn't is
// futile, and three futile ones mean the plan (not the execution) is wrong. Pure logic.
internal sealed class FutilityCounter
{
    public float MinGain { get; }
    public int MaxFutile { get; }

    private float? _lastRemaining;
    private int _futile;

    public FutilityCounter(float minGain, int maxFutile)
    {
        MinGain = minGain;
        MaxFutile = maxFutile;
    }

    public int FutileAttempts => _futile;

    public void Reset()
    {
        _lastRemaining = null;
        _futile = 0;
    }

    /// <summary>Record a recovery attempt at the given remaining distance. Gaining MinGain
    /// since the previous attempt clears the count. Returns true when the attempt budget
    /// is exhausted — stop recovering, the plan itself is wrong.</summary>
    public bool RecordAttempt(float remainingDistance)
    {
        if (_lastRemaining is { } prev && prev - remainingDistance >= MinGain)
            _futile = 0;
        else
            _futile++;
        _lastRemaining = remainingDistance;
        return _futile > MaxFutile;
    }
}
