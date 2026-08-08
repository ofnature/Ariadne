using Ariadne.Zone;

namespace Ariadne.Tests;

public class ZoneKeyTests
{
    [Fact]
    public void FormatNumbers_EmptyProducesEmptyString()
    {
        Assert.Equal("", ZoneKey.FormatNumbers([]));
    }

    [Fact]
    public void FormatNumbers_UsesUppercaseHexJoinedByDots()
    {
        // Matches vnavmesh's NavmeshManager.GetCacheKey numbers<T> helper — the cache
        // filename depends on this exact formatting.
        Assert.Equal("1.2.FF.1000", ZoneKey.FormatNumbers([1u, 2u, 255u, 4096u]));
    }
}
