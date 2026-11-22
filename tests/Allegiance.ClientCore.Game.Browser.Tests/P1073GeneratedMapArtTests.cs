using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.Core;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>P1073 — the complete generated-map artwork namespace, pinned so a new
/// generator surface cannot silently escape the browser pack tools.</summary>
public sealed class P1073GeneratedMapArtTests
{
    [Fact]
    public void Planet_posters_are_the_native_authored_pool()
    {
        var stems = GeneratedMapArt.PlanetPosterStems;
        // CMapData::smszPlanetName has 25 entries with nebplnt19 repeated twice
        // (src/Igc/mapmakerigc.cpp:43-70 → MapGenContext.PlanetNames).
        Assert.Equal(24, stems.Count);
        Assert.Equal(stems.Count, stems.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("nebplnt19bmp", stems);
        Assert.Contains("nebplnt36bmp", stems);
        Assert.Contains("nebplnt37bmp", stems);
    }

    [Fact]
    public void Aleph_domes_and_asteroids_are_all_enumerated()
    {
        Assert.Equal(["plnt19", "plnt42", "aleph"], GeneratedMapArt.WarpTextureStems);
        Assert.Equal(Enumerable.Range(1, 8).Select(n => "globe" + n), GeneratedMapArt.EnvironmentDomeStems);
        Assert.Equal(Enumerable.Range(1, 8).Select(n => "environment" + n + "bmp"), GeneratedMapArt.EnvironmentTextureStems);
        // Native asteroidTypes[]: bgrnd03/05 (huge), 03/05 (buildable), 55/56 (He3),
        // 51/52/53 (uranium/silicon/carbon) → seven distinct meshes.
        Assert.Equal(["bgrnd03", "bgrnd05", "bgrnd55", "bgrnd56", "bgrnd51", "bgrnd52", "bgrnd53"],
            GeneratedMapArt.AsteroidModelStems);
        Assert.All(GeneratedMapArt.AsteroidModelStems, m => Assert.Contains(m + "bmp", GeneratedMapArt.AsteroidTextureStems));
        Assert.Contains("aleph", GeneratedMapArt.AllModelStems);
        Assert.Contains("bgrnd55bmp", GeneratedMapArt.AllModelStems);
    }

    [Fact]
    public void Every_stem_is_a_legal_pack_name()
    {
        foreach (string stem in GeneratedMapArt.AllModelStems)
        {
            Assert.Equal(stem.ToLowerInvariant() + ".mdl", StagedManifest.Canonical(stem.ToLowerInvariant() + ".mdl"));
            Assert.DoesNotContain("/", stem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sky_pack_keys_match_the_published_manifest_names()
    {
        var cluster = new DataClusterIGC(1, [0, 0, 1], 0xffffff, 0, 0, 7, 0, 250,
            "Home", "globe6", "nebplnt19bmp", 0, 0, 60, true, true);
        Assert.Equal(["mapgen-globe-6", "mapgen-planet-nebplnt19bmp"], Game1.SkyPackKeys(cluster));
        var noSky = cluster with { PosterName = "", PlanetName = "" };
        Assert.Empty(Game1.SkyPackKeys(noSky));
    }

    [Fact]
    public void Staged_source_enumerates_the_artwork_root()
    {
        // P1073: WarpRenderer/HangarArt probe the root with "." and the browser
        // source used to reject it as a non-canonical pack name.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["alephbmp.mdl"] = [1], ["plnt19bmp.mdl"] = [2], ["p589obj/moon.png"] = [3],
        };
        IAssetSource source = new StagedAssetSource(files);
        Assert.Equal(["alephbmp.mdl", "plnt19bmp.mdl"], source.Enumerate(".").Order());
        Assert.Equal(["alephbmp.mdl", "plnt19bmp.mdl"], source.Enumerate("").Order());
        Assert.Equal(["p589obj/moon.png"], source.Enumerate("p589obj"));
    }
}
