using Ariadne.Seeding;

namespace Ariadne.Tests;

public class NavmeshHeaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ariadne-header-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(params byte[][] parts)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".navmesh");
        File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
        return path;
    }

    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    [Fact]
    public void CurrentVersion_IsCurrent()
    {
        var path = WriteFile(U32(NavmeshHeader.ExpectedMagic), U32(25), U32(3), U32(0xDEADBEEF)); // trailing payload bytes ignored
        Assert.True(NavmeshHeader.TryRead(path, out var header));
        Assert.True(header.IsValid);
        Assert.True(header.IsCurrent);
        Assert.Equal(25, header.Version);
        Assert.Equal(3, header.Customization);
    }

    [Theory]
    [InlineData(22u)]
    [InlineData(23u)]
    [InlineData(24u)]
    [InlineData(26u)] // future version is just as unloadable by current vnavmesh as an old one
    public void WrongVersion_ValidButNotCurrent(uint version)
    {
        var path = WriteFile(U32(NavmeshHeader.ExpectedMagic), U32(version), U32(0));
        Assert.True(NavmeshHeader.TryRead(path, out var header));
        Assert.True(header.IsValid);
        Assert.False(header.IsCurrent);
    }

    [Fact]
    public void WrongMagic_Invalid()
    {
        var path = WriteFile(U32(0x46494C45), U32(25), U32(0));
        Assert.True(NavmeshHeader.TryRead(path, out var header));
        Assert.False(header.IsValid);
        Assert.False(header.IsCurrent);
    }

    [Fact]
    public void TruncatedFile_TryReadFails()
    {
        var path = WriteFile(U32(NavmeshHeader.ExpectedMagic), new byte[] { 25, 0 }); // 6 bytes total
        Assert.False(NavmeshHeader.TryRead(path, out _));
    }

    [Fact]
    public void EmptyFile_TryReadFails()
    {
        Assert.False(NavmeshHeader.TryRead(WriteFile(), out _));
    }

    [Fact]
    public void MissingFile_TryReadFails()
    {
        Assert.False(NavmeshHeader.TryRead(Path.Combine(_dir, "does-not-exist.navmesh"), out _));
    }
}
