using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1075ArtRevisionTests
{
    [Fact]
    public void Early_model_texture_and_case_index_misses_expire_only_on_promotion()
    {
        var source = new StagedAssetSource(new(StringComparer.Ordinal));
        var misses = new AssetMissCache(source);
        var index = new AssetStemIndex(source);
        foreach (string model in new[] { "station", "asteroid", "ship", "missile", "probe", "treasure", "projectile" })
        {
            Assert.False(index.ContainsKey(model));
            Assert.True(misses.Add(model));
            Assert.True(misses.Add(model + "bmp"));
            Assert.False(misses.Add(model.ToUpperInvariant())); // once per revision
        }
        Assert.Equal(0, source.ContentRevision);
        source.Merge(new(new(StringComparer.Ordinal) { ["station.mdl"] = [1], ["stationbmp.mdl"] = [2] }));
        Assert.Equal(1, source.ContentRevision);
        Assert.False(misses.Contains("station"));
        Assert.False(misses.Contains("stationbmp"));
        Assert.True(index.TryGetValue("STATION", out string exact));
        Assert.Equal("station", exact);
        Assert.True(index.ContainsKey("stationbmp"));
        Assert.True(misses.Add("still-absent"));
        Assert.False(misses.Add("still-absent"));
        source.Merge(new(new(StringComparer.Ordinal) { ["probe.mdl"] = [3] }));
        Assert.True(misses.Add("still-absent"));
        Assert.True(index.ContainsKey("probe"));
        Assert.True(index.ContainsKey("station"));
    }

    [Fact]
    public void Desktop_sources_never_invalidate_their_lifetime_cache()
    {
        var source = new LocalSource();
        var revision = new AssetContentRevision(source);
        var misses = new AssetMissCache(source);
        Assert.True(misses.Add("missing"));
        for (int frame = 0; frame < 100; frame++)
        {
            Assert.False(revision.Changed());
            Assert.True(misses.Contains("missing"));
            Assert.False(misses.Add("missing"));
        }
    }

    private sealed class LocalSource : IAssetSource
    {
        public bool Exists(string path) => false;
        public Stream OpenRead(string path) => throw new FileNotFoundException();
        public IEnumerable<string> Enumerate(string path) => [];
    }
}
