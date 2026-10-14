using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>The optional sky pack (ALLEGIANCE_SKY_PACK_PATH) is layered over the
/// artwork: pack files win, everything else falls through, and with no pack the
/// game reads the artwork alone.</summary>
public class LayeredAssetSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "layered-" + Guid.NewGuid().ToString("N"));
    private readonly string _sky, _art;

    public LayeredAssetSourceTests()
    {
        _sky = Path.Combine(_dir, "sky"); _art = Path.Combine(_dir, "art");
        Directory.CreateDirectory(Path.Combine(_sky, "hdsky", "stars"));
        Directory.CreateDirectory(Path.Combine(_art, "hdsky", "stars"));
        File.WriteAllText(Path.Combine(_sky, "hdsky", "stars", "a.png"), "sky-a");
        File.WriteAllText(Path.Combine(_art, "hdsky", "stars", "a.png"), "art-a");
        File.WriteAllText(Path.Combine(_art, "hdsky", "stars", "b.png"), "art-b");
        File.WriteAllText(Path.Combine(_art, "PCore001.igc"), "core");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Read(IAssetSource s, string p) { using var r = new StreamReader(s.OpenRead(p)); return r.ReadToEnd(); }

    [Fact]
    public void Sky_layer_wins_and_artwork_falls_through()
    {
        var layered = new LayeredAssetSource(new DirectoryAssetSource(_sky), new DirectoryAssetSource(_art));
        Assert.Equal("sky-a", Read(layered, "hdsky/stars/a.png"));      // pack wins
        Assert.Equal("art-b", Read(layered, "hdsky/stars/b.png"));      // falls through
        Assert.Equal("core", Read(layered, "PCore001.igc"));            // non-sky artwork untouched
        Assert.True(layered.Exists("hdsky/stars/a.png"));
        Assert.False(layered.Exists("hdsky/stars/missing.png"));
        Assert.Throws<FileNotFoundException>(() => layered.OpenRead("hdsky/stars/missing.png"));
    }

    [Fact]
    public void Enumerate_is_the_union_with_each_path_once()
    {
        var layered = new LayeredAssetSource(new DirectoryAssetSource(_sky), new DirectoryAssetSource(_art));
        var files = layered.Enumerate("hdsky/stars").Select(f => f.Replace('\\', '/')).OrderBy(f => f).ToArray();
        Assert.Equal(new[] { "hdsky/stars/a.png", "hdsky/stars/b.png" }, files);
    }

    [Fact]
    public void Sky_pack_path_has_its_own_env_var_and_local_env_key()
    {
        Assert.Equal("ALLEGIANCE_SKY_PACK_PATH", ExternalPaths.EnvVar(ExternalResourceKind.SkyPack));
        Assert.Contains("sky pack", ExternalPaths.DescribeMissing(ExternalResourceKind.SkyPack));
    }
}
