using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P336 — normalizes imported missile content models to the real native-.mdl
/// size, so the imported render path agrees with the native-.mdl path for the
/// same model (see <see cref="MissileModelSource.NativeMdlRadius"/> and
/// <see cref="MissileModelSource.ScaleForNativeSize"/>).
/// </summary>
public class MissileModelSourceTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("mis05", 5.803)]
    [InlineData("mis400", 4.520)]
    [InlineData("artifact", 13.328)]
    public void NativeMdlRadius_MatchesTheRealParsedMdlSize(string asset, float expected)
    {
        Require.Artwork();
        // The native-.mdl path (MissileRenderer) draws raw .mdl coordinates at
        // identity scale, so its real rendered merged-bounding-sphere radius is
        // exactly what NativeMdlRadius measures. This is the reference the
        // imported content path normalizes to, so the two paths agree.
        float r = MissileModelSource.NativeMdlRadius(asset);
        Assert.True(r > 0f, $"asset {asset} must resolve a positive native radius, got {r}");
        Assert.Equal(expected, r, 3);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NativeMdlRadius_UnknownOrEmpty_ReturnsZero()
    {
        Require.Artwork();
        Assert.Equal(0f, MissileModelSource.NativeMdlRadius(""));
        Assert.Equal(0f, MissileModelSource.NativeMdlRadius(null!));
        Assert.Equal(0f, MissileModelSource.NativeMdlRadius("does_not_exist"));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ScaleForNativeSize_ScalesSoRenderedEqualsNative()
    {
        Require.Artwork();
        // A content model rendering at 185 units (the ~100× Meshy bone bake,
        // ~the 200-300-unit over-size 2b complained about) must be scaled to
        // land exactly on the real native radius, e.g. mis05's 5.80.
        float native = MissileModelSource.NativeMdlRadius("mis05");
        float scale = MissileModelSource.ScaleForNativeSize(native, 185f);
        Assert.Equal(native, scale * 185f, 3);   // rendered radius * scale == native
        Assert.InRange(scale, 0.025f, 0.045f);   // ~30× shrink, sanity range
    }

    [Fact]
    public void ScaleForNativeSize_GuardsNonPositiveToNoScaling()
    {
        // Missing .mdl (native 0) or degenerate radius → 1 (no scaling),
        // matching the renderer's degrade-not-crash rule.
        Assert.Equal(1f, MissileModelSource.ScaleForNativeSize(0f, 100f), 3);
        Assert.Equal(1f, MissileModelSource.ScaleForNativeSize(5f, 0f), 3);
        Assert.Equal(1f, MissileModelSource.ScaleForNativeSize(-1f, 100f), 3);
        Assert.Equal(1f, MissileModelSource.ScaleForNativeSize(5f, -100f), 3);
    }
}
