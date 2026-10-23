namespace Ariadne.Tests;

/// <summary>
/// A capture is sent once what it holds has stopped growing: the island's buildings and fences
/// stream in after the rest of the zone (Saar, 2026-10-22: 2,694 objects at zone-in, 2,717 once
/// loaded), and a capture taken before them routes straight into them.
/// </summary>
public class CaptureSettleTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 18, 49, 25, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void AStillZone_IsSentAfterTheHold()
    {
        var settle = new CaptureSettle();
        for (var s = 0; s < 5; s++)
            Assert.False(settle.Observe(2717, 195, At(s)));
        Assert.True(settle.Observe(2717, 195, At(5)));
    }

    [Fact]
    public void LateBuildings_RestartTheHold()
    {
        var settle = new CaptureSettle();
        Assert.False(settle.Observe(2694, 193, At(0)));
        Assert.False(settle.Observe(2694, 193, At(4)));
        Assert.False(settle.Observe(2717, 195, At(4.5))); // the island's own buildings arrive
        Assert.False(settle.Observe(2717, 195, At(9)));
        Assert.True(settle.Observe(2717, 195, At(9.5)));
        Assert.Equal((2694, 193), settle.First);
    }

    [Fact]
    public void AMeshCountChange_AloneRestartsTheHold()
    {
        var settle = new CaptureSettle();
        Assert.False(settle.Observe(500, 30, At(0)));
        Assert.False(settle.Observe(500, 31, At(3)));
        Assert.False(settle.Observe(500, 31, At(7)));
        Assert.True(settle.Observe(500, 31, At(8)));
    }

    [Fact]
    public void AZoneThatNeverSettles_IsSentAtTheCap()
    {
        var settle = new CaptureSettle();
        for (var s = 0; s < 30; s++)
            Assert.False(settle.Observe(1000 + s, 50, At(s)));
        Assert.True(settle.Observe(2000, 50, At(30)));
    }
}
