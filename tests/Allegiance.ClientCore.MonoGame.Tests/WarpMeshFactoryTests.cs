using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpMeshFactory"/> and
/// <see cref="WarpVortex"/> — no graphics device involved (the same
/// discipline as <see cref="RockMeshFactoryTests"/>). They pin the invariants
/// that make the P562 double-horn TEXTURED vortex safe to feed into a
/// VertexBuffer/IndexBuffer, and that the ported native rules (brightness
/// band, animated UVs, LOD tiers, star rotation, swell curve) match the
/// original <c>alephgeo.cpp</c> formulas.
/// </summary>
public class WarpMeshFactoryTests
{
    private const int CountDepth = 3;    // → 2·3+1 = 7 axial rings
    private const int CountSection = 8;  // → 9 vertices per ring

    [Fact]
    public void DefaultParams_ProduceARealMesh()
    {
        var mesh = WarpMeshFactory.Generate();
        Assert.True(mesh.VertexCount >= 400, $"expected a real mesh, got {mesh.VertexCount} vertices");
        Assert.True(mesh.TriangleCount >= 750, $"expected a real mesh, got {mesh.TriangleCount} triangles");
        Assert.Equal(mesh.VertexCount * 3, mesh.Positions.Length);
        Assert.Equal(mesh.VertexCount * 4, mesh.Colors.Length);
        Assert.Equal(mesh.VertexCount * 2, mesh.Uvs.Length);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var mesh = WarpMeshFactory.Generate();
        foreach (int idx in mesh.Indices)
            Assert.InRange(idx, 0, mesh.VertexCount - 1);
    }

    [Fact]
    public void SameParams_TwoCalls_ByteIdentical()
    {
        var a = WarpMeshFactory.Generate();
        var b = WarpMeshFactory.Generate();
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Colors, b.Colors);
        Assert.Equal(a.Uvs, b.Uvs);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void DifferentTaper_DifferentShape()
    {
        var blunt = WarpMeshFactory.Generate(taper: 1.0f);
        var tight = WarpMeshFactory.Generate(taper: 4.0f);
        Assert.NotEqual(blunt.Positions, tight.Positions);
    }

    [Fact]
    public void Hourglass_BothEndsWideCenterPinched_AlongForwardAxis()
    {
        var mesh = WarpMeshFactory.Generate(CountDepth, CountSection, rimRadius: 1f, depth: 2f);
        int ringCount = 2 * CountDepth + 1;
        int sections = CountSection + 1;

        float rim0Max = MaxRadialDistance(mesh.Positions, 0, sections);
        float rim1Max = MaxRadialDistance(mesh.Positions, sections * (ringCount - 1), sections);
        Assert.InRange(rim0Max, 0.980f, 1.0201f);   // end 0 is a wide rim (radius ≈ rimRadius)
        Assert.InRange(rim1Max, 0.980f, 1.0201f);   // end 1 is an EQUALLY wide rim
        Assert.True(MathF.Abs(rim0Max - rim1Max) < 1e-3f, "both rims must be equally wide");

        // Middle ring = the pinch, far closer to the axis.
        int center = ringCount / 2;
        float centerMax = MaxRadialDistance(mesh.Positions, center * sections, sections);
        Assert.True(centerMax < rim0Max * 0.3f,
            $"center ring must be far narrower than either rim (rim {rim0Max:F3}, center {centerMax:F3})");

        // Symmetry about the center: rims flank the origin, middle hugs z=0.
        float z0 = mesh.Positions[2];
        float z1 = mesh.Positions[(sections * (ringCount - 1)) * 3 + 2];
        Assert.True(z0 < 0f && z1 > 0f, $"rims must flank the origin (z0 {z0:F3}, z1 {z1:F3})");
        Assert.True(MathF.Abs(z0) - MathF.Abs(z1) is < 1e-3f and > -1e-3f, "rims must be equidistant from center");
        float zMid = mesh.Positions[(center * sections) * 3 + 2];
        Assert.True(MathF.Abs(zMid) < MathF.Abs(z0) * 0.3f, $"middle must sit near z=0 (got {zMid:F3})");
    }

