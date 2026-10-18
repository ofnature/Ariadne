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
}
