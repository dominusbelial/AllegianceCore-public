using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Rendering;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P529 — failing-first unit proofs for the asteroid sector-lighting helper
/// (<see cref="SectorLighting"/>) and the rock mip-map chain
/// (<see cref="MipmapChain"/>). SectorLighting is the pure, GPU-free bridge
/// between a cluster's <c>DataClusterIGC.LightDirection</c>/<c>LightColor</c>
/// and the two directional lights a MonoGame <c>BasicEffect</c> can render —
/// the exact replication of native's bidirectional lighting
/// (<c>Device3D::BidirectionalLightVertex</c>, see docs/reports/P529).
/// </summary>
public sealed class P529AsteroidLightingTests
{
    private const float Tol = 1e-3f;

    private static void AssertClose(Collision.Vector3 v, float r, float g, float b)
    {
        Assert.InRange(v.X, r - Tol, r + Tol);
        Assert.InRange(v.Y, g - Tol, g + Tol);
        Assert.InRange(v.Z, b - Tol, b + Tol);
    }

    /// <summary>
    /// A known cluster: light sitting on the +X side (toward-light = (1,0,0)),
    /// key colour 0x00FF8000 (R=1.0, G=128/255, B=0). Expects the key light to
    /// propagate the OPPOSITE way (native's toward-light is the direction the
    /// brightest normal points; BasicEffect's Direction is propagation), the
    /// alt/back light from the opposite direction at the native constant blue
    /// (0, 0.5, 1.0), and 0.25 ambient — no specular anywhere.
    /// </summary>
    [Fact]
    public void KnownCluster_YieldsExpectedKeyAndOppositeBlueAlt()
    {
        var lp = SectorLighting.FromCluster([1f, 0f, 0f], 0x00FF8000);

        // Key light propagation = -toward-light (sign reasoning in the report).
        Assert.Equal(new Collision.Vector3(-1f, 0f, 0f), lp.KeyDirection);
        AssertClose(lp.KeyColor, 1f, 128f / 255f, 0f);

        // Back light from the opposite direction, native constant blue.
        Assert.Equal(new Collision.Vector3(1f, 0f, 0f), lp.AltDirection);
        AssertClose(lp.AltColor, 0f, 0.5f, 1f);

        // Ambient = native bidirectional 0.25 (clusterIGC.h:331-334), equal on
        // every channel (native adds a scalar to r,g,b alike).
        AssertClose(lp.Ambient, 0.25f, 0.25f, 0.25f);

        // SectorLighting carries NO specular field — the renderer pins every
        // directional light's SpecularColor to zero (native has none).
    }

    [Fact]
    public void Direction_IsNormalisedToUnitLength()
    {
        // Non-unit toward-light (0,0,2) normalises to (0,0,1); propagation = -that.
        var lp = SectorLighting.FromCluster([0f, 0f, 2f], 0xFFFFFFFF);
        Assert.Equal(new Collision.Vector3(0f, 0f, -1f), lp.KeyDirection);
        Assert.Equal(new Collision.Vector3(0f, 0f, 1f), lp.AltDirection);
    }

    [Fact]
    public void DegenerateOrMissingDirection_FallsBackToFixedWhiteLight()
    {
        // Missing direction → sane fixed direction + white key light, never black.
        var missing = SectorLighting.FromCluster(null, 0x00000000);
        Assert.Equal(new Collision.Vector3(0f, 0f, -1f), missing.KeyDirection);
        AssertClose(missing.KeyColor, 1f, 1f, 1f);

        // Zero-magnitude direction → same fallback.
        var zero = SectorLighting.FromCluster([0f, 0f, 0f], 0xFFFF0000);
        Assert.Equal(new Collision.Vector3(0f, 0f, -1f), zero.KeyDirection);
        AssertClose(zero.KeyColor, 1f, 1f, 1f);
    }

    [Fact]
    public void LightColor_Decodes_0xAARRGGBB()
    {
        // 0xFFFF0000 → R=255,G=0,B=0 (alpha 0xFF ignored).
        AssertClose(SectorLighting.FromCluster([1f, 0f, 0f], 0xFFFF0000).KeyColor, 1f, 0f, 0f);
        // 0x0000FF00 → R=0,G=255,B=0.
        AssertClose(SectorLighting.FromCluster([1f, 0f, 0f], 0x0000FF00).KeyColor, 0f, 1f, 0f);
        // 0x000000FF → R=0,G=0,B=255.
        AssertClose(SectorLighting.FromCluster([1f, 0f, 0f], 0x000000FF).KeyColor, 0f, 0f, 1f);
    }

    [Fact]
    public void MipmapChain_LevelCount_IsFloorLog2PlusOne()
    {
        Assert.Equal(9, MipmapChain.LevelCount(256, 256));   // the procedural rock
        Assert.Equal(12, MipmapChain.LevelCount(2048, 2048)); // the real bgrndNNbmp surfaces
        Assert.Equal(1, MipmapChain.LevelCount(1, 1));
        Assert.Equal(2, MipmapChain.LevelCount(2, 2));
        // Rock textures must have more than one level (the P529 anti-alias fix).
        Assert.True(MipmapChain.LevelCount(256, 256) > 1);
        Assert.True(MipmapChain.LevelCount(2048, 2048) > 1);
    }

    [Fact]
    public void MipmapChain_Downsample_BoxFiltersFourPixelsIntoOne()
    {
        // 2x2 with four distinct colours; the 1x1 level is their average.
        var src = new[]
        {
            new Rgba32(10, 20, 30, 255), new Rgba32(30, 20, 30, 255),
            new Rgba32(10, 40, 30, 255), new Rgba32(30, 40, 30, 255),
        };
        var dst = MipmapChain.Downsample(src, 2, 2);
        Assert.Single(dst);
        Assert.Equal(new Rgba32(20, 30, 30, 255), dst[0]);   // (10+30)/2, (20+40)/2, 30, 255
    }

    [Fact]
    public void MipmapChain_Downsample_ReachesOneByOne()
    {
        // Repeat downsampling a 4x4 plane until 1x1, counting levels.
        var plane = new Rgba32[16];
        for (int i = 0; i < plane.Length; i++) plane[i] = new Rgba32((byte)i, 255, 255, 255);
        int w = 4, h = 4, levels = 0;
        while (w > 1 || h > 1)
        {
            plane = MipmapChain.Downsample(plane, w, h);
            MipmapChain.NextLevelSize(w, h, out w, out h);
            levels++;
        }
        Assert.Equal(2, levels);   // 4x4 -> 2x2 -> 1x1
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Single(plane);
    }
}
