using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P235 pure missile-exhaust particle pool
/// (<see cref="MissileTrailParticles"/>). These exercise the pool's own
/// mechanics — spawn of the fire+smoke pair, ageing/expiry, the fixed capacity
/// bound and position integration — with a deterministic random stub, and never
/// touch a graphics device (the pool is MonoGame-free by design).
/// </summary>
public class MissileTrailParticlesTests
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
    private static readonly SimVec Fwd = new(0f, 0f, -1f);   // facing -Z; exhaust ejects +Z

    [Fact]
    public void Update_spawns_one_fire_and_one_smoke_pair_per_missile()
    {
        var pool = new MissileTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Update(0.0, (float)(1.0 / 60.0), new[] { NewMissile(1, 0.0) }, rng);

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
    public void Update_spawns_independently_for_each_live_missile()
    {
        var pool = new MissileTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Update(0.0, (float)(1.0 / 60.0),
            new[]
            {
                NewMissile(1, 0.0, new SimVec(0f, 0f, 0f)),
                NewMissile(2, 0.0, new SimVec(0f, 0f, 50f)),
            },
            rng);

        Assert.Equal(4, pool.ActiveCount);   // 2 missiles × (fire + smoke)
    }

    [Fact]
    public void Stop_emitting_and_pool_drains_to_empty()
    {
        var pool = new MissileTrailParticles(capacity: 256);
        var rng = new FixedRandom();
        const double dt = 0.050;             // 20 Hz sim steps

        // Emit continuously for a couple of seconds — the pool fills to a
        // steady state (a live missile spawns every frame; old ones expire).
        double now = 0.0;
        for (int step = 0; step < 50; step++)
        {
            now += dt;
            pool.Update(now, (float)dt, new[] { NewMissile(1, now) }, rng);
        }
        Assert.True(pool.ActiveCount > 0, "emitting should populate the pool");

        // Stop emitting; every particle must age out of existence.
        for (int step = 0; step < 100; step++)
        {
            now += dt;
            pool.Update(now, (float)dt, Array.Empty<TrailMissile>(), rng);
        }
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Fire_expires_before_smoke_after_emission_stops()
    {
        var pool = new MissileTrailParticles(capacity: 256);
        var rng = new FixedRandom();
        const double dt = 0.010;             // fine steps to see the fire/smoke split
        double now = 0.0;

        // Emit a single pair, then age with no further emission.
        pool.Update(now, (float)dt, new[] { NewMissile(1, now) }, rng);
        Assert.Equal(2, pool.ActiveCount);

        bool sawFireExpireWhileSmokeAlive = false;
        for (int step = 0; step < 300; step++)
        {
            now += dt;
            pool.Update(now, (float)dt, Array.Empty<TrailMissile>(), rng);
            int fire = pool.Enumerate().Count(p => p.Kind == TrailParticleKind.Fire);
            int smoke = pool.Enumerate().Count(p => p.Kind == TrailParticleKind.Smoke);
            if (fire == 0 && smoke > 0) sawFireExpireWhileSmokeAlive = true;   // fire (0.33s) gone, smoke (2.0s) lingering
            if (pool.ActiveCount == 0) break;
        }

        Assert.True(sawFireExpireWhileSmokeAlive,
            "the 0.33s fire particle should expire while the 2.0s smoke is still alive");
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void ActiveCount_respects_capacity_bound()
    {
        const int cap = 4;
        var pool = new MissileTrailParticles(capacity: cap);
        var rng = new FixedRandom();
        double now = 0.0;
        const double dt = 0.008;             // very fast emission vs expiry → fill the pool

        for (int step = 0; step < 1000; step++)
        {
            now += dt;
            pool.Update(now, (float)dt, new[] { NewMissile(1, now) }, rng);
            Assert.True(pool.ActiveCount <= cap,
                $"ActiveCount {pool.ActiveCount} exceeded capacity {cap}");
        }
    }

    [Fact]
    public void Particles_integrate_position_by_velocity_over_time()
    {
        var pool = new MissileTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Update(0.0, (float)(1.0 / 60.0), new[] { NewMissile(1, 0.0) }, rng);
        var before = pool.Enumerate().ToDictionary(p => p.Kind, p => p.Position);
        var smokeBefore = before[TrailParticleKind.Smoke];

        // Feed the same missile position again a little later so it stays alive,
        // then advance; the surviving smoke must have moved off the origin.
        double now = 0.030;
        pool.Update(now, (float)(1.0 / 60.0), new[] { NewMissile(1, now) }, rng);
        var after = pool.Enumerate().First(p => p.Kind == TrailParticleKind.Smoke).Position;

        Assert.NotEqual(smokeBefore, after);
        // Smoke ejects along the exhaust direction (−Forward = +Z here), so its
        // new position must sit behind the missile (greater Z) — it trailed off.
        Assert.True(after.Z > smokeBefore.Z,
            $"expected smoke to move +Z (behind the -Z-facing missile); before={smokeBefore.Z}, after={after.Z}");
    }

    [Fact]
    public void Reset_clears_every_particle_and_velocity_state()
    {
        var pool = new MissileTrailParticles(capacity: 64);
        var rng = new FixedRandom();

        pool.Update(0.0, (float)(1.0 / 60.0), new[] { NewMissile(1, 0.0) }, rng);
        Assert.Equal(2, pool.ActiveCount);

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());

        // After a reset a fresh update works and the velocity estimate is clean.
        pool.Update(0.060, (float)(1.0 / 60.0), new[] { NewMissile(1, 0.060) }, rng);
        Assert.Equal(2, pool.ActiveCount);
    }

    [Fact]
    public void RemainingFrac_decreases_as_a_particle_ages()
    {
        var pool = new MissileTrailParticles(capacity: 256);
        var rng = new FixedRandom();

        // Sample the newest smoke's remaining fraction on successive frames: it
        // must be monotonically non-increasing toward zero.
        float prev = 1f;
        double now = 0.0;
        const double dt = 0.040;
        for (int step = 0; step < 40; step++)
        {
            now += dt;
            pool.Update(now, (float)dt, new[] { NewMissile(1, now) }, rng);
            // Pick the oldest live smoke (lowest remaining) as the fading one.
            var smokes = pool.Enumerate().Where(p => p.Kind == TrailParticleKind.Smoke).ToList();
            if (smokes.Count == 0) break;
            float rem = smokes.Min(p => p.RemainingFrac);
            Assert.True(rem <= prev + 1e-5f, $"remaining fraction rose: {prev} -> {rem}");
            prev = rem;
        }
        // By the end the pool should be draining (some smoke aged out).
        Assert.True(pool.ActiveCount < 40 * 2, "expected ageing to prune old particles");
    }

    private static TrailMissile NewMissile(short id, double t, SimVec? pos = null) =>
        new(id, pos ?? Origin, Fwd, t);
}
