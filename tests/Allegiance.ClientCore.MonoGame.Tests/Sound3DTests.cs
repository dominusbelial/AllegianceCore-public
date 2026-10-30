using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P893 — pure-math tests for the ported native positional-3D sound model
/// (<see cref="Sound3DModel"/>, <see cref="Sound3DMix"/>,
/// <see cref="Native3DMix"/>). Every expected value traces to a native
/// soundengine/WinTrek line (cites inline). No audio device involved.
/// </summary>
public class Sound3DTests
{
    private const float Eps = 1e-4f;

    // ── Distance rolloff (native ds3dvirtualbuffer.cpp:752-762) ─────────────

    [Fact]
    public void DistanceGain_IsZeroDb_AtAndInsideMinimumDistance()
    {
        // min(0, -10·log10(d²/min²)) clamps to 0 dB for d ≤ min.
        Assert.Equal(0f, Sound3DModel.DistanceGainDb(200f, 200f), Eps);
        Assert.Equal(0f, Sound3DModel.DistanceGainDb(200f, 0f), Eps);
        Assert.Equal(0f, Sound3DModel.DistanceGainDb(200f, 50f), Eps);
    }

    [Fact]
    public void DistanceGain_IsMinus20DbPerDecade_OfDistanceOverMinimum()
    {
        // -10·log10((10·min)²/min²) = -10·log10(100) = -20 dB.
        Assert.Equal(-20f, Sound3DModel.DistanceGainDb(200f, 2000f), 0.01f);
        Assert.Equal(-40f, Sound3DModel.DistanceGainDb(200f, 20000f), 0.01f);
        Assert.Equal(-60f, Sound3DModel.DistanceGainDb(200f, 200000f), 0.01f);
    }

    [Fact]
    public void DistanceGain_IsMonotonicNonIncreasing_AndIndependentOfAbsoluteScale()
    {
        // The law depends only on d/min (native uses the ratio d²/min²).
        Assert.Equal(Sound3DModel.DistanceGainDb(100f, 500f),
                     Sound3DModel.DistanceGainDb(1000f, 5000f), 0.01f);
        float prev = 0f;
        for (float d = 1f; d <= 100000f; d *= 1.7f)
        {
            float db = Sound3DModel.DistanceGainDb(100f, d);
            Assert.True(db <= prev + 1e-5f, $"gain rose at d={d}");
            prev = db;
        }
    }

    [Fact]
    public void MaxDistance_Is4000xMinimum_AndAudibleRangeIs1000xMinimum()
    {
        // ds3dbuffer.h:15 (ratio 4000, applied ds3dbuffer.cpp:297);
        // −60 dB (ds3dbuffer.h:12) is reached at 10^(60/20) = 1000 × min.
        Assert.Equal(800000f, Sound3DModel.MaxDistance(200f), Eps);
        Assert.Equal(200000f, Sound3DModel.AudibleRange(200f), Eps);
        Assert.Equal(-60f, Sound3DModel.DistanceGainDb(200f, Sound3DModel.AudibleRange(200f)), 0.01f);
    }

    [Fact]
    public void DbToLinear_MatchesTheDecibelDefinition()
    {
        Assert.Equal(1f, Sound3DModel.DbToLinear(0f), Eps);
        Assert.Equal(0.1f, Sound3DModel.DbToLinear(-20f), 1e-5f);
        Assert.Equal(0.001f, Sound3DModel.DbToLinear(-60f), 1e-6f);
    }

    // ── Cones (native ds3dvirtualbuffer.cpp:765-793) ────────────────────────

