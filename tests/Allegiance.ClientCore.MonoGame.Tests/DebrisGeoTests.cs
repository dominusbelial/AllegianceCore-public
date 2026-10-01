using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P449a ambient space-dust field (<see cref="DebrisGeo"/>) —
/// a faithful port of native <c>DebrisGeo</c> (<c>effect/debrisgeo.cpp:17-205</c>).
/// These exercise the pure evaluate-step math — distance-driven spawning, the
/// forward-cone placement, the bluish-white colour, the <c>1 − 0.25·t²</c> fade,
/// the 500 m / fade cull, and jump-clearing — with a deterministic random stub,
/// and never touch a graphics device (the class is MonoGame-free).
/// </summary>
public class DebrisGeoTests
{
    /// <summary>Deterministic <see cref="ITrailRandom"/>: Range returns a fixed
    /// interpolation between min/max (Direction is unused by DebrisGeo).</summary>
    private sealed class FixedRandom : ITrailRandom
    {
        public float T = 0.5f;
        public float Range(float min, float max) => min + T * (max - min);
        public SimVec Direction() => new(1f, 0f, 0f);
    }

    // A stable camera pose: eye at the origin, looking along -Z, +X right, +Y up.
    private static readonly SimVec Forward = new(0f, 0f, -1f);
    private static readonly SimVec Right = new(1f, 0f, 0f);
    private static readonly SimVec Up = new(0f, 1f, 0f);

    /// <summary>Focal length ≈ cot(37.5°) — a plausible 75° vertical FOV (the
    /// value Game1 extracts from the projection's M22 for the in-flight cameras).
    /// Only the ratio 1/focus matters to the field's cone and (given the FOV-
    /// independent size argument) to the rendered grain.</summary>
    private const float Focus = 1.3f;
    private static readonly float RFocus = 1f / Focus;

    [Fact]
    public void No_particles_spawn_while_stationary()
    {
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        // Camera never moves → length = 0 → no spawns, and never a jump.
        geo.Step(new SimVec(0, 0, 0), Forward, Right, Up, Focus, 0.016f);
        Assert.Equal(0, geo.Count);
        geo.Step(new SimVec(0, 0, 0), Forward, Right, Up, Focus, 0.048f);
        Assert.Equal(0, geo.Count);
    }

    [Fact]
    public void Moving_distance_spawns_one_per_distancePerParticle()
    {
        // Native spawns one grain every distancePerParticle of camera travel
        // (debrisgeo.cpp:128-157). Exercise the accumulator over a run in a
        // small-dt regime (grains live 2 s, so keep the whole run under that so
        // none die mid-test) and verify the spawn count matches total travelled /
        // the field's own measured distancePerParticle — the honest statement of
        // "moving N·distancePerParticle spawns N".
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        const float T = 0.015f;   // seconds per step (small → grains persist)
        const float L = 1f;       // metres per step
        const int steps = 100;    // 100·0.015 = 1.5 s of clock < 2 s fade

        geo.Step(new SimVec(L, 0, 0), Forward, Right, Up, Focus, T);   // establishes dpp
        float dpp = geo.CurrentDistancePerParticle;
        for (int i = 2; i <= steps; i++)
            geo.Step(new SimVec(L * i, 0, 0), Forward, Right, Up, Focus, T * i);

        float totalDist = steps * L;
        int expected = (int)(totalDist / dpp);
        Assert.True(expected > 0, "test must travel several distancePerParticle");
        // Native's accumulator can leave a fractional leftover of up to one dpp
        // (the strict-> bound, debrisgeo.cpp:133), hence the ±1 window.
        Assert.InRange(geo.Count, expected - 1, expected + 1);
    }

    [Fact]
    public void Brightness_follows_1_minus_quarter_t_squared()
    {
        // debrisgeo.cpp:118,174 — bright = 1 - 0.25·t².
        Assert.Equal(1.00f, DebrisGeo.Brightness(0f), 5);
        Assert.Equal(0.75f, DebrisGeo.Brightness(1f), 5);
        Assert.Equal(0.00f, DebrisGeo.Brightness(2f), 5);
        // A distinguishing point proves it is quadratic, not linear: at t=1.5,
        // 1 - 0.25·2.25 = 0.4375 (linear would be 0.25).
        Assert.Equal(0.4375f, DebrisGeo.Brightness(1.5f), 5);
    }

