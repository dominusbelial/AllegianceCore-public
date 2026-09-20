using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P297 damage-debris smoke pool
/// (<see cref="DamageDebrisParticles"/>) — a faithful port of native
/// <c>AddDamageParticles</c> (effect/thinggeo.cpp:699-755). These exercise the
/// pool's own mechanics — one puff per damage vent, native sizing/duration,
/// ship-velocity inheritance, the −⅓·velocity drag, closed-form position
/// integration, ageing/expiry and reset — with a deterministic random stub, and
/// never touch a graphics device (the pool is MonoGame-free).
/// </summary>
public class DamageDebrisParticlesTests
{
    /// <summary>Deterministic <see cref="ITrailRandom"/>: Range returns a fixed
    /// interpolation between min/max, Direction returns a fixed unit vector.</summary>
    private sealed class FixedRandom : ITrailRandom
    {
        public float T = 0.5f;
        public SimVec Dir = new(1f, 0f, 0f);
        public float Range(float min, float max) => min + T * (max - min);
        public SimVec Direction() => Dir;
    }

    private static readonly SimVec Origin = new(1000f, 2000f, 3000f);
    private const float RADIUS = 50f;
    private static readonly Orientation Orient = Orientation.Identity;

    [Fact]
    public void Emit_spawns_exactly_one_puff_per_damage_vent()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 7, 5, 0.016f, rng);
        Assert.Equal(5, pool.ActiveCount);   // 5 active vents → 5 puffs
        Assert.All(pool.Enumerate(), v => Assert.Equal(1f, v.RemainingFrac, 5));
    }

    [Fact]
    public void Emit_with_no_damage_vents_spawns_nothing()
    {
        var pool = new DamageDebrisParticles();
        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 3, 0, 0.016f, new FixedRandom());
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }

    [Fact]
    public void Puff_size_is_native_random_1_5_to_2_5()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom { T = 0.0f };   // min sizes: 1.5
        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 1, 4, 0.016f, rng);
        Assert.All(pool.Enumerate(), v => Assert.Equal(DamageDebrisParticles.EffectSizeMin, v.Size, 5));

        pool.Reset();
        rng.T = 1.0f;                              // max sizes: 2.5
        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 1, 4, 0.016f, rng);
        Assert.All(pool.Enumerate(), v => Assert.Equal(DamageDebrisParticles.EffectSizeMax, v.Size, 5));
    }

    [Fact]
    public void Puffs_inherit_the_ships_velocity()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();
        var v1 = new SimVec(100f, 0f, 0f);
        var v2 = new SimVec(200f, 50f, 0f);

        // Native start velocity = relative (outward) velocity + frame velocity
        // (thinggeo.cpp:578). The outward ejection component is independent of
        // the ship's velocity, so emitting the SAME ship twice at two different
        // velocities must shift every puff's velocity by exactly the velocity
        // delta — regardless of the (arbitrary, deterministic) vent directions.
        pool.Emit(Origin, v1, Orient, RADIUS, 7, 5, 0.016f, rng);
        var slow = pool.Enumerate().Select(v => v.Velocity).ToArray();

        pool.Reset();
        pool.Emit(Origin, v2, Orient, RADIUS, 7, 5, 0.016f, rng);
        var fast = pool.Enumerate().Select(v => v.Velocity).ToArray();

        Assert.Equal(5, slow.Length);
        Assert.Equal(5, fast.Length);
        for (int i = 0; i < 5; i++)
            Assert.Equal(v2 - v1, fast[i] - slow[i]);
    }

    [Fact]
    public void Puff_acceleration_is_minus_one_third_its_start_velocity()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 7, 4, 0.016f, rng);

        // Native m_vecAcceleration = vecStartVelocity · −0.333 (thinggeo.cpp:749) —
        // a drag proportional to each puff's own initial velocity.
        foreach (var v in pool.Enumerate())
            Assert.Equal(v.Velocity * (-1f / 3f), v.Acceleration);
    }

    [Fact]
    public void Puffs_expire_after_the_native_035s_duration()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 7, 8, 0.016f, rng);
        Assert.Equal(8, pool.ActiveCount);

        // Advance well past m_fDuration = 0.35s; everything must expire.
        for (int i = 0; i < 40 && pool.ActiveCount > 0; i++)
            pool.Update(0.025f);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Puffs_integrate_position_with_drag_acceleration_closed_form()
    {
        // Exact closed-form check of native SmokeBehavior's integral
        // pos = origin + vel·t + ½·accel·t² (particlegeo.cpp:58), where accel is
        // the puff's own −⅓·vel drag — a must-fail discriminator against dropping
        // the acceleration term.
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();
        const float dt = 0.016f;
        const float t = 0.080f;

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 7, 3, dt, rng);
        var born = pool.Enumerate().First();
        float before = born.Position.X;
        float after = born.Position.X + born.Velocity.X * t + 0.5f * born.Acceleration.X * t * t;

        pool.Update(t);
        var moved = pool.Enumerate().First().Position;

        // With accel = −⅓·vel and t = 0.08, the drag term is −⅙·vel·t² — small but
        // measurable; without it the prediction would be off by that much.
        float withoutDrag = born.Position.X + born.Velocity.X * t;
        Assert.True(MathF.Abs(after - withoutDrag) > 1e-3f, "test must exercise the drag term");
        Assert.True(after != before, "test must actually move the puff");
        Assert.Equal(after, moved.X, 1e-2f);
    }

    [Fact]
    public void Emit_across_multiple_ships_shares_the_pool()
    {
        // The live client drives one shared pool from every damaged ship each
        // frame; verify accumulation + shared ageing behave sensibly.
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 1, 3, 0.016f, rng);
        pool.Emit(new SimVec(2000f, 0f, 0f), ShipVelocity(), Orient, RADIUS, 2, 4, 0.016f, rng);
        Assert.Equal(7, pool.ActiveCount);

        pool.Update(0.050f);   // neither ship's puffs expire yet (< 0.35s)
        Assert.Equal(7, pool.ActiveCount);
    }

    [Fact]
    public void Reset_clears_every_puff()
    {
        var pool = new DamageDebrisParticles();
        var rng = new FixedRandom();

        pool.Emit(Origin, ShipVelocity(), Orient, RADIUS, 7, 6, 0.016f, rng);
        Assert.Equal(6, pool.ActiveCount);

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }

    private static SimVec ShipVelocity() => new(80f, 0f, 0f);
}
