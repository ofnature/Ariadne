using Ariadne.Windows.Dock;
using System.Numerics;

namespace Ariadne.Tests;

/// <summary>
/// The edge dock's decisions, ported with the dock from Daedalus (and Charon before it): which panel is
/// out, when it closes, what a click pins, and the geometry that turns a label sideways.
/// </summary>
public class EdgeDockTests
{
    private static readonly string[] Main = ["main"];
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(450);
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PointingAtATabOpensIt()
    {
        var hover = new DockHover();
        hover.Update("main", panelBusy: false, T0, Delay, Main);
        Assert.Equal("main", hover.Open);
    }

    /// <summary>The delay is what stops it flickering shut on the way across the gap to the panel.</summary>
    [Fact]
    public void LeavingTheTabClosesItOnlyAfterTheDelay()
    {
        var hover = new DockHover();
        hover.Update("main", false, T0, Delay, Main);

        hover.Update(null, false, T0.AddMilliseconds(200), Delay, Main);
        Assert.Equal("main", hover.Open);

        hover.Update(null, false, T0.AddMilliseconds(500), Delay, Main);
        Assert.Null(hover.Open);
    }

    /// <summary>The pointer on the panel itself, or a control held in it, keeps it out.</summary>
    [Fact]
    public void ABusyPanelStaysOut()
    {
        var hover = new DockHover();
        hover.Update("main", false, T0, Delay, Main);
        hover.Update(null, panelBusy: true, T0.AddSeconds(5), Delay, Main);
        hover.Update(null, false, T0.AddSeconds(5.2), Delay, Main);
        Assert.Equal("main", hover.Open);
    }

    [Fact]
    public void AClickPinsThePanelOpenAndASecondClickLetsItGo()
    {
        var hover = new DockHover();
        hover.Click("main");
        hover.Update(null, false, T0.AddMinutes(5), Delay, Main);
        Assert.Equal("main", hover.Open);
        Assert.Equal("main", hover.Pinned);

        hover.Click("main");
        hover.Update(null, false, T0.AddMinutes(10), Delay, Main);
        Assert.Null(hover.Pinned);
        Assert.Null(hover.Open);
    }

    /// <summary>A tab that stops being shown takes its panel and its pin with it.</summary>
    [Fact]
    public void AHiddenTabTakesItsPanelWithIt()
    {
        var hover = new DockHover();
        hover.Click("main");
        hover.Update(null, false, T0, Delay, Array.Empty<string>());
        Assert.Null(hover.Open);
        Assert.Null(hover.Pinned);
    }

    /// <summary>A quarter-turn anticlockwise, with screen Y growing downward: right becomes up.</summary>
    [Fact]
    public void TurnUpRotatesAQuarterAnticlockwise()
    {
        var pivot = new Vector2(100f, 100f);
        Assert.Equal(new Vector2(100f, 90f), DockMath.TurnUp(new Vector2(110f, 100f), pivot));
        Assert.Equal(new Vector2(110f, 100f), DockMath.TurnUp(new Vector2(100f, 110f), pivot));
    }

    /// <summary>Text w wide and h tall, turned, reads bottom-to-top inside an h-wide, w-tall box at the asked corner.</summary>
    [Fact]
    public void TurnedTextLandsInTheBoxAsked()
    {
        var topLeft = new Vector2(10f, 20f);
        var textSize = new Vector2(80f, 16f);
        var pivot = DockMath.PivotFor(topLeft, textSize);
        var drawnAt = new Vector2(8f, 8f);

        // The unturned text's corners, drawn at drawnAt.
        var a = DockMath.TurnUpAndPlace(drawnAt, drawnAt, pivot);
        var b = DockMath.TurnUpAndPlace(drawnAt + textSize, drawnAt, pivot);

        Assert.Equal(new Vector2(10f, 100f), a); // the start of the line: bottom-left of the box
        Assert.Equal(new Vector2(26f, 20f), b);  // the far corner: top-right
    }

    private static readonly Vector2 WorkPos = Vector2.Zero;
    private static readonly Vector2 WorkSize = new(1920f, 1080f);

    /// <summary>A dragged tab lands on whichever edge the pointer is let go nearest.</summary>
    [Theory]
    [InlineData(20f, 540f, DockEdge.Left)]
    [InlineData(1900f, 540f, DockEdge.Right)]
    [InlineData(960f, 15f, DockEdge.Top)]
    [InlineData(960f, 1070f, DockEdge.Bottom)]
    [InlineData(300f, 900f, DockEdge.Bottom)] // 180 from the bottom beats 300 from the left
    public void ADroppedTabLandsOnTheNearestEdge(float x, float y, DockEdge expected)
        => Assert.Equal(expected, DockMath.NearestEdge(new Vector2(x, y), WorkPos, WorkSize));

