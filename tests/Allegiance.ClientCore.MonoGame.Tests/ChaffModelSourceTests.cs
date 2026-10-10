using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P292 — resolves the real per-chaff visual parameters (texture stem, lifespan,
/// radius ceiling) from the static core's <c>ChaffTypes</c> (the
/// <c>DataChaffTypeIGC</c> native uses — <c>TextureName</c>/<c>.Radius</c>/
/// <c>.Lifespan</c>, see <c>src/Igc/chaffigc.cpp:42,61,82</c>) with a
/// degrade-not-crash fallback, and parses the real chaff sprite assets.
/// </summary>
public class ChaffModelSourceTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Resolve_ReturnsRealValuesForEveryKnownType_InTheDemoCore()
    {
        Require.Artwork();
        var catalog = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(catalog);
        Assert.NotNull(catalog!.ChaffTypes);
        Assert.NotEmpty(catalog.ChaffTypes);

        foreach (var (typeId, ct) in catalog.ChaffTypes)
        {
            Assert.Equal(ct.Lifespan, ChaffModelSource.ResolveLifespan(typeId, catalog.ChaffTypes), 0.001f);
            Assert.Equal(ct.Radius, ChaffModelSource.ResolveRadiusCeiling(typeId, catalog.ChaffTypes), 0.001f);
            // The demo core's chaff all carry TextureName "fxmine" → stem fxminebmp.
            Assert.Equal(ct.TextureName + "bmp", ChaffModelSource.ResolveStem(typeId, catalog.ChaffTypes));
        }
    }

    [Fact]
    public void Resolve_FallsBackToDefaults_ForUnknownOrMissing()
    {
        // Unknown type id / missing table → the documented defaults.
        Assert.Equal(ChaffEntity.DefaultLifespan, ChaffModelSource.ResolveLifespan(short.MaxValue, null), 0.001f);
        Assert.Equal(ChaffEntity.DefaultRadiusCeiling, ChaffModelSource.ResolveRadiusCeiling(short.MaxValue, null), 0.001f);
        Assert.Equal(ChaffModelSource.DefaultStem, ChaffModelSource.ResolveStem(short.MaxValue, null));
        Assert.Equal(ChaffEntity.DefaultLifespan, ChaffModelSource.ResolveLifespan(1, null), 0.001f);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveStem_NormalisesCase_AndDefaults_ForUnrealTexture()
    {
        Require.Artwork();
        // Case-insensitive: "FXMINE" resolves to the canonical real stem.
        Allegiance.Core.DataChaffTypeIGC weirdRec = default;
        var weird = new Dictionary<short, Allegiance.Core.DataChaffTypeIGC>
        {
            [7] = weirdRec with { TextureName = "FXMINE" },
        };
        Assert.Equal("fxminebmp", ChaffModelSource.ResolveStem(7, weird));

        // A texture name that maps to no real asset falls back to the default.
        Allegiance.Core.DataChaffTypeIGC bogusRec = default;
        var bogus = new Dictionary<short, Allegiance.Core.DataChaffTypeIGC>
        {
            [7] = bogusRec with { TextureName = "nosuchtex" },
        };
        Assert.Equal(ChaffModelSource.DefaultStem, ChaffModelSource.ResolveStem(7, bogus));
    }

    [Fact]
    public void RealStems_ContainsOnlyTheTwoCensusTextures()
    {
        Assert.Equal(2, ChaffModelSource.RealStems.Count);
        Assert.Contains("fxminebmp", ChaffModelSource.RealStems);
        Assert.Contains("cc_cmtexbmp", ChaffModelSource.RealStems);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("fxminebmp")]
    [InlineData("cc_cmtexbmp")]
    public void LoadTexture_ParsesTheRealChaffSprites(string stem)
    {
        Require.Artwork();
        var tex = ChaffModelSource.LoadTexture(stem);
        Assert.True(tex.Width > 0 && tex.Height > 0, $"the real {stem}.mdl sprite must decode to a non-empty texture");
        Assert.True(tex.Width == tex.Height, "chaff sprites are square");
    }
}
