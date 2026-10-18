using Ariadne.Windows.Dock;

namespace Ariadne.Tests;

/// <summary>
/// Several plugins' edge docks on one edge publish to one shared list and stack as a group. Daedalus and
/// Charon carry the same rules; these tests pin the ones that make three docks agree.
/// </summary>
public class DockLayoutTests
{
    private const long Now = 100_000;
    private const float WorkTop = 0f;
    private const float WorkHeight = 1000f;

    private static DockSlot Slot(string owner, float height, int order, int edge = 0, long seen = Now)
        => new(owner, edge, height, order, seen);

    [Fact]
    public void AloneADockIsCentred()
    {
        var top = DockLayout.TopFor("Ariadne", 0, 100f, [Slot("Ariadne", 100f, 30)], Now, WorkTop, WorkHeight);
        Assert.Equal(450f, top);
    }

    /// <summary>Three docks on one edge: one group, centred, Daedalus then Charon then Ariadne.</summary>
    [Fact]
    public void ThreeDocksOnOneEdgeStackInOrder()
    {
        DockSlot[] slots = [Slot("Ariadne", 100f, 30), Slot("Charon", 200f, 20), Slot("Daedalus", 100f, 10)];
        var total = 100f + DockLayout.GroupGap + 200f + DockLayout.GroupGap + 100f;
        var groupTop = (WorkHeight - total) / 2f;

        Assert.Equal(groupTop, DockLayout.TopFor("Daedalus", 0, 100f, slots, Now, WorkTop, WorkHeight));
        Assert.Equal(groupTop + 100f + DockLayout.GroupGap, DockLayout.TopFor("Charon", 0, 200f, slots, Now, WorkTop, WorkHeight));
        Assert.Equal(groupTop + 100f + DockLayout.GroupGap + 200f + DockLayout.GroupGap,
            DockLayout.TopFor("Ariadne", 0, 100f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>Docks on different edges do not push each other.</summary>
    [Fact]
    public void DocksOnOppositeEdgesAreEachCentred()
    {
        DockSlot[] slots = [Slot("Daedalus", 100f, 10, edge: 0), Slot("Ariadne", 200f, 30, edge: 1)];
        Assert.Equal(450f, DockLayout.TopFor("Daedalus", 0, 100f, slots, Now, WorkTop, WorkHeight));
        Assert.Equal(400f, DockLayout.TopFor("Ariadne", 1, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>A dock that stopped publishing (closed, unloaded) drops out and the rest close up.</summary>
    [Fact]
    public void AStaleDockDropsOut()
    {
        DockSlot[] slots = [Slot("Daedalus", 100f, 10, seen: Now - DockLayout.StaleMs - 1), Slot("Ariadne", 200f, 30)];
        Assert.Equal(400f, DockLayout.TopFor("Ariadne", 0, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>A dock with no tabs this frame takes no room.</summary>
    [Fact]
    public void AnEmptyDockTakesNoRoom()
    {
        DockSlot[] slots = [Slot("Daedalus", 0f, 10), Slot("Ariadne", 200f, 30)];
        Assert.Equal(400f, DockLayout.TopFor("Ariadne", 0, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>Not in the list (the share was unavailable): centre alone.</summary>
    [Fact]
    public void MissingFromTheListMeansCentredAlone()
        => Assert.Equal(450f, DockLayout.TopFor("Ariadne", 0, 100f, [Slot("Charon", 200f, 20)], Now, WorkTop, WorkHeight));

    [Fact]
    public void TheShareTagIsTheOneDaedalusAndCharonUse() => Assert.Equal("EdgeDock.Layout.v1", DockLayoutShare.Tag);
}
