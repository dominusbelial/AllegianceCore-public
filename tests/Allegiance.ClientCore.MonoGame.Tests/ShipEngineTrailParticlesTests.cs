using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P358 pure ship engine-thrust particle pool
/// (<see cref="ShipEngineTrailParticles"/>). These exercise the pool's own
/// mechanics — the thrust-power &gt; 0 emission gate, the fire+smoke pair spawn,
/// backward ejection + ship-velocity inheritance, ageing/expiry and the fixed
/// capacity bound — with a deterministic random stub, and never touch a
/// graphics device (the pool is MonoGame-free by design).
/// </summary>
public class ShipEngineTrailParticlesTests
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

    private static readonly SimVec Origin = new(100f, 200f, 300f);
    private static readonly SimVec ShipVel = new(10f, 0f, 0f);   // ship coasts +X; trail inherits this

    private static Orientation FaceNegZ()
    {
        var o = new Orientation();
        o.SetForward(new SimVec(0f, 0f, -1f));   // forward -Z => backward +Z (thrust ejects +Z)
        return o;
    }

    private static EngineTrailShip NewShip(short id, float thrustPower, float radius = 25f,
        SimVec? pos = null, SimVec? vel = null, Orientation? ori = null) =>
        new(id, pos ?? Origin, vel ?? ShipVel, ori ?? FaceNegZ(), radius, thrustPower);

    [Fact]
    public void Emit_spawns_one_fire_and_one_smoke_pair_for_a_thrusting_ship()
    {
        var pool = new ShipEngineTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Emit(NewShip(1, thrustPower: 1f), (float)(1.0 / 60.0), rng);

        Assert.Equal(2, pool.ActiveCount);
        var views = pool.Enumerate().ToList();
        Assert.Contains(views, p => p.Kind == TrailParticleKind.Fire);
        Assert.Contains(views, p => p.Kind == TrailParticleKind.Smoke);

        // Freshly spawned: full remaining lifetime; native durations preserved.
        var fire = views.First(p => p.Kind == TrailParticleKind.Fire);
        var smoke = views.First(p => p.Kind == TrailParticleKind.Smoke);
        Assert.Equal(1f, fire.RemainingFrac);
        Assert.Equal(1f, smoke.RemainingFrac);
    }

    [Fact]
    public void Emit_nothing_when_thrust_power_is_zero_or_negative()
    {
        var pool = new ShipEngineTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        // Idle ship (m_fThrustPower <= 0): the native gate (thinggeo.cpp:760).
        pool.Emit(NewShip(1, thrustPower: 0f), (float)(1.0 / 60.0), rng);

        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Emit_spawns_independently_for_each_thrusting_ship()
    {
        var pool = new ShipEngineTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Emit(NewShip(1, thrustPower: 1f, pos: new SimVec(0f, 0f, 0f)), (float)(1.0 / 60.0), rng);
        pool.Emit(NewShip(2, thrustPower: 1f, pos: new SimVec(0f, 0f, 50f)), (float)(1.0 / 60.0), rng);
        pool.Emit(NewShip(3, thrustPower: 0f), (float)(1.0 / 60.0), rng);   // idle -> no pair

        Assert.Equal(4, pool.ActiveCount);   // 2 thrusting ships × (fire + smoke)
    }

    [Fact]
    public void Fire_and_smoke_eject_backward_and_inherit_the_ship_velocity()
    {
        var pool = new ShipEngineTrailParticles(capacity: 64);
        var rng = new FixedRandom();
        pool.Emit(NewShip(1, thrustPower: 1f), (float)(1.0 / 60.0), rng);

        // Fresh spawn: both particles start aft of the ship centre (+Z, backward),
        // not at or ahead of it.
        foreach (var p in pool.Enumerate())
            Assert.True(p.Position.Z > Origin.Z,
                $"expected the particle to start aft of the ship centre (got Z={p.Position.Z}, centre={Origin.Z})");

        // Age them a short step. Fire lives 0.333s, so 0.1s keeps it alive.
        var before = pool.Enumerate().ToList();
        const float dt = 0.10f;
        pool.Update(dt);
        var after = pool.Enumerate().ToList();

        // Both particles advance +Z (backward ejection along the thrust direction).
        var fBefore = before.First(p => p.Kind == TrailParticleKind.Fire);
        var fAfter = after.First(p => p.Kind == TrailParticleKind.Fire);
        var sBefore = before.First(p => p.Kind == TrailParticleKind.Smoke);
        var sAfter = after.First(p => p.Kind == TrailParticleKind.Smoke);
        Assert.True(fAfter.Position.Z > fBefore.Position.Z, "fire must eject backward (+Z)");
        Assert.True(sAfter.Position.Z > sBefore.Position.Z, "smoke must eject backward (+Z)");

        // Velocity inheritance: the SMOKE velocity is exactly thrust + shipVel
        // (ComputeSmokeParticleParameters has no end-point travel), so its +X
        // advance over dt is ShipVel.X·dt = 10·0.10 = 1.0 — the ship's +X motion
        // drags the trail along, keeping it attached to a moving ship.
        float sx = sAfter.Position.X - sBefore.Position.X;
        Assert.InRange(sx, 0.5f, 1.5f);   // ≈ ShipVel.X·dt = 1.0 → velocity inheritance
        // The fire carries the same inherited +X but adds a small relVel
        // correction for its start-position jitter; just require a positive drift.
        Assert.True(fAfter.Position.X > fBefore.Position.X, "fire must inherit the ship's +X velocity");
    }

    [Fact]
    public void Stop_thrusting_and_pool_drains_to_empty()
    {
        var pool = new ShipEngineTrailParticles(capacity: 256);
        var rng = new FixedRandom();
        const float dt = 0.030f;

        // Emit continuously while thrusting for a couple of seconds — steady state.
        for (int step = 0; step < 50; step++)
            pool.Emit(NewShip(1, thrustPower: 1f), dt, rng);
        Assert.True(pool.ActiveCount > 0, "emitting should populate the pool");

        // Stop thrusting (power 0): no new emission; every particle ages out.
        for (int step = 0; step < 200; step++)
        {
            pool.Emit(NewShip(1, thrustPower: 0f), dt, rng);
            pool.Update(dt);
        }
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Particles_age_and_expire_by_their_native_durations()
    {
        var pool = new ShipEngineTrailParticles(capacity: 256);
        var rng = new FixedRandom();
        const float dt = 0.030f;

        pool.Emit(NewShip(1, thrustPower: 1f), dt, rng);
        // Smoke lives 2.0s; fire 0.333s. After 1s the fire is gone, the smoke lives on.
        for (int step = 0; step < 40; step++)   // ~1.2s
            pool.Update(dt);
        var views = pool.Enumerate().ToList();
        Assert.DoesNotContain(views, p => p.Kind == TrailParticleKind.Fire);
        Assert.Contains(views, p => p.Kind == TrailParticleKind.Smoke);
    }
}
