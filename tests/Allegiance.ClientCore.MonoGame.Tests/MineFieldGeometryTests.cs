using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MineFieldGeometry"/> (P291) — no graphics
/// device involved. They pin the two things that make the mine decal field
/// faithful to native's <c>MineFieldGeo</c> (<c>src/effect/efgeo.cpp:15-87</c>):
/// the exact decal count (<c>1 + int(strength·radius²/100)</c>, strength 1.0) and
/// a uniform <b>within-the-sphere</b> distribution, plus the determinism that
/// keeps a given mine frame-stable and the tests reproducible.
/// </summary>
public class MineFieldGeometryTests
{
    [Theory]
    [InlineData(0f, 1)]
    [InlineData(10f, 2)]      // 10²/100 = 1 → 1+1 = 2
    [InlineData(50f, 26)]     // 2500/100 = 25 → 26
    [InlineData(100f, 101)]   // 10000/100 = 100 → 101
    [InlineData(200f, 401)]   // 40000/100 = 400 → 401
    public void OffsetCount_MatchesNativeGetCount(float radius, int expected)
    {
        // Native: GetCount(strength=1.0, radius) = 1 + int(radius²/100)
        // (efgeo.cpp:21-24, mineigc.cpp:103 passes strength 1.0).
        var actual = MineFieldGeometry.OffsetCount(radius);
        var reference = 1 + (int)(radius * radius / 100.0f);
        Assert.Equal(expected, actual);
        Assert.Equal(reference, actual);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalOffsets()
    {
        var a = new Vector3[100];
        var b = new Vector3[100];
        MineFieldGeometry.SampleUniformInSphere(123f, seed: 7, a);
        MineFieldGeometry.SampleUniformInSphere(123f, seed: 7, b);
        for (int i = 0; i < 100; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentClouds()
    {
        var a = new Vector3[50];
        var b = new Vector3[50];
        MineFieldGeometry.SampleUniformInSphere(100f, seed: 1, a);
        MineFieldGeometry.SampleUniformInSphere(100f, seed: 2, b);
        Assert.True(a.Zip(b, (x, y) => (x - y).Length()).Any(d => d > 0.001f),
            "different mines must render different clouds");
    }

    [Fact]
    public void AllOffsetsLieWithinTheSphere()
    {
        var offsets = new Vector3[MineFieldGeometry.OffsetCount(180f)];
        MineFieldGeometry.SampleUniformInSphere(180f, seed: 42, offsets);
        Assert.All(offsets, o => Assert.True(o.Length() <= 180f + 0.001f,
            $"offset {o} escapes the {180f}-radius sphere"));
    }

    [Fact]
    public void DistributionFillsTheInterior_NotJustTheSurface()
    {
        // Uniform-in-ball must place some decals well inside (near the centre)
        // and some near the rim — not all at |offset| ≈ radius (surface-only).
        var offsets = new Vector3[MineFieldGeometry.OffsetCount(250f)];
        MineFieldGeometry.SampleUniformInSphere(250f, seed: 991, offsets);
        var mags = offsets.Select(o => o.Length()).OrderBy(m => m).ToList();

        double innerFrac = mags.Count(m => m < 125f) / (double)mags.Count; // inside the half-radius ball
        double nearRim = mags.Count(m => m > 230f) / (double)mags.Count;
        // Uniform-in-ball: P(r<R/2) = (1/2)^3 = 12.5%. Allow a generous band.
        Assert.True(innerFrac > 0.055 && innerFrac < 0.30,
            $"interior fraction {innerFrac:P0} outside expected ~12.5% band");
        Assert.True(nearRim > 0.005, $"expected at least some near-rim decals, got {nearRim:P0}");
    }
}
