using Ariadne.Zone;

namespace Ariadne.Tests;

/// <summary>
/// The Island Sanctuary's layout in the zone key: the tag is short and stable, any change in the
/// state is a different tag, and a change while standing on the island is adopted only once it
/// has held still for the settle time.
/// </summary>
public class IslandLayoutTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private const string Island = "bg/ffxiv/hou_xx/hou/h1m2/level/h1m2//0////";
    private const string IslandA = Island + "//mji-0a1b2c3d";
    private const string IslandB = Island + "//mji-ffffffff";
    private const string Limsa = "bg/ffxiv/sea_s1/twn/s1t1/level/s1t1//11F93//54//0";

    [Fact]
    public void TheHashIsEightHexDigits_AndTheSameForTheSameBytes()
    {
        var a = IslandLayout.Hash([1, 2, 3, 4]);
        Assert.Equal(8, a.Length);
        Assert.Matches("^[0-9a-f]{8}$", a);
        Assert.Equal(a, IslandLayout.Hash([1, 2, 3, 4]));
    }

    [Fact]
    public void OneByteChanged_IsADifferentTag()
    {
        // the pasture fence going up: Pasture.UnderConstruction flips, then Level changes
        Assert.NotEqual(IslandLayout.Hash([1, 0, 0, 0, 1, 0, 0, 0]), IslandLayout.Hash([1, 0, 0, 0, 1, 0, 0, 1]));
        Assert.NotEqual(IslandLayout.Hash([1, 0, 0, 0, 1, 0, 0, 1]), IslandLayout.Hash([1, 0, 0, 0, 1, 0, 1, 0]));
    }

    [Fact]
    public void StripRemovesOnlyTheIslandTag()
    {
        Assert.Equal(Island, IslandSettle.Strip(IslandA));
        Assert.Equal(Limsa, IslandSettle.Strip(Limsa));
    }

    [Fact]
    public void SameZoneOtherLayout_IsTheIslandRebuilt_NotAZoneChange()
    {
        Assert.True(IslandSettle.SameZoneOtherLayout(IslandA, IslandB));
        Assert.False(IslandSettle.SameZoneOtherLayout(IslandA, Limsa));
        Assert.False(IslandSettle.SameZoneOtherLayout(IslandA, IslandA));
        Assert.False(IslandSettle.SameZoneOtherLayout("", IslandA)); // arriving on the island is a zone change
    }

    [Fact]
    public void AZoneChange_IsAdoptedAtOnce()
    {
        var settle = new IslandSettle();
        Assert.Equal(IslandA, settle.Adopt(Limsa, IslandA, T0));
        Assert.Equal("", settle.Adopt(IslandA, "", T0)); // unloading
        Assert.Equal(IslandB, settle.Adopt("", IslandB, T0)); // a different character's island
    }

    [Fact]
    public void BuildingSomething_IsAdoptedOnceTheLayoutHasHeldStill()
    {
        var settle = new IslandSettle();
        Assert.Null(settle.Adopt(IslandA, IslandB, T0));
        Assert.Null(settle.Adopt(IslandA, IslandB, T0.AddSeconds(5)));
        Assert.Equal(IslandB, settle.Adopt(IslandA, IslandB, T0 + IslandSettle.Settle));
    }

    [Fact]
    public void ALayoutStillChanging_RestartsTheClock()
    {
        var settle = new IslandSettle();
        const string islandC = Island + "//mji-00000001";
        Assert.Null(settle.Adopt(IslandA, IslandB, T0));
        Assert.Null(settle.Adopt(IslandA, islandC, T0.AddSeconds(6)));   // changed again: starts over
        Assert.Null(settle.Adopt(IslandA, islandC, T0.AddSeconds(10)));  // 4 s into the new wait
        Assert.Equal(islandC, settle.Adopt(IslandA, islandC, T0.AddSeconds(14)));
    }

    [Fact]
    public void BackToTheCurrentLayout_DropsThePendingOne()
    {
        var settle = new IslandSettle();
        Assert.Null(settle.Adopt(IslandA, IslandB, T0));
        Assert.Null(settle.Adopt(IslandA, IslandA, T0.AddSeconds(3)));
        Assert.Null(settle.Adopt(IslandA, IslandB, T0.AddSeconds(9))); // a fresh wait, not 9 s into the old one
    }
}
