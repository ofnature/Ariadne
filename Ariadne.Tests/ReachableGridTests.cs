using Ariadne.Mnemosyne;
using System;

namespace Ariadne.Tests;

// A grid the client cannot index safely must be refused, not passed on: the consumer turns
// `Columns[i]` into a lookup in its own width×depth array, so a mismatched length or an
// out-of-range column would read past the end of it.
public class ReachableGridTests
{
    private static readonly int[] Columns = [0, 4, 5];
    private static readonly float[] Heights = [12.4f, 12.4f, 20f];
    private static readonly int[] States = [1, 2, 1];

    [Fact]
    public void WellFormedGrid_Validates()
    {
        Assert.True(ReachableGrid.TryValidate(Columns, Heights, States, 3, 2, out var whyNot));
        Assert.Equal("", whyNot);
    }

    [Fact]
    public void EmptyGrid_Validates()
    {
        // a window with no walkable surface in it is legitimate; the broker only validates a
        // non-empty grid, so this must not be the thing that refuses it
        Assert.True(ReachableGrid.TryValidate([], [], [], 3, 2, out _));
    }

    [Fact]
    public void HeightCountMismatch_IsRefused()
    {
        Assert.False(ReachableGrid.TryValidate(Columns, [12.4f, 12.4f], States, 3, 2, out var whyNot));
        Assert.Contains("parallel arrays", whyNot);
    }

    [Fact]
    public void StateCountMismatch_IsRefused()
    {
        Assert.False(ReachableGrid.TryValidate(Columns, Heights, [1], 3, 2, out var whyNot));
        Assert.Contains("parallel arrays", whyNot);
    }

    [Fact]
    public void GridWithoutDimensions_IsRefused()
    {
        Assert.False(ReachableGrid.TryValidate(Columns, Heights, States, 0, 2, out var whyNot));
        Assert.Contains("0×2", whyNot);
    }

    [Fact]
    public void ColumnOutsideTheGrid_IsRefused()
    {
        Assert.False(ReachableGrid.TryValidate([0, 6], [1f, 1f], [1, 1], 3, 2, out var whyNot)); // 6 == width*depth
        Assert.Contains("outside", whyNot);
    }

    [Fact]
    public void NegativeColumn_IsRefused()
    {
        Assert.False(ReachableGrid.TryValidate([-1], [1f], [1], 3, 2, out _));
    }

    [Fact]
    public void States_AreCarriedToTheConsumerAsBytes()
    {
        Assert.Equal(new byte[] { 1, 2, 1 }, ReachableGrid.ToStates(States));
    }

    [Fact]
    public void States_OutsideTheDocumentedRange_AreClampedNotWrapped()
    {
        // 1 reachable · 2 cutOff are the only documented values; anything else is the server's
        // business, but it must not wrap to the opposite meaning on the way through a byte
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, ReachableGrid.ToStates([-5, 1, 2, 300]));
    }
}
