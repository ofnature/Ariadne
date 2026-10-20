using Ariadne.Windows.Dock;

namespace Ariadne.Tests;

/// <summary>
/// Every plugin's edge tabs publish to one shared list and are laid out together: each where it was put,
/// pushed apart only when they would overlap. Daedalus and Charon carry the same rules; these pin the ones
/// that make the three docks agree.
/// </summary>
public class DockLayoutTests
{
    private const long Now = 100_000;
    private const float WorkStart = 0f;
    private const float WorkLength = 1000f;

    private static DockSlot Slot(string key, float length, int order, float along = 0.5f, int edge = 0, long seen = Now)
        => new(key, edge, along, length, order, seen);

    private static Dictionary<string, float> Resolve(params DockSlot[] slots)
        => DockLayout.Resolve(0, slots, Now, WorkStart, WorkLength);

    [Fact]
    public void AloneATabIsCentred()
        => Assert.Equal(450f, Resolve(Slot("Ariadne/main", 100f, 3000))["Ariadne/main"]);

    /// <summary>Three plugins' tabs all wanting the middle: one run, centred, Daedalus then Charon then Ariadne.</summary>
    [Fact]
    public void TabsWantingTheSameSpotStackCentredInOrder()
    {
        var at = Resolve(Slot("Ariadne/main", 100f, 3000), Slot("Charon/Floor", 200f, 2001), Slot("Daedalus/main", 100f, 1000));
        var total = 100f + DockLayout.GroupGap + 200f + DockLayout.GroupGap + 100f;
        var top = (WorkLength - total) / 2f;

        Assert.Equal(top, at["Daedalus/main"]);
        Assert.Equal(top + 100f + DockLayout.GroupGap, at["Charon/Floor"]);
        Assert.Equal(top + 100f + DockLayout.GroupGap + 200f + DockLayout.GroupGap, at["Ariadne/main"]);
    }

    /// <summary>One plugin's own tabs sit closer together than two plugins' tabs.</summary>
    [Fact]
    public void OnePluginsTabsUseTheSmallerGap()
    {
        var at = Resolve(Slot("Daedalus/main", 100f, 1000), Slot("Daedalus/occult", 100f, 1001));
        Assert.Equal(at["Daedalus/main"] + 100f + DockLayout.TabGap, at["Daedalus/occult"]);
    }

    /// <summary>A tab put somewhere stays there.</summary>
    [Fact]
    public void APlacedTabSitsWhereItWasPut()
        => Assert.Equal(200f - 50f, Resolve(Slot("Ariadne/main", 100f, 3000, along: 0.2f))["Ariadne/main"]);

    /// <summary>Tabs put far apart do not touch each other.</summary>
    [Fact]
    public void TabsPutFarApartStayPut()
    {
        var at = Resolve(Slot("Ariadne/main", 100f, 3000, along: 0.1f), Slot("Charon/Floor", 100f, 2001, along: 0.9f));
        Assert.Equal(50f, at["Ariadne/main"]);
        Assert.Equal(850f, at["Charon/Floor"]);
    }

    /// <summary>A tab dropped onto another: the two sit end to end, centred on where both wanted to be.</summary>
    [Fact]
    public void ATabDroppedOnAnotherNudgesBothApart()
    {
        var at = Resolve(Slot("Daedalus/main", 100f, 1000, along: 0.30f), Slot("Ariadne/main", 100f, 3000, along: 0.32f));
        var first = at["Daedalus/main"];
        var second = at["Ariadne/main"];

        Assert.Equal(first + 100f + DockLayout.GroupGap, second); // end to end, in order along the edge
        Assert.Equal(310f, (first + second + 100f) / 2f, 3);      // centred on the average of the two wishes
    }

    /// <summary>Kept on screen: a tab put at the very end does not run off it.</summary>
    [Fact]
    public void ATabAtTheEndStaysOnScreen()
    {
        Assert.Equal(900f, Resolve(Slot("Ariadne/main", 100f, 3000, along: 1f))["Ariadne/main"]);
        Assert.Equal(0f, Resolve(Slot("Ariadne/main", 100f, 3000, along: 0f))["Ariadne/main"]);
    }

    /// <summary>Tabs on different edges do not push each other.</summary>
    [Fact]
    public void TabsOnOtherEdgesDoNotPush()
    {
        DockSlot[] slots = [Slot("Daedalus/main", 100f, 1000, edge: 0), Slot("Ariadne/main", 200f, 3000, edge: 1)];
        Assert.Equal(450f, DockLayout.Resolve(0, slots, Now, WorkStart, WorkLength)["Daedalus/main"]);
        Assert.Equal(400f, DockLayout.Resolve(1, slots, Now, WorkStart, WorkLength)["Ariadne/main"]);
    }

    /// <summary>A tab that stopped publishing (closed, unloaded) drops out and the rest close up.</summary>
    [Fact]
    public void AStaleTabDropsOut()
    {
        var at = Resolve(Slot("Daedalus/main", 100f, 1000, seen: Now - DockLayout.StaleMs - 1), Slot("Ariadne/main", 200f, 3000));
        Assert.False(at.ContainsKey("Daedalus/main"));
        Assert.Equal(400f, at["Ariadne/main"]);
    }

    /// <summary>Same spot and same order: the key settles it, so every plugin computes the same layout.</summary>
    [Fact]
    public void FullTiesAreSettledByKey()
    {
        var at = Resolve(Slot("Zeta/a", 100f, 1000), Slot("Alpha/a", 100f, 1000));
        Assert.True(at["Alpha/a"] < at["Zeta/a"]);
    }

    /// <summary>Longer than the screen: start at the top rather than above it.</summary>
    [Fact]
    public void ARunLongerThanTheScreenStartsAtTheTop()
        => Assert.Equal(WorkStart, Resolve(Slot("Daedalus/main", 800f, 1000), Slot("Ariadne/main", 800f, 3000))["Daedalus/main"]);

    [Fact]
    public void TheOwnerIsTheKeyBeforeTheSlash()
        => Assert.Equal("Charon", Slot("Charon/Floor", 1f, 0).Owner);

    [Fact]
    public void TheShareTagIsTheOneDaedalusAndCharonUse() => Assert.Equal("EdgeDock.Layout.v2", DockLayoutShare.Tag);
}