    [Fact]
    public void Faded_particles_are_culled_on_the_next_step()
    {
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        // Spawn 3 grains by moving 3.5 m in the dpp=1.0 regime (dt = 76·3.5).
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.Equal(3, geo.Count);

        // Hold the camera still and let > 2 s elapse: every grain's brightness
        // drops to ≤ 0 and the cull removes it (even with zero travel the cull
        // runs; debrisgeo.cpp:112-126).
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f + 3f);
        Assert.Equal(0, geo.Count);
    }

    [Fact]
    public void Beyond_500m_or_faded_is_culled_by_the_predicate()
    {
        // distance² > 250000 (500 m) culls; exactly 500 m does not (debrisgeo.cpp:121).
        Assert.True(DebrisGeo.ShouldCull(250001f, 0f));
        Assert.False(DebrisGeo.ShouldCull(250000f, 0f));
        Assert.False(DebrisGeo.ShouldCull(0f, 0f));
        // Brightness ≤ 0 (age ≥ 2 s) culls; still bright does not.
        Assert.True(DebrisGeo.ShouldCull(0f, 2f));
        Assert.False(DebrisGeo.ShouldCull(0f, 1.5f));
    }

    [Fact]
    public void A_jump_clears_the_field()
    {
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        // Build up a few grains.
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.Equal(3, geo.Count);

        // A single > 100 m camera displacement (a warp/teleport/sector change)
        // is native's jump rule (debrisgeo.cpp:98) — the field is cleared.
        geo.Step(new SimVec(3.5f + 1000f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f + 0.017f);
        Assert.Equal(0, geo.Count);
    }

    [Fact]
    public void Clear_drops_every_grain_and_resets_the_accumulator()
    {
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.Equal(3, geo.Count);

        geo.Clear();
        Assert.Equal(0, geo.Count);

        // A normal (non-jump) step after clearing must not resurrect anything and
        // spawns fresh grains from a clean accumulator.
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f + 0.1f);
        Assert.Equal(0, geo.Count);
    }

    [Fact]
    public void Spawn_positions_lie_in_the_forward_cone()
    {
        // One step that spawns 3 grains (move 3.5 m, dpp = 1.0). Every grain is
        // placed relative to THIS step's camera pose, so we can check the cone
        // invariants against it directly.
        SimVec cameraPos = new(3.5f, 0, 0);
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        geo.Step(cameraPos, Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.Equal(3, geo.Count);

        foreach (var grain in geo.Points)
        {
            var rel = grain.Position - cameraPos;
            float fwdC = rel.X * Forward.X + rel.Y * Forward.Y + rel.Z * Forward.Z;  // Dot(rel, forward)
            // Lateral component = rel with its forward projection removed.
            var lat = rel - (fwdC * Forward);
            float latC = lat.Length();

            // Cone invariants (debrisgeo.cpp:137-147):
            //   forward component = dist = maxDistance·√u1  ∈ [0, maxDistance]
            //   lateral |·| = radius = rfocus·dist·√u2      ≤ rfocus·dist
            Assert.InRange(fwdC, 0f, DebrisGeo.MaxDistance);
            Assert.True(latC <= RFocus * fwdC + 1e-3f,
                $"lateral {latC} must not exceed rfocus·forward {RFocus * fwdC}");
        }
    }

    [Fact]
    public void Colours_are_bluish_white()
    {
        // Spawn a few grains and check every channel within the native ranges
        // (debrisgeo.cpp:149-154): r∈[0.5,0.75], g,b∈[0.75,1.0].
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.True(geo.Count > 0);

        foreach (var grain in geo.Points)
        {
            Assert.InRange(grain.Colour.X, 0.50f, 0.75f);   // red 0.5–0.75
            Assert.InRange(grain.Colour.Y, 0.75f, 1.00f);   // green 0.75–1.0
            Assert.InRange(grain.Colour.Z, 0.75f, 1.00f);   // blue 0.75–1.0
        }
    }

    [Fact]
    public void Density_zero_disables_the_field()
    {
        // The brief's "0 = off": a zero density keeps the field empty regardless
        // of camera motion (native's intended semantics, debrisgeo.cpp:102).
        var geo = new DebrisGeo(density: 0f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        geo.Step(new SimVec(3.5f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f);
        Assert.Equal(0, geo.Count);
        geo.Step(new SimVec(50f, 0, 0), Forward, Right, Up, Focus, 76f * 3.5f + 0.1f);
        Assert.Equal(0, geo.Count);
    }

    [Fact]
    public void Density_adjustment_applies_at_low_speed_only()
    {
        // m_speed >= 75 → dpp = density (debrisgeo.cpp:94-95); the else branch is
        // exercised implicitly above. Check the low-speed branch explicitly:
        // density 1.0, m_speed 50 → dpp = 1 - (0.3 - 0.004·50) = 1 - 0.1 = 0.9.
        var geo = new DebrisGeo(density: 1f, new FixedRandom(), new SimVec(0, 0, 0), initialTime: 0f);
        // length = 2, dt = 100 → m_speed = 50 < 75.
        geo.Step(new SimVec(2f, 0, 0), Forward, Right, Up, Focus, 100f);
        Assert.Equal(0.9f, geo.CurrentDistancePerParticle, 3);
    }
}
