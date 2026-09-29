using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

public class RouteSanityTests
{
    private static readonly Vector3 Player = new(100, -10, 200);

    [Fact]
    public void RouteFromTheGroundUnderTheFeet_IsFine()
    {
        // the planner hands the start back at the mesh's height, a little off the character's
        Assert.Null(RouteSanity.StartOffset(Player, [new(100, -9.85f, 200), new(140, -9, 230)]));
    }

    [Fact]
    public void AJumpInProgress_IsStillFine()
    {
        var midJump = Player with { Y = -8f };
        Assert.Null(RouteSanity.StartOffset(midJump, [new(100, -10, 200), new(140, -9, 230)]));
    }

    [Fact]
    public void RouteFromThePlaneUnderTheTerrain_IsCaught()
    {
        // South Shroud's plane at y -14, four yalms under a character standing at -10
        var offset = RouteSanity.StartOffset(Player, [new(100, -14, 200), new(140, -14, 230)]);
        Assert.NotNull(offset);
        Assert.Equal(-4f, offset.Value, 3);
        Assert.Equal("4.0y below your feet", RouteSanity.Describe(offset.Value));
    }

    [Fact]
    public void RouteFromAWalkwayOverhead_IsCaught()
    {
        var offset = RouteSanity.StartOffset(Player, [new(100, -6, 200)]);
        Assert.Equal("4.0y above your head", RouteSanity.Describe(offset!.Value));
    }

    [Fact]
    public void NoRoute_NothingToJudge()
    {
        Assert.Null(RouteSanity.StartOffset(Player, []));
    }

    [Fact]
    public void TheLimitIsNarrowerThanThePlannersSnap()
    {
        // the planner snaps within five yalms; a check as wide as that would catch nothing
        Assert.True(RouteSanity.MaxStartOffset < 5f);
        Assert.Null(RouteSanity.StartOffset(Player, [Player with { Y = Player.Y - RouteSanity.MaxStartOffset }]));
        Assert.NotNull(RouteSanity.StartOffset(Player, [Player with { Y = Player.Y - RouteSanity.MaxStartOffset - 0.1f }]));
    }
}
