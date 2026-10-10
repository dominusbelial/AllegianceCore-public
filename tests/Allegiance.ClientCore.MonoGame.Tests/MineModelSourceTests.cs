using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P291 — resolves the real per-type mine-field radius from the static core's
/// <c>MineTypes</c> (the <c>DataMineTypeIGC.MineRadius</c> native uses — see
/// <see cref="Allegiance.ClientCore.State.MineEntity"/> for the C++ shadowing
/// proof) with a degrade-not-crash fallback.
/// </summary>
public class MineModelSourceTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveRadius_ReturnsRealMineRadiusForAKnownType()
    {
        Require.Artwork();
        var catalog = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(catalog);
        Assert.NotNull(catalog!.MineTypes);
        Assert.NotEmpty(catalog.MineTypes);

        foreach (var (typeId, mt) in catalog.MineTypes)
        {
            // Every real mine type must resolve to its own positive MineRadius
            // (the garbage base `Radius` is never used — see MineEntity doc).
            float r = MineModelSource.ResolveRadius(typeId, catalog.MineTypes);
            Assert.True(r > 0f, $"type {typeId} resolved radius {r} must be positive");
            Assert.Equal(mt.MineRadius, r, 0.001f);
        }
    }

    [Fact]
    public void ResolveRadius_FallsBackToDefaultForUnknownOrMissing()
    {
        // Unknown type id (nonexistent) → documented default.
        Assert.Equal(MineModelSource.DefaultRadius, MineModelSource.ResolveRadius(short.MaxValue, null), 0.001f);
        // Missing table → default.
        Assert.Equal(MineModelSource.DefaultRadius, MineModelSource.ResolveRadius(1, null), 0.001f);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LoadTexture_ParsesTheRealMineSprite()
    {
        Require.Artwork();
        var tex = MineModelSource.LoadTexture();
        Assert.True(tex.Width > 0 && tex.Height > 0, "the real fxmine sprite must decode to a non-empty texture");
        Assert.True(tex.Width == tex.Height, "the mine sprite is square");
    }
}
