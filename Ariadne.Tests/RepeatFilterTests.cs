namespace Ariadne.Tests;

public class RepeatFilterTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 16, 56, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstMessage_GoesThrough()
    {
        var f = new RepeatFilter();
        var written = f.Admit("findPath: 12 waypoints (3.1ms)", T0);
        Assert.Single(written);
        Assert.Equal("findPath: 12 waypoints (3.1ms)", written[0]);
    }

    [Fact]
    public void SameAnswerWithOtherNumbers_IsHeldBack()
    {
        var f = new RepeatFilter();
        f.Admit("findPath: 17 waypoints partial [noRouteOnMesh] (4.0ms)", T0);
        Assert.Empty(f.Admit("findPath: 9 waypoints partial [noRouteOnMesh] (12.7ms)", T0.AddSeconds(1)));
        Assert.Empty(f.Admit("findPath: 24 waypoints partial [noRouteOnMesh] (2.2ms)", T0.AddSeconds(2)));
    }

    [Fact]
    public void ADifferentMessage_CarriesTheCountOfWhatWasHeld()
    {
        var f = new RepeatFilter();
        f.Admit("findPath: 17 waypoints partial [noRouteOnMesh] (4.0ms)", T0);
        f.Admit("findPath: 9 waypoints partial [noRouteOnMesh] (12.7ms)", T0.AddSeconds(1));
        f.Admit("findPath: 24 waypoints partial [noRouteOnMesh] (2.2ms)", T0.AddSeconds(2));

        var written = f.Admit("findPath: 31 waypoints (14.7ms)", T0.AddSeconds(3));
        Assert.Equal(2, written.Length);
        Assert.Contains("2 more like it", written[0]);
        Assert.Contains("24 waypoints partial", written[0]); // the last one held is the one quoted
        Assert.Equal("findPath: 31 waypoints (14.7ms)", written[1]);
    }

    [Fact]
    public void ARepeatAfterTheWindow_IsWrittenAgain_WithTheCount()
    {
        var f = new RepeatFilter(TimeSpan.FromSeconds(30));
        f.Admit("findPath [meshNotReady]: zone is building", T0);
        for (var i = 1; i <= 20; i++)
            Assert.Empty(f.Admit("findPath [meshNotReady]: zone is building", T0.AddSeconds(i)));

        var written = f.Admit("findPath [meshNotReady]: zone is building", T0.AddSeconds(31));
        Assert.Equal(2, written.Length);
        Assert.Contains("20 more like it", written[0]);
        Assert.Equal("findPath [meshNotReady]: zone is building", written[1]);
    }

    [Fact]
    public void DifferentResults_AreDifferentMessages()
    {
        var f = new RepeatFilter();
        f.Admit("findPath: 17 waypoints partial [noRouteOnMesh] (4.0ms)", T0);
        Assert.Single(f.Admit("findPath [targetOffMesh]: `to` is not on the mesh", T0.AddSeconds(1)));
        Assert.Single(f.Admit("zone 'ex2_02_est_e3_dun_e3d7_level_e3d7__4389E____0': LocalCurrent (12.8ms)", T0.AddSeconds(2)));
    }

    [Fact]
    public void Flush_SaysWhatIsStillHeld_Once()
    {
        var f = new RepeatFilter();
        f.Admit("findPath: 3 waypoints (1.0ms)", T0);
        Assert.Null(f.Flush());
        f.Admit("findPath: 5 waypoints (1.1ms)", T0.AddSeconds(1));
        Assert.Contains("once more", f.Flush());
        Assert.Null(f.Flush());
    }
}
