using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P296 warp-transit teleport-spark pool
/// (<see cref="WarpTransitParticles"/>) — a faithful port of native
/// <c>AddTeleportParticles</c> (effect/thinggeo.cpp:619-696). These exercise
/// the pool's own mechanics — the transit-instant burst (count, size, ship-
/// velocity inheritance, anti-velocity acceleration), ageing/expiry, the
/// exact closed-form position integration, and reset — with a deterministic
/// random stub, and never touch a graphics device (the pool is MonoGame-free).
/// </summary>
public class WarpTransitParticlesTests
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
    private const float RADIUS = 10f;

    [Fact]
    public void EmitBurst_emits_exactly_teleport_spark_count_sparks_at_transit()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();

        pool.EmitBurst(Origin, ShipVelocity(), SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);

        // Native transit-instant count = TELEPORT_SPARK_COUNT × s_iShowSmoke.
        // P781 — ShowSmoke is the SHIPPED default 3 (WinTrek.cpp:3293), not the
        // static initialiser 1 (thinggeo.cpp:1679), so one burst emits 9 sparks.
        Assert.Equal(9, pool.ActiveCount);
        var views = pool.Enumerate().ToList();
        Assert.Equal(9, views.Count);
        // Fresh sparks: full remaining lifetime (native m_fDuration = 0.5s).
        Assert.All(views, v => Assert.Equal(1f, v.RemainingFrac, 5));
    }

    [Fact]
    public void EmitBurst_spark_size_scales_from_ship_radius()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();

        pool.EmitBurst(Origin, ShipVelocity(), SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);

        // Native size = random(3·radius·0.15, 3·radius·0.3) = (0.45..0.9)·radius
        // (thinggeo.cpp:688, fScaledUpCount = 3·radius at transit).
        foreach (var v in pool.Enumerate())
            Assert.InRange(v.Size, RADIUS * 0.449f, RADIUS * 0.901f);
    }

    [Fact]
    public void EmitBurst_sparks_inherit_the_ships_velocity_plus_a_blast()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();
        var vel = new SimVec(100f, 0f, 0f);

        pool.EmitBurst(Origin, vel, SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);

        // Each spark's velocity = ship velocity + blast, where the blast
        // magnitude is radius·random(0.75,1.25) (thinggeo.cpp:681,666-667).
        foreach (var v in pool.Enumerate())
        {
            var blast = v.Velocity - vel;
            Assert.InRange(blast.Length(), RADIUS * 0.74f, RADIUS * 1.26f);
        }
    }

    [Fact]
    public void EmitBurst_spark_acceleration_opposes_the_ships_velocity()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();
        var vel = new SimVec(100f, 0f, 0f);

        pool.EmitBurst(Origin, vel, SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);

        // Native m_vecAcceleration = -vecFrameVelocity (thinggeo.cpp:682).
        foreach (var v in pool.Enumerate())
            Assert.Equal(-vel, v.Acceleration);
    }

    [Fact]
    public void Sparks_age_out_after_the_native_duration()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();

        pool.EmitBurst(Origin, ShipVelocity(), SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);
        Assert.Equal(9, pool.ActiveCount);   // P781 — smoke 3 (WinTrek.cpp:3293)

        // Advance past m_fDuration = 0.5s in a few steps; everything must expire.
        for (int i = 0; i < 20 && pool.ActiveCount > 0; i++)
            pool.Update(0.03f);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void Sparks_integrate_position_with_acceleration_closed_form()
    {
        // Exact closed-form check of the native integral
        // pos = origin + vel·t + ½·accel·t² (particlegeo.cpp:239), where vel is
        // the PARTICLE's own velocity (ship velocity + outward blast), using a
        // scenario where the anti-velocity acceleration visibly pulls the spark
        // back off the pure-velocity drift — a must-fail discriminator against
        // dropping the acceleration term.
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom { Dir = new SimVec(1f, 0f, 0f) };
        var vel = new SimVec(100f, 0f, 0f);
        const float dt = 0.06f;
        const float t = 0.10f;

        pool.EmitBurst(Origin, vel, SimVec.Zero, SimVec.Zero, RADIUS, dt, rng);
        var born = pool.Enumerate().First();   // position/velocity/accel at age 0

        pool.Update(t);
        var moved = pool.Enumerate().First().Position;

        // Shift = particleVel·t + ½·accel·t². Here particleVel.X = 100 + 10
        // (ship + blast), accel.X = -100, t = 0.1 → +11 − 0.5 = +10.5 (the −0.5
        // is the acceleration pull-back; without acceleration the shift would
        // be a full +11, so this distinguishes the term).
        float withAccel = born.Position.X + born.Velocity.X * t + 0.5f * born.Acceleration.X * t * t;
        float withoutAccel = born.Position.X + born.Velocity.X * t;

        Assert.True(withAccel != withoutAccel, "test must actually exercise the acceleration term");
        Assert.Equal(withAccel, moved.X, 1e-2f);
        Assert.Equal(born.Position.Y, moved.Y, 1e-3f);
        Assert.Equal(born.Position.Z, moved.Z, 1e-3f);
    }

    [Fact]
    public void RemainingFrac_decreases_as_a_spark_ages()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();

        pool.EmitBurst(Origin, ShipVelocity(), SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);
        float prev = 1f;
        for (int i = 0; i < 10 && pool.ActiveCount > 0; i++)
        {
            pool.Update(0.02f);
            float rem = pool.Enumerate().First().RemainingFrac;
            Assert.True(rem <= prev + 1e-5f, $"remaining fraction rose: {prev} -> {rem}");
            prev = rem;
        }
        Assert.True(prev < 1f, "expected sparks to have aged");
    }

    [Fact]
    public void Reset_clears_every_spark()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();

        pool.EmitBurst(Origin, ShipVelocity(), SimVec.Zero, SimVec.Zero, RADIUS, 0.06f, rng);
        Assert.Equal(9, pool.ActiveCount);   // P781 — smoke 3 (WinTrek.cpp:3293)

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }

    private static SimVec ShipVelocity() => new(80f, 0f, 0f);
}