    [Fact]
    public void TopAndBottomRunAlongTheEdge()
    {
        Assert.True(DockMath.IsHorizontal(DockEdge.Top));
        Assert.True(DockMath.IsHorizontal(DockEdge.Bottom));
        Assert.False(DockMath.IsHorizontal(DockEdge.Left));
        Assert.False(DockMath.IsHorizontal(DockEdge.Right));
    }

    /// <summary>Each edge's panel opens on the screen side of its tab.</summary>
    [Fact]
    public void ThePanelOpensOnTheScreenSideOfTheTab()
    {
        var panel = new Vector2(300f, 200f);

        var left = DockMath.PanelPlacement(DockEdge.Left, new Vector2(0f, 400f), new Vector2(34f, 90f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal((new Vector2(40f, 400f), Vector2.Zero), left);

        var right = DockMath.PanelPlacement(DockEdge.Right, new Vector2(1886f, 400f), new Vector2(34f, 90f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal((new Vector2(1880f, 400f), new Vector2(1f, 0f)), right);

        var top = DockMath.PanelPlacement(DockEdge.Top, new Vector2(800f, 0f), new Vector2(90f, 34f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal((new Vector2(800f, 40f), Vector2.Zero), top);

        // Bottom: the pivot is the panel's bottom-left, so it grows upward from just above the tab.
        var bottom = DockMath.PanelPlacement(DockEdge.Bottom, new Vector2(800f, 1046f), new Vector2(90f, 34f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal((new Vector2(800f, 1040f), new Vector2(0f, 1f)), bottom);
    }

    /// <summary>Where along an edge a drop lands: down the side edges, across the top and bottom.</summary>
    [Fact]
    public void TheDropSpotIsMeasuredAlongTheEdge()
    {
        var point = new Vector2(480f, 270f);
        Assert.Equal(0.25f, DockMath.AlongFraction(DockEdge.Left, point, WorkPos, WorkSize), 3);
        Assert.Equal(0.25f, DockMath.AlongFraction(DockEdge.Top, point, WorkPos, WorkSize), 3);
        Assert.Equal(1f, DockMath.AlongFraction(DockEdge.Bottom, new Vector2(5000f, 0f), WorkPos, WorkSize));
    }

    /// <summary>Each edge's tab hugs that edge, standing up the sides and lying along the top and bottom.</summary>
    [Fact]
    public void ATabHugsItsEdge()
    {
        Assert.Equal((new Vector2(0f, 300f), new Vector2(34f, 90f)), DockMath.TabRect(DockEdge.Left, 300f, 90f, 34f, WorkPos, WorkSize));
        Assert.Equal((new Vector2(1886f, 300f), new Vector2(34f, 90f)), DockMath.TabRect(DockEdge.Right, 300f, 90f, 34f, WorkPos, WorkSize));
        Assert.Equal((new Vector2(300f, 0f), new Vector2(90f, 34f)), DockMath.TabRect(DockEdge.Top, 300f, 90f, 34f, WorkPos, WorkSize));
        Assert.Equal((new Vector2(300f, 1046f), new Vector2(90f, 34f)), DockMath.TabRect(DockEdge.Bottom, 300f, 90f, 34f, WorkPos, WorkSize));
    }

    /// <summary>A panel that would run off the screen slides back along the edge instead.</summary>
    [Fact]
    public void APanelNearTheEndOfItsEdgeSlidesBackOnScreen()
    {
        var panel = new Vector2(300f, 200f);

        var top = DockMath.PanelPlacement(DockEdge.Top, new Vector2(1800f, 0f), new Vector2(90f, 34f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal(1920f - 300f - 4f, top.Pos.X);

        var left = DockMath.PanelPlacement(DockEdge.Left, new Vector2(0f, 1000f), new Vector2(34f, 90f), panel, WorkPos, WorkSize, 6f);
        Assert.Equal(1080f - 200f - 4f, left.Pos.Y);
    }

    /// <summary>The v0.1.9 "Right edge" setting becomes the tab's edge once, then clears.</summary>
    [Fact]
    public void TheOldRightEdgeSettingIsCarriedOverOnce()
    {
        var config = new Ariadne.Config.AriadneConfig { EdgeDockRightSide = true };
        Assert.True(config.MigrateEdgeDock());
        Assert.Equal(DockEdge.Right, config.EdgeDockEdge);
        Assert.False(config.EdgeDockRightSide);
        Assert.False(config.MigrateEdgeDock());

        var untouched = new Ariadne.Config.AriadneConfig();
        Assert.False(untouched.MigrateEdgeDock());
        Assert.Equal(DockEdge.Left, untouched.EdgeDockEdge);
    }
}
