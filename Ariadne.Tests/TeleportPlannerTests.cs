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

    [Theory]
    // walking 6 y/s, cost 12 s, saving 5 s: a trip has to be 17 s (102 y) before any crystal can pay
    [InlineData(101f, false, false)]
    [InlineData(103f, false, true)]
    // flying 20 y/s: 340 y
    [InlineData(339f, true, false)]
    [InlineData(341f, true, true)]
    public void CanPay_IsDecidedByTheTripAlone(float distance, bool fly, bool expected)
    {
        Assert.Equal(expected, TeleportPlanner.CanPay(Player, new Vector3(distance, 0, 0), fly, Cost, MinSaving));
    }

    [Fact]
    public void CanPay_NeverRulesOutATripTheFullRuleWouldTake()
    {
        // the best case for a teleport is a crystal standing on the goal: if even that does
        // not pay, nothing does - so the cheap check can only say no when Choose would too
        for (var d = 10f; d < 600f; d += 7f)
        {
            var goal = new Vector3(d, 0, 0);
            foreach (var fly in new[] { false, true })
            {
                var chosen = TeleportPlanner.Choose(Player, goal, fly, [(1u, goal)], Cost, MinSaving) != null;
                Assert.Equal(chosen, TeleportPlanner.CanPay(Player, goal, fly, Cost, MinSaving));
            }
        }
    }
}
