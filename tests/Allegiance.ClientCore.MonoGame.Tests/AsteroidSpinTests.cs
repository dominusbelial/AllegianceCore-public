using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="AsteroidSpin"/> (P252 client-side asteroid
/// rotation) — no graphics device involved. They pin the two behaviours that
/// make the client-side rotation faithful to native's
/// <c>o.PostRotate(axis, angle*dt)</c> (<c>modelIGC.cpp:85-99</c>):
/// accumulation of real elapsed time at the wire's real per-second rate, and
/// per-instance independence so different asteroids do NOT spin in lockstep.
/// Plus one real-data test against <c>m2xspiderw.igc</c> proving the shipped
/// map genuinely gives every asteroid a distinct non-zero rate on a
/// non-degenerate axis (so no arbitrary fallback is ever exercised live).
/// </summary>
public class AsteroidSpinTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Fact]
    public void FromWire_KeepsWireRateAndNormalizesAxis()
    {
        // axis(2,2,2) normalized to unit, rate 0.0628 rad/s
        var spin = AsteroidSpin.FromWire([2f, 2f, 2f, 0.0628f]);

        Assert.Equal(0.0628f, spin.Rate);
        Assert.Equal(1f, spin.Axis.Length(), 4);
        Assert.Equal(2f / MathF.Sqrt(12), spin.Axis.X, 4);
    }

    [Fact]
    public void Advance_AccumulatesRateTimesElapsed()
    {
        var spin = AsteroidSpin.FromWire([0f, 1f, 0f, 0.5f]); // 0.5 rad/s
        Assert.Equal(0f, spin.Angle);

        spin.Advance(0.5f); // 0.25 rad
        spin.Advance(0.5f); // +0.25 rad
        Assert.Equal(0.5f, spin.Angle, 4);
    }

    [Fact]
    public void TwoSpinsWithDifferentRates_DivergeNotLockstep()
    {
        // Real m2xspiderw data gives every rock a distinct rate; two instances
        // with different wire rates must accumulate different angles over the
        // same elapsed time (the "not all spinning in lockstep" requirement).
        var slow = AsteroidSpin.FromWire([0f, 1f, 0f, 0.020f]);
        var fast = AsteroidSpin.FromWire([1f, 0f, 0f, 0.180f]);

        for (int i = 0; i < 100; i++) { slow.Advance(0.033f); fast.Advance(0.033f); }

        Assert.NotEqual(slow.Angle, fast.Angle);
        Assert.True(fast.Angle > slow.Angle * 5f,
            $"fast ({fast.Angle}) should outpace slow ({slow.Angle}) by their rate ratio");
    }

    [Fact]
    public void DegenerateZeroAxis_FallsBackToDefaultAxis()
    {
        // A zero-length axis would make an axis-angle rotation undefined; the
        // port's own normalise returns the zero vector for it, so the class
        // substitutes a fixed axis (defensive; real data never hits it).
        var spin = AsteroidSpin.FromWire([0f, 0f, 0f, 0.1f]);

        Assert.Equal(1f, spin.Axis.Length(), 4);
        Assert.Equal(0f, spin.Axis.X, 4);
        Assert.Equal(1f, spin.Axis.Y, 4);
        Assert.Equal(0f, spin.Axis.Z, 4);
        Assert.Equal(0.1f, spin.Rate);
    }

    [Fact]
    public void MalformedWire_IsStationary()
    {
        Assert.Equal(0f, AsteroidSpin.FromWire(null).Rate);
        Assert.Equal(0f, AsteroidSpin.FromWire([1f, 0f, 0f]).Rate); // too short
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealM2XSpiderwData_HasDistinctNonZeroRatesOnValidAxes()
    {
        Require.Artwork();
        // P252 real-data proof: every asteroid in the shipped demo map spins at
        // its OWN non-zero rate on a non-degenerate axis — so the client must
        // NOT apply a single shared speed, and the defensive fallback is never
        // exercised by real data.
        var map = MissionMap.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "m2xspiderw.igc")));

        Assert.NotEmpty(map.Asteroids);
        var rates = map.Asteroids.Select(a => a.Rotation[3]).ToList();
        var axes = map.Asteroids.Select(a =>
            new Vector3(a.Rotation[0], a.Rotation[1], a.Rotation[2]).Length()).ToList();

        Assert.True(rates.All(r => r > 0f), "every real asteroid must have a non-zero rotation rate");
        Assert.True(rates.Distinct().Count() > 1,
            $"asteroids must not be in lockstep; got {rates.Distinct().Count()} distinct rates of {rates.Count}");
        Assert.True(axes.All(x => x > 0.5f),
            "every real asteroid must have a non-degenerate rotation axis");
    }
}
