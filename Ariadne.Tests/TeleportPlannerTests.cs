using Ariadne.Travel;
using System.Numerics;

namespace Ariadne.Tests;

public class TeleportPlannerTests
{
    private static readonly Vector3 Player = new(0, 0, 0);
    private const float Cost = 12f;
    private const float MinSaving = 5f;

    [Fact]
    public void FarGoal_NearbyCrystal_Teleports()
    {
        var goal = new Vector3(600, 0, 0);                         // 100 s walk
        var crystal = (Id: 7u, Position: new Vector3(580, 0, 0)); // 12 s + 3.3 s
        var choice = TeleportPlanner.Choose(Player, goal, fly: false, [crystal], Cost, MinSaving);
        Assert.NotNull(choice);
        Assert.Equal(7u, choice.AetheryteId);
        Assert.Equal(100f, choice.DirectSeconds, 0.01);
        Assert.True(choice.ViaSeconds < 16f);
    }

    [Fact]
    public void FlyingShrinksTheGap_SoTheSameTripStaysDirect()
    {
        var goal = new Vector3(300, 0, 0);                         // 15 s flight
        var crystal = (Id: 7u, Position: new Vector3(290, 0, 0)); // 12 s + 0.5 s: saves 2.5 s < 5
        Assert.Null(TeleportPlanner.Choose(Player, goal, fly: true, [crystal], Cost, MinSaving));
        Assert.NotNull(TeleportPlanner.Choose(Player, goal, fly: false, [crystal], Cost, MinSaving)); // 50 s walk: teleports
    }

    [Fact]
    public void CrystalBehindThePlayer_NeverWins()
    {
        var goal = new Vector3(100, 0, 0);
        var crystal = (Id: 7u, Position: new Vector3(-300, 0, 0));
        Assert.Null(TeleportPlanner.Choose(Player, goal, fly: false, [crystal], Cost, MinSaving));
    }

    [Fact]
    public void PicksTheCrystalClosestToTheGoal()
    {
        var goal = new Vector3(900, 0, 0);
        var choice = TeleportPlanner.Choose(Player, goal, fly: false,
            [(1u, new Vector3(500, 0, 0)), (2u, new Vector3(880, 0, 0)), (3u, new Vector3(700, 0, 0))], Cost, MinSaving);
        Assert.Equal(2u, choice!.AetheryteId);
    }

    [Fact]
    public void NoAttunedCrystals_NoTeleport()
    {
        Assert.Null(TeleportPlanner.Choose(Player, new Vector3(900, 0, 0), false, [], Cost, MinSaving));
    }
}
