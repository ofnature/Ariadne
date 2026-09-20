using Ariadne.Mnemosyne;
using Ariadne.Movement;
using System;
using System.Collections.Generic;

namespace Ariadne.Tests;

// Legs are an additive wire feature, so the fallbacks matter as much as the happy path: a
// server that sends none, or nonsense, has to leave the follower following the flat waypoint
// list exactly as it did before legs existed.
public class PathLegTests
{
    private static FindPathLegResponse Wire(string? mode, int first, int count, string? enter = null, ulong? enterArg = null)
        => new() { Mode = mode, First = first, Count = count, Enter = enter, EnterArg = enterArg };

    [Fact]
    public void NoLegs_IsEmpty()
    {
        Assert.Empty(PathLegs.Parse(null, 20));
        Assert.Empty(PathLegs.Parse(Array.Empty<FindPathLegResponse>(), 20));
    }

    [Fact]
    public void NoWaypoints_IsEmpty()
    {
        Assert.Empty(PathLegs.Parse([Wire("walk", 0, 5)], 0));
    }

    [Fact]
    public void WalkedTail_ParsesBothLegsInOrder()
    {
        var legs = PathLegs.Parse([Wire("fly", 0, 12), Wire("walk", 12, 8, "land")], 20);

        Assert.Equal(2, legs.Count);
        Assert.Equal(LegMode.Fly, legs[0].Mode);
        Assert.Equal(0, legs[0].First);
        Assert.Equal(12, legs[0].End);
        Assert.Null(legs[0].Enter);
        Assert.Equal(LegMode.Walk, legs[1].Mode);
        Assert.Equal(LegTransition.Land, legs[1].Enter);
        Assert.True(legs[1].Covers(19));
        Assert.False(legs[1].Covers(20));
    }

    [Fact]
    public void UnsortedWire_IsOrderedByFirst()
    {
        var legs = PathLegs.Parse([Wire("walk", 10, 5), Wire("fly", 0, 10)], 20);

        Assert.Equal(2, legs.Count);
        Assert.Equal(0, legs[0].First);
        Assert.Equal(10, legs[1].First);
    }

    [Fact]
    public void ModeIsCaseInsensitive()
    {
        var legs = PathLegs.Parse([Wire("FLY", 0, 3), Wire("Walk", 3, 3)], 6);

        Assert.Equal(2, legs.Count);
        Assert.Equal(LegMode.Fly, legs[0].Mode);
        Assert.Equal(LegMode.Walk, legs[1].Mode);
    }

    [Fact]
    public void UnknownMode_DropsThatLegOnly()
    {
        Assert.Empty(PathLegs.Parse([Wire("swim", 0, 5)], 20));
        Assert.Single(PathLegs.Parse([Wire("swim", 0, 5), Wire("walk", 5, 5)], 20));
    }

    [Fact]
    public void NonPositiveSpan_IsDropped()
    {
        Assert.Empty(PathLegs.Parse([Wire("walk", 0, 0), Wire("fly", 2, -3)], 20));
    }

    [Fact]
    public void SpanStartingOutsideTheWaypoints_IsDropped()
    {
        Assert.Empty(PathLegs.Parse([Wire("walk", 20, 5)], 20));
        Assert.Empty(PathLegs.Parse([Wire("walk", -1, 5)], 20));
    }

    [Fact]
    public void SpanPastTheEnd_IsClamped()
    {
        var leg = Assert.Single(PathLegs.Parse([Wire("fly", 15, 10)], 20));

        Assert.Equal(15, leg.First);
        Assert.Equal(5, leg.Count);
        Assert.Equal(20, leg.End);
    }

    [Fact]
    public void UnknownTransition_KeepsTheLegWithoutTheTransition()
    {
        var leg = Assert.Single(PathLegs.Parse([Wire("walk", 0, 5, "vault")], 20));

        Assert.Equal(LegMode.Walk, leg.Mode);
        Assert.Null(leg.Enter);
    }

    [Fact]
    public void EnterArg_IsCarried()
    {
        var leg = Assert.Single(PathLegs.Parse([Wire("walk", 0, 5, "teleport", 42)], 20));

        Assert.Equal(LegTransition.Teleport, leg.Enter);
        Assert.Equal(42ul, leg.EnterArg);
    }

    [Fact]
    public void Describe_NamesTheModeAndTransition()
    {
        Assert.Equal("fly", new PathLeg(LegMode.Fly, null, 0, 0, 1).Describe());
        Assert.Equal("walk (enter: land)", new PathLeg(LegMode.Walk, LegTransition.Land, 0, 0, 1).Describe());
    }

    // IndexAt is asked "which leg is the head waypoint in?" every frame, so its boundaries —
    // including gaps between spans, which partial coverage produces — are the whole point.
    private static readonly IReadOnlyList<PathLeg> TwoLegs =
        PathLegs.Parse([Wire("fly", 0, 12), Wire("walk", 12, 8)], 20);

    [Fact]
    public void IndexAt_ReturnsTheCoveringLeg()
    {
        Assert.Equal(0, PathLegs.IndexAt(TwoLegs, 0));   // first waypoint of the fly leg
        Assert.Equal(0, PathLegs.IndexAt(TwoLegs, 11));  // last waypoint of the fly leg
        Assert.Equal(1, PathLegs.IndexAt(TwoLegs, 12));  // boundary belongs to the walk leg
        Assert.Equal(1, PathLegs.IndexAt(TwoLegs, 19));
    }

    [Fact]
    public void IndexAt_PastTheLastLeg_IsNone()
    {
        Assert.Equal(-1, PathLegs.IndexAt(TwoLegs, 20));
        Assert.Equal(-1, PathLegs.IndexAt(TwoLegs, 99));
    }

    [Fact]
    public void IndexAt_InAGap_IsNone()
    {
        var legs = PathLegs.Parse([Wire("fly", 0, 5), Wire("walk", 10, 5)], 20);

        Assert.Equal(0, PathLegs.IndexAt(legs, 4));
        Assert.Equal(-1, PathLegs.IndexAt(legs, 7));
        Assert.Equal(1, PathLegs.IndexAt(legs, 10));
    }

    [Fact]
    public void IndexAt_NoLegs_IsNone()
    {
        Assert.Equal(-1, PathLegs.IndexAt(Array.Empty<PathLeg>(), 0));
    }
}