    [Fact]
    public void ConeGain_IsZeroInsideInnerAngle_AndOutsideGainAtOrBeyondOuter()
    {
        Assert.Equal(0f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 30f), Eps);
        Assert.Equal(0f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 60f), Eps);
        Assert.Equal(-18f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 120f), Eps);
        Assert.Equal(-18f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 200f), Eps);
    }

    [Fact]
    public void ConeGain_FadesLinearlyInDbBetweenInnerAndOuter()
    {
        // Halfway between inner (60°) and outer (120°) → half the outside gain.
        Assert.Equal(-9f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 90f), Eps);
        // Quarter of the way from inner (60°) to outer (120°) → a quarter of the
        // outside gain (native interpolates 0 → outsideGain linearly in dB).
        Assert.Equal(-4.5f, Sound3DModel.ConeGainDb(60f, 120f, -18f, 75f), Eps);
    }

    [Fact]
    public void ConeGain_DegenerateCones_AreSilent()
    {
        // Native skips cones with outsideGain 0 or a full 360° inner angle
        // (ds3dvirtualbuffer.cpp:771-773).
        Assert.Equal(0f, Sound3DModel.ConeGainDb(60f, 120f, 0f, 180f), Eps);
        Assert.Equal(0f, Sound3DModel.ConeGainDb(360f, 360f, -18f, 180f), Eps);
    }

    [Fact]
    public void ConeAngle_UsesTheAcosDotIdentity()
    {
        Assert.Equal(0f, Sound3DModel.ConeAngleDegrees(Vector3.UnitX, Vector3.UnitX), 0.01f);
        Assert.Equal(90f, Sound3DModel.ConeAngleDegrees(Vector3.UnitX, Vector3.UnitY), 0.01f);
        Assert.Equal(180f, Sound3DModel.ConeAngleDegrees(Vector3.UnitX, -Vector3.UnitX), 0.01f);
    }

    // ── Doppler (native factor 1.0, ds3dengine.cpp:237; 343 m/s) ────────────

    [Fact]
    public void Doppler_ApproachingShiftsUp_AtTheExpectedSize()
    {
        // Listener still at origin; emitter 1000 m down +X closing at 100 m/s:
        // f' = 343 / (343 − 100) ≈ 1.4118 (> 1, upshift).
        float f = Sound3DModel.DopplerPitchFactor(Vector3.Zero, -Vector3.UnitX * 100f, Vector3.UnitX * 1000f);
        Assert.Equal(343f / 243f, f, 0.001f);
        Assert.True(f > 1f);
        // In MonoGame octaves that's a positive pitch delta.
        Assert.Equal(MathF.Log2(343f / 243f), Sound3DModel.DopplerPitchOctaves(f), 0.001f);
    }

    [Fact]
    public void Doppler_RecedingShiftsDown_Symmetrically()
    {
        float up = Sound3DModel.DopplerPitchFactor(Vector3.Zero, -Vector3.UnitX * 100f, Vector3.UnitX * 1000f);
        float down = Sound3DModel.DopplerPitchFactor(Vector3.Zero, Vector3.UnitX * 100f, Vector3.UnitX * 1000f);
        Assert.Equal(343f / 443f, down, 0.001f);
        Assert.True(down < 1f);
        // Sign is correct and the magnitudes are the factor pair of the same 100 m/s.
        Assert.Equal(343f * 343f / (243f * 443f), up * down, 0.001f);
    }

    [Fact]
    public void Doppler_IsZero_WhenThereIsNoRadialRelativeVelocity()
    {
        // Emitter crossing sideways at a constant distance → no shift.
        float f = Sound3DModel.DopplerPitchFactor(Vector3.Zero, Vector3.UnitY * 100f, Vector3.UnitX * 1000f);
        Assert.Equal(1f, f, Eps);
        // Both at rest → no shift. Both with equal velocity → no shift.
        Assert.Equal(1f, Sound3DModel.DopplerPitchFactor(Vector3.Zero, Vector3.Zero, Vector3.UnitX * 10f), Eps);
        Assert.Equal(1f, Sound3DModel.DopplerPitchFactor(Vector3.UnitZ * 50f, Vector3.UnitZ * 50f, Vector3.UnitX * 10f), Eps);
    }

    [Fact]
    public void Doppler_ListenerMotion_Counts()
    {
        // Listener flying TOWARD a still emitter 1000 m ahead at 100 m/s → upshift.
        float f = Sound3DModel.DopplerPitchFactor(Vector3.UnitX * 100f, Vector3.Zero, Vector3.UnitX * 1000f);
        Assert.Equal(343f / 243f, f, 0.001f);
    }

    // ── Pan (the stereo down-mix DirectSound produces for a 3D source) ──────

    [Fact]
    public void Pan_ProjectsOntoTheListenerRightAxis()
    {
        Assert.Equal(1f, Sound3DModel.Pan(Vector3.UnitX * 500f, Vector3.UnitX), Eps);
        Assert.Equal(-1f, Sound3DModel.Pan(-Vector3.UnitX * 500f, Vector3.UnitX), Eps);
        Assert.Equal(0f, Sound3DModel.Pan(Vector3.UnitZ * 500f, Vector3.UnitX), Eps);   // dead ahead
        Assert.Equal(0f, Sound3DModel.Pan(-Vector3.UnitZ * 500f, Vector3.UnitX), Eps);  // dead behind
        Assert.Equal(0f, Sound3DModel.Pan(Vector3.Zero, Vector3.UnitX), Eps);           // at the listener
    }

    // ── Native minimum distances (sounddef.mdl ThreeDSound values) ──────────

    [Fact]
    public void NativeMinDistances_MatchTheSounddefCites()
    {
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.ProjectileAmbient), Eps);  // er_beam, :14
        Assert.Equal(100f, Native3DMix.MinDistance(Native3DRole.MissileFlight), Eps);      // :1303
        Assert.Equal(20f, Native3DMix.MinDistance(Native3DRole.MissileLaunch), Eps);       // :1302
        Assert.Equal(1000f, Native3DMix.MinDistance(Native3DRole.ExplosionLarge), Eps);    // :268
        Assert.Equal(400f, Native3DMix.MinDistance(Native3DRole.ExplosionMedium), Eps);    // :269
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.ExplosionSmall), Eps);     // :270
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.ShipHit), Eps);            // :250/:255
        Assert.Equal(100f, Native3DMix.MinDistance(Native3DRole.RockHit), Eps);            // :266
        Assert.Equal(100f, Native3DMix.MinDistance(Native3DRole.Collision), Eps);          // :308
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.WarpAmbient), Eps);        // :385
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.StationAmbient), Eps);     // :1156
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.AsteroidAmbient), Eps);    // :382
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.ProbeAmbient), Eps);       // :14
        Assert.Equal(200f, Native3DMix.MinDistance(Native3DRole.MiningLoop), Eps);         // :379
    }

    // ── The mix pass ────────────────────────────────────────────────────────

    private static Listener3D Listener(Vector3 right) =>
        new(Position: Vector3.Zero, Velocity: Vector3.Zero, Right: right, HasBasis: true);

    [Fact]
    public void Solve_OptionOff_IsExactlyTheBaseState()
    {
        // Option off ⇒ no pan, no pitch delta, the planner's loudness verbatim —
        // structurally today's (pre-P893) mix.
        var voice = new Voice3DState(0.42f, -0.35f,
            new World3DEmitter(new Vector3(5000f, 0, 0), new Vector3(0, 0, 100f)),
            Native3DRole.ProjectileAmbient);
        var r = Sound3DMix.Solve(voice, Listener(Vector3.UnitX), enabled3D: false);
        Assert.Equal(0.42f, r.Loudness, Eps);
        Assert.True(r.Audible);
        Assert.Equal(0f, r.Pan, Eps);
        Assert.Equal(0f, r.PitchDelta, Eps);
    }

    [Fact]
    public void Solve_AtTheListener_IsHeadRelativeFullVolume()
    {
        // Native's equal-position optimization (ds3dvirtualbuffer.cpp:723-726):
        // full volume, centred, unshifted.
        var voice = new Voice3DState(0.5f, 0f,
            new World3DEmitter(Vector3.Zero, Vector3.Zero), Native3DRole.MissileFlight);
        var r = Sound3DMix.Solve(voice, Listener(Vector3.UnitX), enabled3D: true);
        Assert.Equal(1f, r.Loudness, Eps);
        Assert.True(r.Audible);
        Assert.Equal(0f, r.Pan, Eps);
        Assert.Equal(0f, r.PitchDelta, Eps);
    }

    [Fact]
    public void Solve_AttenuatesByTheNativeLaw()
    {
        var voice = new Voice3DState(1f, 0f,
            new World3DEmitter(new Vector3(20000f, 0, 0), Vector3.Zero), Native3DRole.ProjectileAmbient);
        var r = Sound3DMix.Solve(voice, Listener(Vector3.UnitX), enabled3D: true);
        // 20000 = 100 × min(200) → −40 dB → linear 0.01.
        Assert.Equal(0.01f, r.Loudness, 1e-5f);
        Assert.True(r.Audible);
    }

    [Fact]
    public void Solve_BeyondTheNativeAudibility_IsInaudible()
    {
        var voice = new Voice3DState(1f, 0f,
            new World3DEmitter(new Vector3(Sound3DModel.AudibleRange(200f) * 2f, 0, 0), Vector3.Zero),
            Native3DRole.ProjectileAmbient);
        var r = Sound3DMix.Solve(voice, Listener(Vector3.UnitX), enabled3D: true);
        Assert.False(r.Audible);
        Assert.True(r.Loudness <= Sound3DModel.DbToLinear(Sound3DModel.MinAudibleDb));
    }

    [Fact]
    public void Solve_PansBySide_AndKeepsAheadSourcesCentred()
    {
        var right = new Voice3DState(1f, 0f,
            new World3DEmitter(new Vector3(1000f, 0, 0), Vector3.Zero), Native3DRole.StationAmbient);
        var ahead = new Voice3DState(1f, 0f,
            new World3DEmitter(new Vector3(0, 0, -1000f), Vector3.Zero), Native3DRole.StationAmbient);
        Assert.Equal(1f, Sound3DMix.Solve(right, Listener(Vector3.UnitX), true).Pan, 0.001f);
        Assert.Equal(0f, Sound3DMix.Solve(ahead, Listener(Vector3.UnitX), true).Pan, Eps);
    }

    [Fact]
    public void Solve_ApproachingEmitter_ShiftsPitchUp()
    {
        // Emitter 1000 m ahead (+Z is behind; forward is −Z) closing at 100 m/s.
        var voice = new Voice3DState(1f, -0.35f,
            new World3DEmitter(new Vector3(0, 0, -1000f), new Vector3(0, 0, 100f)),
            Native3DRole.MissileFlight);
        var r = Sound3DMix.Solve(voice, Listener(Vector3.UnitX), true);
        Assert.True(r.PitchDelta > 0f, "an approaching missile must shift up");
        Assert.Equal(MathF.Log2(343f / 243f), r.PitchDelta, 0.001f);
    }

    [Fact]
    public void Solve_WithoutListenerBasis_PansCentred()
    {
        var voice = new Voice3DState(1f, 0f,
            new World3DEmitter(new Vector3(1000f, 0, 0), Vector3.Zero), Native3DRole.StationAmbient);
        var noBasis = new Listener3D(Vector3.Zero, Vector3.Zero, Vector3.Zero, HasBasis: false);
        Assert.Equal(0f, Sound3DMix.Solve(voice, noBasis, true).Pan, Eps);
    }
}
