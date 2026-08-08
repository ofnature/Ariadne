using Ariadne.Seeding;

namespace Ariadne.Tests;

public class CacheSeederTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ariadne-seeder-tests").FullName;
    private readonly string _cacheDir;
    private readonly string _sourceDir;
    private readonly CacheSeeder _seeder;

    public CacheSeederTests()
    {
        // cache dir deliberately not created — Seed must handle a fresh vnavmesh install
        _cacheDir = Path.Combine(_root, "meshcache");
        _sourceDir = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        _seeder = new CacheSeeder(_cacheDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteMesh(string dir, string name, uint version, byte payload = 0xAB)
    {
        var path = Path.Combine(dir, name + ".navmesh");
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(NavmeshHeader.ExpectedMagic));
        bytes.AddRange(BitConverter.GetBytes(version));
        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.Add(payload);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    [Fact]
    public void MissingTarget_SeedsAndCreatesDirectory()
    {
        var source = WriteMesh(_sourceDir, "zone_a", 25);
        Assert.Equal(LocalMeshStatus.Missing, _seeder.LocalStatus("zone_a"));
        Assert.Equal(SeedResult.Seeded, _seeder.Seed("zone_a", source));
        Assert.Equal(LocalMeshStatus.Current, _seeder.LocalStatus("zone_a"));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(_seeder.TargetPath("zone_a")));
    }

    [Fact]
    public void CurrentTarget_IsNoOp()
    {
        var source = WriteMesh(_sourceDir, "zone_a", 25);
        Assert.Equal(SeedResult.Seeded, _seeder.Seed("zone_a", source));
        Assert.Equal(SeedResult.AlreadyCurrent, _seeder.Seed("zone_a", source));
    }

    [Fact]
    public void StaleTarget_IsReplaced()
    {
        Directory.CreateDirectory(_cacheDir);
        WriteMesh(_cacheDir, "zone_a", 24, payload: 0x01);
        Assert.Equal(LocalMeshStatus.Stale, _seeder.LocalStatus("zone_a"));

        var source = WriteMesh(_sourceDir, "zone_a", 25, payload: 0x02);
        Assert.Equal(SeedResult.Seeded, _seeder.Seed("zone_a", source));
        Assert.Equal(LocalMeshStatus.Current, _seeder.LocalStatus("zone_a"));
    }

    [Fact]
    public void StaleSource_IsRejected()
    {
        var source = WriteMesh(_sourceDir, "zone_a", 24);
        Assert.Equal(SeedResult.SourceInvalid, _seeder.Seed("zone_a", source));
        Assert.Equal(LocalMeshStatus.Missing, _seeder.LocalStatus("zone_a"));
    }

    [Fact]
    public void MissingSource_IsRejected()
    {
        Assert.Equal(SeedResult.SourceInvalid, _seeder.Seed("zone_a", Path.Combine(_sourceDir, "nope.navmesh")));
    }
}
