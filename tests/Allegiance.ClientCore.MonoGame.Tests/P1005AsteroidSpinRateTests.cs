using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xna = Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1005 — pins the client-side asteroid spin's <b>angular velocity</b> to the
/// native rule instead of trusting a screenshot: native <c>CasteroidIGC</c> seeds
/// <c>m_rotation</c> from <c>DataAsteroidIGC.Rotation</c> (<c>Igc/asteroidigc.cpp:45</c>)
/// and <c>CmodelIGC::Update</c> post-rotates the model by
/// <c>m_rotation.angle() · dt</c> every update (<c>Igc/modelIGC.cpp:85-99</c>),
/// where <c>angle</c> is an angular <b>rate in radians/second</b> (<c>Time::operator-</c>
/// divides by <c>fResolution() = 1000</c>, <c>zlib/ztime.h:172</c>) and <c>dt</c> is
/// <b>mission</b> time.
/// <para>The tests here feed the <em>production</em> <see cref="AsteroidSpin"/> /
/// <see cref="AsteroidSpinClock"/> and the production draw composition
/// (<c>PoseConverter.ToXnaRotation(base) · CreateFromAxisAngle(axis, angle)</c>,
/// exactly <c>Game1.Rendering.cs</c>) and compare the resulting orientation with
/// the sim's <see cref="Orientation.PostRotate"/> — the P766-certified port of
/// native's Rodrigues <c>Matrix::SetRotate</c> (<c>zlib/matrix.cpp:408-452</c>,
/// <c>zlib/orientation.cpp:595-603</c>).</para>
/// </summary>
public class P1005AsteroidSpinRateTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    /// <summary>The world rotation of a rock exactly as <c>Game1</c> composes it
    /// (colour pass, linear-depth gather and HUD preview all share this form).</summary>
    private static Xna.Matrix RockRotation(AsteroidSpin spin, Orientation baseOrientation) =>
        PoseConverter.ToXnaRotation(baseOrientation)
        * Xna.Matrix.CreateFromAxisAngle(
            new Xna.Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z), spin.Angle);

    [Fact]
    public void Rendered_angular_velocity_equals_native_angle_times_dt()
    {
        // Wire value: axis +Y, angle 0.5 -> 0.5 rad/s (native's rad/s rate).
        var spin = AsteroidSpin.FromWire([0f, 1f, 0f, 0.5f]);
        var baseOrientation = new Orientation();      // map rocks carry zero forward/up -> identity
        var before = RockRotation(spin, baseOrientation);
        var rightBefore = Xna.Vector3.Transform(new Xna.Vector3(1f, 0f, 0f), before);

        const float dt = 1f / 60f;
        for (int i = 0; i < 60; i++) spin.Advance(dt);        // exactly one sim second
        Assert.Equal(0.5f, spin.Angle, 4);                    // native: angle x dt accumulated

        var after = RockRotation(spin, baseOrientation);
        var rightAfter = Xna.Vector3.Transform(new Xna.Vector3(1f, 0f, 0f), after);
        var forwardAfter = Xna.Vector3.Transform(new Xna.Vector3(0f, 0f, -1f), after);

        // Native's per-tick o.PostRotate(axis, angle x dt) over that second.
        var native = new Orientation();
        native.PostRotate(new Vector3(0f, 1f, 0f), 0.5f);

        Assert.Equal(1f, rightBefore.Length(), 4);            // a pure rotation (unit basis)
        Assert.Equal(native.GetRight().X, rightAfter.X, 3);
        Assert.Equal(native.GetRight().Y, rightAfter.Y, 3);
        Assert.Equal(native.GetRight().Z, rightAfter.Z, 3);
        Assert.Equal(native.GetForward().X, forwardAfter.X, 3);
        Assert.Equal(native.GetForward().Z, forwardAfter.Z, 3);

        // ... and the turned angle is the rate times the elapsed time (0.5 rad),
        // in native's Rodrigues direction: (1,0,0) -> (cos t, 0, -sin t).
        Assert.Equal(MathF.Cos(0.5f), rightAfter.X, 3);
        Assert.Equal(0f, rightAfter.Y, 3);
        Assert.Equal(-MathF.Sin(0.5f), rightAfter.Z, 3);
    }

    [Fact]
    public void Rate_is_invariant_to_how_the_elapsed_time_is_split()
    {
        // 1 s of sim time applied as one step, 4 quarter steps, 60 frames or 144
        // frames must produce the same rotation: the spin integrates angle x dt.
        float Angle(AsteroidSpin s, params float[] steps)
        {
            foreach (var step in steps) s.Advance(step);
            return s.Angle;
        }

        var one = Angle(AsteroidSpin.FromWire([0f, 1f, 0f, 0.3f]), 1.0f);
        var four = Angle(AsteroidSpin.FromWire([0f, 1f, 0f, 0.3f]), 0.25f, 0.25f, 0.25f, 0.25f);
        var sixty = Angle(AsteroidSpin.FromWire([0f, 1f, 0f, 0.3f]),
            Enumerable.Repeat(1f / 60f, 60).ToArray());
        var hundredFortyFour = Angle(AsteroidSpin.FromWire([0f, 1f, 0f, 0.3f]),
            Enumerable.Repeat(1f / 144f, 144).ToArray());

        Assert.Equal(0.3f, one, 4);
        Assert.Equal(one, four, 4);
        Assert.Equal(one, sixty, 4);
        Assert.Equal(one, hundredFortyFour, 4);
    }

    [Fact]
    public void Spin_clock_seeds_without_a_step_and_a_repeated_game_time_advances_nothing()
    {
        // The "applied per draw pass" regression: the linear-depth gather, the
        // colour pass and the HUD preview all read the same rock. The clock is a
        // game-clock DELTA, so the first observation seeds it (native seeds
        // m_lastUpdate = now at creation, modelIGC.h:86-89) and any further read of
        // the same game time yields zero.
        var clock = new AsteroidSpinClock();
        var spin = AsteroidSpin.FromWire([0f, 1f, 0f, 0.5f]);

        Assert.Equal(0f, clock.Advance(12.5));      // first frame: seed only
        Assert.Equal(0f, spin.Angle);
        Assert.Equal(0f, clock.Advance(12.5));      // a second pass in the same frame
        Assert.Equal(0f, spin.Angle);

        Assert.Equal(0.25f, clock.Advance(12.75), 5);   // one quarter second of game time
        Assert.Equal(0f, clock.Advance(12.75));         // again: no second advance
        spin.Advance(0.25f);
        Assert.Equal(0.125f, spin.Angle, 5);            // 0.5 rad/s x 0.25 s

        // A game clock that goes backwards (session reset) never runs the spin back.
        Assert.Equal(0f, clock.Advance(1.0));
        Assert.Equal(0.125f, spin.Angle, 5);            // the caller's 0 step changes nothing
    }

    [Fact]
    public void Spin_clock_clamps_one_step_to_native_max_delta()
    {
        // native Time::dwMaxDeltaTime = fResolution() * 0.25 (4 FPS, ztime.h:57),
        // applied by the Time arithmetic at ztime.h:146-166: a stalled frame,
        // a menu round-trip or a paused host cannot jump a rock by seconds.
        Assert.Equal(0.25f, AsteroidSpinClock.MaxStepSeconds, 5);

        var clock = new AsteroidSpinClock();
        clock.Advance(10.0);
        Assert.Equal(0.25f, clock.Advance(40.0), 5);        // 30 s gap -> clamped
        Assert.Equal(0.2f, clock.Advance(40.2), 5);         // an ordinary 0.2 s step passes as-is
    }

    [Fact]
    public void Spin_runs_on_the_game_clock_so_time_dilation_slows_the_rocks()
    {
        // P618: the client's world animation clock is _gameSeconds = wall time x the
        // live sim scale (the port's analogue of native's mission clock). A rock at
        // 0.2 rad/s must advance 0.2 rad per game second — so at a 0.5 time scale it
        // turns half as far in a wall second, WITH the slowed world, instead of
        // outrunning it (the wall-clock Draw step P252 used).
        static float Simulate(float rate, double timeScale)
        {
            var clock = new AsteroidSpinClock();
            var spin = AsteroidSpin.FromWire([0f, 1f, 0f, rate]);
            double gameSeconds = 0.0;
            clock.Advance(gameSeconds);                      // seed the first frame (step 0)
            for (int i = 0; i < 60; i++)
            {
                gameSeconds += (1.0 / 60.0) * timeScale;    // one wall second of frames
                spin.Advance(clock.Advance(gameSeconds));
            }
            return spin.Angle;
        }

        Assert.Equal(0.2f, Simulate(0.2f, 1.0), 4);          // full speed: 0.2 rad/s
        Assert.Equal(0.1f, Simulate(0.2f, 0.5), 4);          // diluted to half speed
        Assert.Equal(0.02f, Simulate(0.2f, 0.1), 4);         // native-free slow motion
    }

    [Fact]
    public void Zero_rate_rock_does_not_spin()
    {
        // native's guard: m_rotation.angle() != 0 (Igc/modelIGC.cpp:87).
        var clock = new AsteroidSpinClock();
        var spin = AsteroidSpin.FromWire([0f, 1f, 0f, 0f]);
        clock.Advance(0.0);
        for (int i = 1; i <= 60; i++) spin.Advance(clock.Advance(i / 60.0));
        Assert.Equal(0f, spin.Angle);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_map_rates_are_the_native_band_and_the_port_spins_at_that_rate()
    {
        Require.Artwork();
        // m2xspiderw.igc (134 rocks): native's authored rate field is rad/s, and the
        // port must turn each rock by exactly that many radians per sim second.
        var map = MissionMap.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "m2xspiderw.igc")));
        var rocks = map.Asteroids;
        Assert.Equal(134, rocks.Count);

        foreach (var rock in rocks)
        {
            float rate = rock.Rotation[3];
            Assert.InRange(rate, 0.002f, 0.25f);                 // rad/s (<= 2.4 rpm)
            Assert.InRange(rate * 60f / (2f * MathF.PI), 0.01f, 2.4f);   // rpm
            Assert.InRange(rate * 180f / MathF.PI, 0.1f, 14.4f);         // deg/s
            var axisLength = new Vector3(rock.Rotation[0], rock.Rotation[1], rock.Rotation[2]).Length();
            Assert.InRange(axisLength, 0.5f, 2.0f);               // a real, non-degenerate axis

            var spin = AsteroidSpin.FromWire(rock.Rotation);
            Assert.Equal(rate, spin.Rate);
            spin.Advance(1.0f);                                   // one sim second
            Assert.Equal(rate, spin.Angle, 5);                    // native: angle x dt
        }
    }
}
