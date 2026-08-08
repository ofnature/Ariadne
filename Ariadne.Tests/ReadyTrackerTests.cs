namespace Ariadne.Tests;

public class ReadyTrackerTests
{
    private double _now;
    private bool _available = true;
    private bool _ready = true;
    private float _progress = -1;
    private readonly ReadyTracker _tracker;

    public ReadyTrackerTests()
    {
        _tracker = new ReadyTracker(() => _available, () => _ready, () => _progress, () => _now);
    }

    private void Tick(double advanceSeconds = 0)
    {
        _now += advanceSeconds;
        _tracker.Tick();
    }

    [Fact]
    public void CacheLoad_RecordsUnbuiltTiming()
    {
        _tracker.OnZoneChanged("zone_a");
        _ready = false;          // vnavmesh drops its mesh for the transition
        Tick();                  // -> measuring; progress never leaves 0 during a cache load
        _progress = 0f;
        Tick(0.8);
        _progress = -1;
        _ready = true;
        Tick(0.7);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal("zone_a", timing.CacheKey);
        Assert.False(timing.Built);
        Assert.Equal(1.5, timing.Seconds, precision: 3);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void Build_ProgressAdvancing_RecordsBuiltTiming()
    {
        _tracker.OnZoneChanged("zone_a");
        _ready = false;
        Tick();
        _progress = 0.4f;
        Tick(20);
        _progress = -1;          // build done, progress resets before ready flips
        _ready = true;
        Tick(25);

        var timing = Assert.Single(_tracker.History);
        Assert.True(timing.Built);
        Assert.Equal(45, timing.Seconds, precision: 3);
    }

    [Fact]
    public void NoTransition_NothingRecorded()
    {
        _tracker.OnZoneChanged("zone_a"); // vnavmesh stays ready the whole time
        Tick(1);
        Tick(5);
        Tick(1);
        Assert.Empty(_tracker.History);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void VnavmeshAbsent_GivesUpQuietly()
    {
        _available = false;
        _tracker.OnZoneChanged("zone_a");
        Tick(1);
        Tick(5);
        Assert.Empty(_tracker.History);
    }

    [Fact]
    public void ZoneChangeMidMeasurement_RestartsForNewZone()
    {
        _tracker.OnZoneChanged("zone_a");
        _ready = false;
        Tick(1);

        _tracker.OnZoneChanged("zone_b"); // left before zone_a's mesh came up
        Tick(1);
        _ready = true;
        Tick(2);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal("zone_b", timing.CacheKey);
        Assert.Equal(3, timing.Seconds, precision: 3);
    }

    [Fact]
    public void EmptyKey_CancelsMeasurement()
    {
        _tracker.OnZoneChanged("zone_a");
        _ready = false;
        Tick(1);
        _tracker.OnZoneChanged(""); // zone unloading
        _ready = true;
        Tick(1);
        Assert.Empty(_tracker.History);
    }

    [Fact]
    public void InProgress_ExposesLiveElapsedAndProgress()
    {
        _tracker.OnZoneChanged("zone_a");
        _ready = false;
        Tick();
        _progress = 0.25f;
        Tick(3);

        var live = _tracker.InProgress;
        Assert.NotNull(live);
        Assert.Equal("zone_a", live.Value.CacheKey);
        Assert.Equal(3, live.Value.Elapsed, precision: 3);
        Assert.Equal(0.25f, live.Value.MaxProgress);
    }
}
