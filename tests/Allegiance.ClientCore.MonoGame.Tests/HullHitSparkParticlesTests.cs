using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P364 projectile-impact spark pool
/// (<see cref="HullHitSparkParticles"/>) — a faithful port of native
/// <c>ThingGeo::AddHullHit</c>'s debris-bits half (effect/thinggeo.cpp:1324-1345,
/// BitsGeo efgeo.cpp:798-846). These exercise the pool's own mechanics — exactly
/// two bits per hit, native sizing (<c>radius/2</c>), native launch velocity
/// (<c>0.4·size·(normal + random ±0.25 per axis)</c>), the 2&#8209;s bit lifetime,
/// straight-line constant-velocity position integration, ageing/expiry and reset —
/// with a deterministic random stub, and never touch a graphics device (the pool
/// is MonoGame-free).
/// </summary>
public class HullHitSparkParticlesTests
{
    /// <summary>Deterministic <see cref="ITrailRandom"/>: Range returns a fixed
    /// interpolation between min/max (this pool uses only Range — the bits do not
    /// call Direction).</summary>
    private sealed class FixedRandom : ITrailRandom
    {
        public float T = 0.5f;
        public float Range(float min, float max) => min + T * (max - min);
        public SimVec Direction() => new(1f, 0f, 0f);
    }

    private const float RADIUS = 50f;
    private static readonly SimVec Impact = new(1000f, 2000f, 3000f);
    private static readonly SimVec Normal = new(1f, 0f, 0f);

    [Fact]
    public void Emit_spawns_exactly_two_bits_per_hit()
    {
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom());

        Assert.Equal(HullHitSparkParticles.BitsPerHit, pool.ActiveCount);
        Assert.All(pool.Enumerate(), v => Assert.Equal(1f, v.RemainingFrac, 5));
    }

    [Fact]
    public void Bit_size_is_radius_over_two()
    {
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom());

        Assert.All(pool.Enumerate(), v => Assert.Equal(RADIUS / 2f, v.Size, 5));   // thinggeo.cpp:1328
    }

    [Fact]
    public void Bit_velocity_is_speed_times_normal_plus_zero_spread_at_midpoint()
    {
        // size = 25, speed = 0.4·25 = 10. With T=0.5 the per-axis random(∓0.25)
        // spread is 0, so each bit flies exactly at 10·normal = (10, 0, 0).
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom { T = 0.5f });

        Assert.All(pool.Enumerate(), v => Assert.Equal(10f, v.Velocity.X, 5));
        Assert.All(pool.Enumerate(), v => Assert.Equal(0f, v.Velocity.Y, 5));
        Assert.All(pool.Enumerate(), v => Assert.Equal(0f, v.Velocity.Z, 5));
    }

    [Fact]
    public void Bit_velocity_applies_the_per_axis_spread_bounds()
    {
        // spread extremes: T=0 → normal + (−0.25,−0.25,−0.25) = (0.75,−0.25,−0.25),
        // scaled by speed 10 → (7.5, −2.5, −2.5); T=1 → (12.5, 2.5, 2.5).
        var poolLow = new HullHitSparkParticles();
        poolLow.Emit(Impact, Normal, RADIUS, new FixedRandom { T = 0.0f });
        Assert.All(poolLow.Enumerate(), v =>
        {
            Assert.Equal(7.5f, v.Velocity.X, 5);
            Assert.Equal(-2.5f, v.Velocity.Y, 5);
            Assert.Equal(-2.5f, v.Velocity.Z, 5);
        });

        var poolHigh = new HullHitSparkParticles();
        poolHigh.Emit(Impact, Normal, RADIUS, new FixedRandom { T = 1.0f });
        Assert.All(poolHigh.Enumerate(), v =>
        {
            Assert.Equal(12.5f, v.Velocity.X, 5);
            Assert.Equal(2.5f, v.Velocity.Y, 5);
            Assert.Equal(2.5f, v.Velocity.Z, 5);
        });
    }

    [Fact]
    public void Bits_expire_after_the_native_two_second_lifetime()
    {
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom());
        Assert.Equal(2, pool.ActiveCount);

        // Advance well past g_bitsLifeTime = 2s; everything must expire.
        for (int i = 0; i < 200 && pool.ActiveCount > 0; i++)
            pool.Update(0.020f);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Bits_integrate_position_linearly_at_constant_velocity()
    {
        // Native BitsGeo renders position = m_vecPosition + m_dvecPosition·dtime
        // (efgeo.cpp:836) — straight-line, no acceleration. With speed 10 along
        // +X, after t seconds each bit has advanced exactly 10·t in X.
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom { T = 0.5f });

        const float t = 0.040f;
        pool.Update(t);
        Assert.All(pool.Enumerate(), v => Assert.Equal(Impact.X + 10f * t, v.Position.X, 1e-3f));
    }

    [Fact]
    public void Emit_across_multiple_hits_shares_the_pool()
    {
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom());
        pool.Emit(new SimVec(2000f, 0f, 0f), Normal, RADIUS, new FixedRandom());
        Assert.Equal(4, pool.ActiveCount);

        pool.Update(0.050f);   // neither hit's bits expire yet (< 2s)
        Assert.Equal(4, pool.ActiveCount);
    }

    [Fact]
    public void Reset_clears_every_bit()
    {
        var pool = new HullHitSparkParticles();
        pool.Emit(Impact, Normal, RADIUS, new FixedRandom());
        Assert.Equal(2, pool.ActiveCount);

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }
}
