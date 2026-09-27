namespace Ariadne.Tests;

// The mesh-ready measurement: how long each pipeline takes to have a usable mesh for a zone, and
// which one answered. The two-rows-out-of-one-zone case is the one worth testing hardest — it is the
// difference between "Ariadne answered in 0.1 s" and "vnavmesh spent 8.6 s building", which is the
// comparison milestone 5 exists for, and it is the measurement that has to survive vnavmesh being
// uninstalled rather than going quietly empty.
public class ReadyTrackerTests
{
    private double _now;
    private MeshReadiness _vnavmesh = new(Available: true, Ready: true, Progress: -1);
    private MeshReadiness _ariadne = new(Available: false, Ready: false, Progress: -1);
    private readonly ReadyTracker _tracker;

    public ReadyTrackerTests()
    {
        _tracker = new ReadyTracker(() => _vnavmesh, () => _ariadne, () => _now);
    }

    private static MeshReadiness Vnav(bool ready, float progress = -1, bool available = true)
        => new(available, ready, progress);

    private static MeshReadiness Service(bool ready, float progress = -1, bool available = true)
        => new(available, ready, progress);

    private void Tick(double advanceSeconds = 0)
    {
        _now += advanceSeconds;
        _tracker.Tick();
    }

    [Fact]
    public void VnavmeshLoadsFromItsCache_RecordsACacheLoad()
    {
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);          // it drops its mesh for the transition
        Tick();                           // -> measuring; a cache load never climbs past 0
        _vnavmesh = Vnav(false, progress: 0f);
        Tick(0.8);
        _vnavmesh = Vnav(true);
        Tick(0.7);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal("zone_a", timing.CacheKey);
        Assert.Equal(MeshSource.VnavmeshCache, timing.Source);
        Assert.False(timing.Built);
        Assert.Equal(1.5, timing.Seconds, precision: 3);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void VnavmeshBuilds_RecordsTheBuild()
    {
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick();
        _vnavmesh = Vnav(false, progress: 0.4f);
        Tick(20);
        _vnavmesh = Vnav(true);           // progress resets before ready flips
        Tick(25);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal(MeshSource.VnavmeshBuilt, timing.Source);
        Assert.True(timing.Built);
        Assert.Equal(45, timing.Seconds, precision: 3);
    }

    [Fact]
    public void SeededZone_RecordsTheAriadneAnswerAndTheBuildItFailedToSave()
    {
        // the pair milestone 5 is about, and the reason this tracker takes two probes: Ariadne has a
        // mesh in hand almost immediately, and vnavmesh builds from scratch anyway because the seed
        // lost the race. Both numbers come out, attributed.
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick();

        _ariadne = Service(ready: false);          // Ariadne's query is in flight
        Tick(0.05);
        _ariadne = Service(ready: true);           // the mesh was already in Mnemosyne's store
        Tick(0.05);
        _vnavmesh = Vnav(false, progress: 0.3f);   // the seed was too late: vnavmesh rebuilds
        Tick(8.5);
        _vnavmesh = Vnav(true);
        Tick();

        var history = _tracker.History; // latest first
        Assert.Equal(2, history.Length);
        Assert.Equal(MeshSource.VnavmeshBuilt, history[0].Source);
        Assert.Equal(8.6, history[0].Seconds, precision: 3);
        Assert.Equal(MeshSource.AriadneAnswered, history[1].Source);
        Assert.Equal(0.1, history[1].Seconds, precision: 3);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void AriadneAnswersFromItsOwnCache_WithoutVnavmeshInstalled()
    {
        // the post-flip shape: vnavmesh is gone, and the measurement has to keep producing a number
        _vnavmesh = Vnav(false, available: false);
        _ariadne = Service(ready: true, available: true);
        _tracker.OnZoneChanged("zone_a");

        _ariadne = Service(ready: false);           // the layout is ready, the query is not answered yet
        Tick(0.05);
        _ariadne = Service(ready: true);
        Tick(0.05);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal(MeshSource.AriadneAnswered, timing.Source);
        Assert.Equal(0.1, timing.Seconds, precision: 3);
        Assert.False(timing.Built);
    }

    [Fact]
    public void MnemosyneBuildsTheZone_RecordsAnAriadneBuild()
    {
        _vnavmesh = Vnav(false, available: false);
        _tracker.OnZoneChanged("zone_a");
        _ariadne = Service(ready: false);
        Tick();
        _ariadne = Service(ready: false, progress: 0.5f); // nobody had the zone: build-on-miss
        Tick(4);
        _ariadne = Service(ready: true);
        Tick(0.2);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal(MeshSource.AriadneBuilt, timing.Source);
        Assert.True(timing.Built);
        Assert.Equal(4.2, timing.Seconds, precision: 3);
    }

    [Fact]
    public void BothPipelinesKeepTheirMesh_NothingRecorded()
    {
        _ariadne = Service(ready: true);
        _tracker.OnZoneChanged("zone_a");
        Tick(1);
        Tick(5);
        Assert.Empty(_tracker.History);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void NoPipelineAvailable_GivesUpQuietly()
    {
        _vnavmesh = Vnav(false, available: false);
        _tracker.OnZoneChanged("zone_a");
        Tick(1);
        Tick(5);
        Assert.Empty(_tracker.History);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void APipelineThatVanishesMidWait_DoesNotHoldTheMeasurementOpen()
    {
        _ariadne = Service(ready: false);
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick();

        _ariadne = Service(ready: false, available: false); // Mnemosyne went away mid-query
        Tick(0.5);
        _vnavmesh = Vnav(true);
        Tick(0.5);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal(MeshSource.VnavmeshCache, timing.Source);
        Assert.Null(_tracker.InProgress);
    }

    [Fact]
    public void ZoneChangeMidMeasurement_RestartsForTheNewZone()
    {
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick(1);

        _tracker.OnZoneChanged("zone_b"); // left before zone_a's mesh came up
        Tick(1);
        _vnavmesh = Vnav(true);
        Tick(2);

        var timing = Assert.Single(_tracker.History);
        Assert.Equal("zone_b", timing.CacheKey);
        Assert.Equal(3, timing.Seconds, precision: 3);
    }

    [Fact]
    public void EmptyKey_CancelsMeasurement()
    {
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick(1);
        _tracker.OnZoneChanged(""); // zone unloading
        _vnavmesh = Vnav(true);
        Tick(1);
        Assert.Empty(_tracker.History);
    }

    [Fact]
    public void InProgress_ExposesLiveElapsedAndProgress()
    {
        _tracker.OnZoneChanged("zone_a");
        _vnavmesh = Vnav(false);
        Tick();
        _vnavmesh = Vnav(false, progress: 0.25f);
        Tick(3);

        var live = _tracker.InProgress;
        Assert.NotNull(live);
        Assert.Equal("zone_a", live.Value.CacheKey);
        Assert.Equal(3, live.Value.Elapsed, precision: 3);
        Assert.Equal(0.25f, live.Value.MaxProgress);
    }

    [Fact]
    public void History_KeepsTheLastTwentyRows()
    {
        for (var zone = 0; zone < 12; zone++) // 12 zones x 2 pipelines = 24 rows
        {
            _tracker.OnZoneChanged($"zone_{zone}");
            _vnavmesh = Vnav(false);
            _ariadne = Service(ready: false);
            Tick();
            _vnavmesh = Vnav(true);
            _ariadne = Service(ready: true);
            Tick(0.1);
        }

        Assert.Equal(20, _tracker.History.Length);
    }
}
