using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P589 — the pure, headless-testable per-sector space-object placement:
/// moons, distant galaxies/nebulae cut-outs, the deep-field galaxy field and
/// the Little Red Dots, all seeded deterministically from the cluster id, and
/// the native lens-flare element table. These are brand-new modules (no
/// pre-P589 code existed), so the tests pin the behaviour they introduce —
/// the determinism contract, the native table exactness and the shape/colour
/// ranges — and would fail if the placement were made random or non-reproducible.
/// </summary>
public class SpaceObjectMathTests
{
    // ---- determinism contract -----------------------------------------

    [Fact]
    public void MoonCount_IsZeroToOneOrTwo_ForManyClusters()
    {
        for (short c = 1; c <= 200; c++)
        {
            int n = SpaceObjectMath.MoonCount(c);
            Assert.InRange(n, 0, 2);
        }
    }

    [Fact]
    public void MoonPlacement_IsDeterministic_ForSameCluster()
    {
        var planet = new NativeSkyMath.Vector3Unit(0.866f, 0.5f, 0f);
        var a = SpaceObjectMath.PlaceMoon(7, 0, planet, 3f, 3);
        var b = SpaceObjectMath.PlaceMoon(7, 0, planet, 3f, 3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void MoonPlacement_DiffersAcrossClusters()
    {
        var planet = new NativeSkyMath.Vector3Unit(0.866f, 0.5f, 0f);
        var a = SpaceObjectMath.PlaceMoon(11, 0, planet, 3f, 3);
        var b = SpaceObjectMath.PlaceMoon(12, 0, planet, 3f, 3);
        Assert.NotEqual(a.Direction, b.Direction);
    }

    [Fact]
    public void Moon_IsSmallerThanPlanet_AndValidDirection()
    {
        var planet = new NativeSkyMath.Vector3Unit(0, 0, 1);
        for (int i = 0; i < 20; i++)
        {
            var m = SpaceObjectMath.PlaceMoon((short)i, 0, planet, 3f, 3);
            Assert.True(m.AngularHalfDeg < 3f, $"moon should be smaller than the 3° planet, got {m.AngularHalfDeg}");
            float len = MathF.Sqrt(m.Direction.X * m.Direction.X + m.Direction.Y * m.Direction.Y + m.Direction.Z * m.Direction.Z);
            Assert.True(MathF.Abs(len - 1f) < 1e-3f, "moon direction must be a unit vector");
        }
    }

    [Fact]
    public void MoonIllumination_IsUnitLitTowardLight_AndDarkAway()
    {
        var light = new NativeSkyMath.Vector3Unit(1, 0, 0);
        // Fully day-side when the moon faces the light.
        float lit = SpaceObjectMath.MoonIllumination(new NativeSkyMath.Vector3Unit(1, 0, 0), light);
        Assert.Equal(1.0, (double)lit, 3);
        // Night side when opposite → clamped to the ambient floor (0.25 default).
        float dark = SpaceObjectMath.MoonIllumination(new NativeSkyMath.Vector3Unit(-1, 0, 0), light);
        Assert.Equal(0.25, (double)dark, 3);
        Assert.InRange(dark, 0f, 1f);
    }

    // ---- distant objects ---------------------------------------------

    [Fact]
    public void DistantCount_RespectsRangeAndAvailability()
    {
        for (int avail = 1; avail <= 6; avail++)
        {
            for (short c = 1; c <= 50; c++)
            {
                int n = SpaceObjectMath.DistantCount(c, avail);
                Assert.InRange(n, Math.Min(2, avail), Math.Min(4, avail));
            }
        }
        Assert.Equal(0, SpaceObjectMath.DistantCount(3, 0));
    }

    [Fact]
    public void PlaceDistant_IsDeterministic()
    {
        var a = SpaceObjectMath.PlaceDistant(21, 0, 8);
        var b = SpaceObjectMath.PlaceDistant(21, 0, 8);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PlaceDistant_IsPlanetLikeAndDim()
    {
        for (short c = 1; c <= 30; c++)
        {
            var o = SpaceObjectMath.PlaceDistant(c, 0, 8);
            Assert.InRange(o.AngularHalfDeg, 0.5f, 4f);   // planet-like (native ~2.5-5°)
            Assert.InRange(o.SpriteIndex, 0, 7);
            Assert.InRange(o.Brightness, 0f, 1f);
        }
    }

    // ---- deep-field galaxy field -------------------------------------

    [Fact]
    public void PlaceGalaxies_ReturnsRequestedCount_DimAndLogSized()
    {
        var g = SpaceObjectMath.PlaceGalaxies(9, 400, 50);
        Assert.Equal(400, g.Length);
        foreach (var x in g)
        {
            Assert.InRange(x.SpriteIndex, 0, 49);
            Assert.InRange(x.Brightness, 0f, 1f);
            Assert.True(x.AngularHalfDeg > 0f && x.AngularHalfDeg <= 1.5f, "galaxies stay well below planet size");
        }
    }

    [Fact]
    public void PlaceGalaxies_IsDeterministic()
    {
        var a = SpaceObjectMath.PlaceGalaxies(9, 100, 20);
        var b = SpaceObjectMath.PlaceGalaxies(9, 100, 20);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PlaceGalaxies_DiffersAcrossClusters()
    {
        var a = SpaceObjectMath.PlaceGalaxies(9, 100, 20);
        var b = SpaceObjectMath.PlaceGalaxies(10, 100, 20);
        // Very likely at least one of the first few directions differs.
        bool anyDifferent = false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) { anyDifferent = true; break; }
        Assert.True(anyDifferent, "two different clusters should not get identical galaxy fields");
    }

    // ---- Little Red Dots ---------------------------------------------

    [Fact]
    public void LittleRedDots_AreTinyRedAndDeterministic()
    {
        var lrds = SpaceObjectMath.PlaceLittleRedDots(5, 40);
        Assert.Equal(40, lrds.Length);
        for (int i = 0; i < lrds.Length; i++)
        {
            Assert.InRange(lrds[i].AngularHalfDeg, 0.005f, 0.04f); // ≈1-4 px at 2560
            // Hue stays in the red band (deep red through orange-red).
            Assert.InRange(lrds[i].Hue, 0.955f, 1.06f);
            Assert.InRange(lrds[i].Brightness, 0f, 1f);
        }
        var again = SpaceObjectMath.PlaceLittleRedDots(5, 40);
        Assert.Equal(lrds, again);
    }

    // ---- native lens-flare element table -----------------------------

    [Fact]
    public void LensFlareTable_MatchesNativeEightElements()
    {
        // Exact native g_lensFlareData (effect/efimage.cpp:601-613).
        var t = LensFlareElements.Table;
        Assert.Equal(8, t.Length);
        Assert.Equal(new LensFlareElements.Element(2, -1.00f, 1.50f, true), t[0]);
        Assert.Equal(new LensFlareElements.Element(0, -0.75f, 1.00f, false), t[1]);
        Assert.Equal(new LensFlareElements.Element(0, -0.50f, 0.50f, false), t[2]);
        Assert.Equal(new LensFlareElements.Element(0, -0.25f, 0.25f, false), t[3]);
        Assert.Equal(new LensFlareElements.Element(0, 0.25f, 0.25f, false), t[4]);
        Assert.Equal(new LensFlareElements.Element(0, 0.50f, 0.50f, false), t[5]);
        Assert.Equal(new LensFlareElements.Element(0, 0.75f, 1.00f, false), t[6]);
        Assert.Equal(new LensFlareElements.Element(1, 1.00f, 1.50f, true), t[7]);
    }

    [Fact]
    public void LensFlareSurfaceArt_MatchesNativeArtNames()
    {
        // effect/efart.h:8-10 — MID_RING f104bmp, STAR_AURA f105bmp, END_RING f106bmp.
        Assert.Equal("f104bmp", LensFlareElements.SurfaceArt[0]);
        Assert.Equal("f105bmp", LensFlareElements.SurfaceArt[1]);
        Assert.Equal("f106bmp", LensFlareElements.SurfaceArt[2]);
    }
}
