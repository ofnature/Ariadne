using Ariadne.Movement;
using System.Numerics;

namespace Ariadne.Tests;

public class PathProgressTests
{
    private static List<Vector3> Path(params (float x, float y, float z)[] pts)
        => [.. pts.Select(p => new Vector3(p.x, p.y, p.z))];

    [Fact]
    public void FarFromFirstWaypoint_NothingPopped()
    {
        var path = Path((10, 0, 0), (20, 0, 0));
        var done = PathProgress.Advance(path, new Vector3(0, 0, 0), null, 0.25f, 0, false);
        Assert.False(done);
        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void PassingThroughWaypoint_PopsIt()
    {
        var path = Path((10, 0, 0), (20, 0, 0));
        // player moved from x=9 to x=11 this frame — the waypoint at x=10 lies on that segment
        var done = PathProgress.Advance(path, new Vector3(11, 0, 0), new Vector3(9, 0, 0), 0.25f, 0, false);
        Assert.False(done);
        Assert.Single(path);
        Assert.Equal(new Vector3(20, 0, 0), path[0]);
    }

    [Fact]
    public void ReachingLastWaypoint_Finishes()
    {
        var path = Path((10, 0, 0));
        var done = PathProgress.Advance(path, new Vector3(10.1f, 0, 0), new Vector3(9.9f, 0, 0), 0.25f, 0, false);
        Assert.True(done);
        Assert.Empty(path);
    }

    [Fact]
    public void WithinDestinationTolerance_FinishesEarly()
    {
        var path = Path((10, 0, 0), (12, 0, 0), (14, 0, 0));
        // 1.5 from the final waypoint with tolerance 2 → done regardless of intermediate points
        var done = PathProgress.Advance(path, new Vector3(12.5f, 0, 0), null, 0.25f, 2f, false);
        Assert.True(done);
        Assert.Empty(path);
    }

    [Fact]
    public void IgnoreDeltaY_TreatsHeightAsIrrelevant()
    {
        var path = Path((10, 5, 0), (20, 5, 0));
        // walking underneath the waypoint at a different Y still counts when ignoring Y
        var doneIgnoring = PathProgress.Advance(path, new Vector3(11, 0, 0), new Vector3(9, 0, 0), 0.25f, 0, true);
        Assert.False(doneIgnoring);
        Assert.Single(path);

        var path2 = Path((10, 5, 0), (20, 5, 0));
        var doneStrict = PathProgress.Advance(path2, new Vector3(11, 0, 0), new Vector3(9, 0, 0), 0.25f, 0, false);
        Assert.False(doneStrict);
        Assert.Equal(2, path2.Count); // 5 yalms below → not passed
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 10, 0, 0, 0)]      // v == a
    [InlineData(5, 0, 0, 0, 0, 0, 10, 0, 0, 0)]      // on the segment
    [InlineData(5, 3, 0, 0, 0, 0, 10, 0, 0, 3)]      // perpendicular offset
    [InlineData(-4, 0, 0, 0, 0, 0, 10, 0, 0, 4)]     // before a → distance to a
    [InlineData(13, 0, 0, 0, 0, 0, 10, 0, 0, 3)]     // beyond b → distance to b
    [InlineData(3, 4, 0, 0, 0, 0, 0, 0, 0, 5)]       // degenerate segment (a == b)
    public void DistanceToLineSegment(float vx, float vy, float vz, float ax, float ay, float az, float bx, float by, float bz, float expected)
    {
        var d = PathProgress.DistanceToLineSegment(new(vx, vy, vz), new(ax, ay, az), new(bx, by, bz));
        Assert.Equal(expected, d, precision: 4);
    }
}