    [Fact]
    public void BrightnessBand_DarkPinchAndRim_BrightMidBand()
    {
        var mesh = WarpMeshFactory.Generate(CountDepth, CountSection);
        int ringCount = 2 * CountDepth + 1;
        int sections = CountSection + 1;

        float rimLuma = AverageLuminance(mesh.Colors, 0, sections);
        int pinch = ringCount / 2;
        float pinchLuma = AverageLuminance(mesh.Colors, pinch * sections, sections);
        // Mid-band rings (r≈0.5 each side of the pinch) carry the bright ring.
        int loMid = ringCount / 4;
        int hiMid = ringCount - 1 - ringCount / 4;
        float midLuma = (AverageLuminance(mesh.Colors, loMid * sections, sections)
                       + AverageLuminance(mesh.Colors, hiMid * sections, sections)) / 2f;

        // Native 2r(1-r): ~0 at pinch and rim, peaks (0.5) on the mid band.
        Assert.True(rimLuma < 0.021f, $"rim must be dark (got {rimLuma:F3})");
        Assert.True(pinchLuma < 0.060f, $"pinch must be dark (got {pinchLuma:F3})");
        Assert.True(midLuma > rimLuma * 4f && midLuma > 0.30f,
            $"mid band must be the bright ring (rim {rimLuma:F3}, mid {midLuma:F3})");
        Assert.True(midLuma > pinchLuma * 4f, $"mid band must be brighter than the pinch ({pinchLuma:F3} vs {midLuma:F3})");

        // Grayscale (r==g==b) — native VertexL sets all three to `bright`.
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Assert.Equal(mesh.Colors[i * 4 + 0], mesh.Colors[i * 4 + 1]);
            Assert.Equal(mesh.Colors[i * 4 + 0], mesh.Colors[i * 4 + 2]);
            Assert.Equal(1f, mesh.Colors[i * 4 + 3]);
        }
    }

    // ── Native rule fidelity (alephgeo.cpp) ────────────────────────────────

    [Fact]
    public void BrightnessFormula_MatchesNative2rMinusRsquared()
    {
        // alephgeo.cpp:82  bright = 2·radius·(1−radius)
        Assert.Equal(0f, WarpMeshFactory.Brightness(0f), 3);
        Assert.Equal(0f, WarpMeshFactory.Brightness(1f), 3);
        Assert.Equal(0.5f, WarpMeshFactory.Brightness(0.5f), 3);
        Assert.Equal(2f * 0.25f * 0.75f, WarpMeshFactory.Brightness(0.25f), 5);
        Assert.Equal(0f, WarpMeshFactory.Brightness(0f));
    }

    [Fact]
    public void TwistUFormula_MatchesNativeSpiralTwist()
    {
        // alephgeo.cpp:134,137  u = 4·section/countSection + 5·(1−(1−r)²)
        const int cs = 32;
        // r=0 (pinch): the +5·(…) twist term is 0.
        Assert.Equal(4f * 0 / cs, WarpMeshFactory.TwistU(0, cs, 0f), 5);
        // r=1 (rim): twist term = 5·(1−0)=5.
        Assert.Equal(4f * 0 / cs + 5f, WarpMeshFactory.TwistU(0, cs, 1f), 5);
        // section 8 of 32 → 4·8/32 = 1; r=0.5 → 5·(1−0.25)=3.75.
        Assert.Equal(4f * 8 / cs + 5f * (1f - MathF.Pow(1f - 0.5f, 2f)),
            WarpMeshFactory.TwistU(8, cs, 0.5f), 5);
        // Section wraps the tile 4× around the circumference (u spans ~0..9).
        Assert.True(WarpMeshFactory.TwistU(cs - 1, cs, 1f) > 4f, "the 4× circumference tiling must be present");
    }

    [Fact]
    public void FlowVFormula_MatchesNativeInwardFlow()
    {
        // alephgeo.cpp:128,133  v = mod(time/10,1) + r
        Assert.Equal(0f + 0.5f, WarpMeshFactory.FlowV(0f, 0.5f), 5);
        Assert.Equal(0.5f + 0.5f, WarpMeshFactory.FlowV(5f, 0.5f), 5);   // mod(0.5,1)=0.5
        Assert.Equal(0f + 0.5f, WarpMeshFactory.FlowV(10f, 0.5f), 5);    // mod(1,1)=0 (wraps)
        Assert.Equal(0.353f + 0.782f, WarpMeshFactory.FlowV(3.532f, 0.782f), 3);
        // Period 10 s.
        Assert.Equal(WarpMeshFactory.FlowV(2.345f, 0.456f), WarpMeshFactory.FlowV(12.345f, 0.456f), 5);
    }

    [Fact]
    public void MeshUvs_AreNativeBaseUvAtTimeZero()
    {
        // A vertex at section j, ring i must carry u = TwistU(j, cs, r) and
        // v = r (= FlowV(0, r)), so the animated renderer only adds the flow.
        var mesh = WarpMeshFactory.Generate(CountDepth, CountSection);
        int sections = CountSection + 1;
        int ring = CountDepth;                 // the pinch ring (r = 0)
        int vi = ring * sections;              // section 0
        Assert.Equal(0f, mesh.Uvs[vi * 2 + 1], 5);            // v = r = 0 at pinch
        Assert.Equal(0f, mesh.Uvs[vi * 2 + 0], 5);            // u = 0 at section 0, r=0

        int rimRing = 0;                                       // r = 1 (a rim)
        int vr = rimRing * sections + CountSection;            // last section (duplicated seam)
        Assert.Equal(1f, mesh.Uvs[vr * 2 + 1], 5);             // v = r = 1 at the rim
        Assert.Equal(WarpMeshFactory.TwistU(CountSection, CountSection, 1f),
            mesh.Uvs[vr * 2 + 0], 5);
    }

    [Fact]
    public void LodTiers_MatchNativeDepthSectionPairs()
    {
        // alephgeo.cpp:239-251  CreateAlephGeo: (8,32),(4,16),(2,16),(2,8)
        Assert.Equal(4, WarpMeshFactory.LodTiers.Length);
        Assert.Equal((8, 32), WarpMeshFactory.LodTiers[0]);
        Assert.Equal((4, 16), WarpMeshFactory.LodTiers[1]);
        Assert.Equal((2, 16), WarpMeshFactory.LodTiers[2]);
        Assert.Equal((2, 8), WarpMeshFactory.LodTiers[3]);
        // Coarser tiers must have strictly fewer vertices (a real LOD).
        int prev = int.MaxValue;
        foreach (var (depth, section) in WarpMeshFactory.LodTiers)
        {
            int vcount = (2 * depth + 1) * (section + 1);
            Assert.True(vcount < prev, $"LOD tiers must strictly decrease in size ({vcount} >= {prev})");
            prev = vcount;
        }
    }

    // ── WarpVortex: star core + transit swell (alephgeo.cpp:158-208) ───────

    [Fact]
    public void StarRotationRates_CounterRotateAtNativeMagnitudes()
    {
        // alephgeo.cpp:197 (+0.25) and :207 (−0.375)
        Assert.Equal(0.25f, WarpVortex.StarRateA, 5);
        Assert.Equal(-0.375f, WarpVortex.StarRateB, 5);
        Assert.True(WarpVortex.StarRateA > 0f && WarpVortex.StarRateB < 0f,
            "the two star decals must counter-rotate");
        Assert.Equal(0.1f, WarpVortex.StarBaseScaleFraction, 5);   // alephgeo.cpp:160
    }

    [Fact]
    public void StarScale_IdleIsBaseScale_AfterSwellDecays()
    {
        // Far after the swell (now > explodeTime + 0.25) the star returns to
        // its resting 0.1 scale.
        Assert.Equal(0.1f, WarpVortex.StarScale(now: 200f, triggerTime: 0f, explodeDuration: 1.2f), 5);
        // Exactly at the trigger instant the swell has barely begun (scale ≈ 0.1).
        Assert.Equal(0.1f, WarpVortex.StarScale(now: 0f, triggerTime: 0f, explodeDuration: 1.2f), 4);
        // Just after the post-explosion decay window it is back to base.
        Assert.Equal(0.1f, WarpVortex.StarScale(now: 1.2f + 0.30f, triggerTime: 0f, explodeDuration: 1.2f), 4);
    }

    [Fact]
    public void StarScale_SwellPeaksAboveSixTimes()
    {
        // At the swell climax (now == explodeTime, progress 1) the star swells
        // to ≫ the base 0.1 — native's "×6" (plus the resonant +0.8 term).
        float peak = WarpVortex.StarScale(now: 1.2f, triggerTime: 0f, explodeDuration: 1.2f);
        Assert.True(peak > 6f, $"swell peak must exceed 6×, got {peak:F3}");
        Assert.True(peak > WarpVortex.StarBaseScaleFraction * 6f, "swell must clear the native ×6 floor");
    }

    [Fact]
    public void StarScale_ProducesEightResonantPulses()
    {
        // Native's fCosine = |sin(π·(numPulses+0.5)·progress)| (alephgeo.cpp:177)
        // imprints 8 distinct bumps across the swell. Count the local maxima.
        const float dur = 1.2f;
        const int samples = 960;
        var vals = new float[samples + 1];
        for (int i = 0; i <= samples; i++)
            vals[i] = WarpVortex.StarScale(now: dur * i / samples, triggerTime: 0f, explodeDuration: dur);

        int pulses = 0;
        for (int i = 1; i < samples; i++)
            if (vals[i] > vals[i - 1] && vals[i] > vals[i + 1])
                pulses++;
        Assert.True(pulses >= 7 && pulses <= 9, $"expected ~8 resonant pulses, got {pulses}");
    }

    // ── Shared helpers ─────────────────────────────────────────────────────

    [Fact]
    public void VisualScaleMultiplier_CalibratesWarpToAboutTwoAndHalfTimesRenderedShipSize()
    {
        const float warpRealRadius = 100f;            // m2xspiderw.igc every warp (measured live)
        const float renderedShipLongestDim = 190.065f; // bios_scout_upscaled.fbx bounds (measured live)
        float warpVisualDiameter = 2f * warpRealRadius * WarpRenderer.VisualScaleMultiplier;
        float ratio = warpVisualDiameter / renderedShipLongestDim;
        Assert.InRange(ratio, 2.5f, 3.0f);
    }

    private static float MaxRadialDistance(float[] pos, int startVertex, int count)
    {
        float max = 0f;
        for (int i = 0; i < count; i++)
        {
            int o = (startVertex + i) * 3;
            float r = MathF.Sqrt(pos[o] * pos[o] + pos[o + 1] * pos[o + 1]);
            max = MathF.Max(max, r);
        }
        return max;
    }

    private static float AverageLuminance(float[] col, int startVertex, int count)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            int o = (startVertex + i) * 4;
            sum += 0.2126f * col[o] + 0.7152f * col[o + 1] + 0.0722f * col[o + 2];
        }
        return sum / count;
    }
}
