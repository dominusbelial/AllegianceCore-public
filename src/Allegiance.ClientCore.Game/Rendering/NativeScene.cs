using System;
using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P1066 shared native scene primitives. Explicit inputs keep desktop
/// orchestration and browser lifecycle independent. Reuse caller-owned buffers;
/// future engine (B2) and combat (B3) passes follow this prepare/draw boundary.</summary>
public static class NativeScene
{
    /// <summary>Native cluster rig: WinTrek/WinTrek.cpp:3634-3640;
    /// Igc/clusterIGC.h:306-334. Unknown clusters keep the desktop fallback.</summary>
    public static SectorLighting Light(DataClusterIGC? cluster) => cluster is { } c
        ? SectorLighting.FromCluster(c.LightDirection, c.LightColor)
        : SectorLighting.FromCluster(null, 0xFFFFFFFF);

    /// <summary>Existing port shimmer, not a native viewport effect.
    /// Native cloak state: WinTrek/consoledata.cpp:480-489,565-575.</summary>
    public static float Opacity(bool cloaked, double seconds) => cloaked ? Flight.CloakVisual.Opacity(seconds) : 1f;

    public static void DrawSky(ISkyRenderer sky, Matrix view, Matrix projection, Vector3 eye, SpaceVisualFrame frame)
        => sky.Draw(view, projection, eye, frame);

    public static void GatherDust(VisualOptions visuals, SubtleDust subtleDust, DebrisGeo debrisGeo,
        SpaceVisualFrame spaceFrame, Vector3 cameraEye, Matrix view, Matrix proj, double gameSeconds,
        List<AmbientDustView> output, BoundingFrustum? frustum = null)
    {
        if (visuals.IsOn(VisualFlags.SubtleDustKey))
        {
            subtleDust.Step(cameraEye, debrisGeo.Density, spaceFrame.Cut);
            output.Clear();
            for (int i = 0; i < subtleDust.Grains.Count; i++)
            {
                var grain = subtleDust.Grains[i];
                float half = grain.HalfExtent / proj.M22;
                if (frustum?.Contains(new BoundingSphere(grain.Position - cameraEye, half * 1.414214f)) == ContainmentType.Disjoint) continue;
                float brightness = grain.Brightness * SubtleDust.DistanceFade(Vector3.Distance(grain.Position, cameraEye));
                output.Add(new AmbientDustView(
                    new SimVec(grain.Position.X, grain.Position.Y, grain.Position.Z),
                    half, new Vector3(brightness)));
            }
        }
        else
        {
            subtleDust.Reset();
            debrisGeo.Step(
                new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z),
                new SimVec(-view.M31, -view.M32, -view.M33),   // camera forward
                new SimVec(view.M11, view.M12, view.M13),      // camera right
                new SimVec(view.M21, view.M22, view.M23),      // camera up
                proj.M22,                                      // focus = cot(vFOV/2)
                (float)gameSeconds);
            float debrisFocus = proj.M22;
            output.Clear();
            for (int i = 0; i < debrisGeo.Points.Count; i++)
            {
                var pt = debrisGeo.Points[i];
                float age = (float)gameSeconds - pt.StartTime;
                float bright = DebrisGeo.Brightness(age);
                if (bright <= 0f) continue;
                float rx = pt.Position.X - cameraEye.X, ry = pt.Position.Y - cameraEye.Y, rz = pt.Position.Z - cameraEye.Z;
                float dsq = rx * rx + ry * ry + rz * rz;
                // Native Render skips grains within 20 m of the camera (debrisgeo.cpp:177).
                if (dsq <= DebrisGeo.NearCameraSkipSquared) continue;
                // P503: native DrawDecal scale = 0.1/focus is a CONSTANT world
                // half-extent for every grain (context.cpp:2317, debrisgeo.cpp:166);
                // the perspective divide then makes a grain subtend 0.1/distance of
                // the screen height — smaller the farther away. The P449a formula
                // (0.1/focus)·distance·Calibration gave every grain a constant
                // ~1.3% of screen height regardless of depth → the "bubbles".
                float half = AmbientDustRenderer.DecalHalfExtent(debrisFocus);
                if (frustum?.Contains(new BoundingSphere(new Vector3(pt.Position.X, pt.Position.Y, pt.Position.Z) - cameraEye,
                        half * 1.414214f)) == ContainmentType.Disjoint) continue;
                output.Add(new AmbientDustView(
                    pt.Position,
                    half,
                    new Vector3(bright * pt.Colour.X, bright * pt.Colour.Y, bright * pt.Colour.Z)));
            }
        }
    }

    /// <summary>Authored station lights: effect/thinggeo.cpp:1482-1510,1618-1630.</summary>
    public static void AppendStationLights(NativeStationRenderer renderer, string model, Vector3 position, Matrix rotation, float radius, float scale, Vector3 eye, Matrix view, Matrix projection, float width,
        float seconds, List<NavStrobeView> output)
    {
        if (NativeStationLights.IsVisible(position - eye, radius, view, projection, width))
            NativeStationLights.Append(renderer.LightsFor(model), rotation * Matrix.CreateTranslation(position), scale, seconds, output);
    }

    /// <summary>Native two-shell build art and rotations: effect/efgeo.cpp:99-227.</summary>
    public static void DrawBuilding(BuildingEffectRenderer renderer, ConstructionPhase phase,
        Vector3 eye, Matrix view, Matrix projection, float seconds)
        => renderer.Draw(phase, eye, view, projection, seconds);

    public static void DrawGlimpses(AlephGlimpseRenderer renderer, IReadOnlyList<GlimpseTarget> targets,
        Vector3 eye, Matrix view, Matrix projection, DataClusterIGC cluster,
        Func<short, float> strength, Func<short, int, int, float, bool> occluded)
        => renderer.Draw(targets, eye, view, projection, cluster, strength, occluded);

    /// <summary>Identical P367 anchors and pulse arithmetic, without a per-ship list.
    /// LightsGeo::Render: effect/efapp.cpp:171-222. Pulse changes size, never RGB.</summary>
    public static void AppendShipStrobes(SimVec position, Orientation orientation, float radius,
        bool visible, float seconds, List<NavStrobeView> output)
    {
        if (!visible || radius <= 0f) return;
        var right = orientation.GetRight();
        var backward = orientation.GetBackward();
        var timing = NavStrobeTiming.Native;
        AppendStrobe(new(position + (-right * (radius * NavStrobeLayout.WingSpanFraction)),
            timing with { Phase = 0f }, .95f, .10f, .050f), radius, seconds, output);
        AppendStrobe(new(position + (right * (radius * NavStrobeLayout.WingSpanFraction)),
            timing with { Phase = timing.Period * .5f }, .035f, .92f, .080f), radius, seconds, output);
        AppendStrobe(new(position + (backward * (radius * NavStrobeLayout.AftFraction)),
            timing with { Phase = timing.Period * .25f }, 1f, 1f, 1f), radius, seconds, output);
    }

    public static void AppendStrobe(NavStrobeAnchor anchor, float radius, float seconds, List<NavStrobeView> output)
    {
        float pulse = NavStrobeMath.Pulse(seconds, anchor.Timing);
        if (pulse <= 0f) return;
        float half = pulse * radius * NavStrobeLayout.SizeFactor;
        output.Add(new(anchor.Position, half, new Vector3(anchor.R, anchor.G, anchor.B)));
    }

    public static void EmitTransitBurst(WarpTransitParticles particles, SimVec position, SimVec velocity,
        SimVec forward, SimVec right, float radius, ITrailRandom random)
        => particles.EmitBurst(position, velocity, forward, right,
            radius > 0f ? radius : WarpTransitParticles.ShipRadiusDefault,
            WarpTransitParticles.EmissionClockSeconds, random);

    /// <summary>P781 fixed 60 Hz emission, at most four ticks after a hitch.
    /// Native intent: effect/thinggeo.cpp:619-696; cadence is the existing port policy.</summary>
    public static void EmitTeleportLeadup(WarpTransitParticles particles, ReadOnlySpan<TeleportEmitter> emitters,
        float frameSeconds, ref double accumulator, ITrailRandom random)
    {
        const float clock = WarpTransitParticles.EmissionClockSeconds;
        accumulator += frameSeconds;
        int ticks = 0;
        while (accumulator >= clock && ticks < 4)
        {
            accumulator -= clock;
            ticks++;
        }
        if (accumulator >= clock) accumulator %= clock;
        for (int tick = 0; tick < ticks; tick++)
            for (int i = 0; i < emitters.Length; i++)
            {
                ref readonly var e = ref emitters[i];
                particles.EmitTeleport(e.Position, e.FrameVelocity, e.Forward, e.Right,
                    e.Radius, e.TimeUntilTeleport, clock, random);
            }
    }

    // ── B2 engine effects (P358 exhaust, P365 nozzle glow, P363 ribbons) ──────

    /// <summary>Native afterburner power ramp rates (CafterburnerIGC::Update,
    /// Igc/afterburnerIGC.cpp:133-146; the port's documented ramp constants).</summary>
    public const float ThrustPowerOnRate = 6f, ThrustPowerOffRate = 3f;

    /// <summary>The ribbon's half-width per side as a fraction of hull radius:
    /// native <c>SetOrientation</c> stores <c>GetRight()·m_radius·0.25</c>
    /// (effect/thinggeo.cpp:1149-1183; trail width used at efgeo.cpp:492-661).</summary>
    public const float TrailWidthFraction = 0.25f;

    /// <summary>P358 — one frame of a ship's afterburner power ramp. Native
    /// <c>AddThrustParticles</c> emits only while <c>m_fThrustPower &gt; 0</c>
    /// (effect/thinggeo.cpp:760); a ship with no mounted afterburner is pinned
    /// to zero (native WinTrek.cpp:8005-8006).</summary>
    public static float ThrustPower(bool hasAfterburner, bool thrusting, float previous, float dtSec)
        => ShipEffectRules.ThrustPower(hasAfterburner, thrusting, previous, dtSec, ThrustPowerOnRate, ThrustPowerOffRate);

    /// <summary>P365 — native <c>UpdateGlow</c> input: full while the afterburner
    /// is engaged, otherwise the ordinary throttle glow (effect/thinggeo.cpp:440-455).</summary>
    public static float NozzleGlow(bool hasAfterburner, int stateM, float throttle)
        => ShipEffectRules.NozzleGlow(hasAfterburner, (stateM & ShipControlState.AfterburnerButton) != 0, throttle);

    /// <summary>P566 — the world-space aft nozzle offset from the model's authored
    /// "rocket"/"thrust"/"smoke" frame: native RY(π) flip negates X and Z
    /// (effect/thinggeo.cpp:1501-1503), then the uniform native scale and the ship
    /// orientation. Null when the model has no such frame (caller uses the
    /// radius-based aft-centre anchor).</summary>
    public static SimVec? AftWorldOffset(IReadOnlyList<MdlFrame>? frames, float nativeScale, Orientation orientation)
    {
        if (frames is null) return null;
        var local = NativeShipResolver.AftNozzleLocalOffset(frames, 0f);
        if (local == Vector3.Zero) return null;
        var flipped = new Vector3(-local.X, local.Y, -local.Z) * nativeScale;
        var x = Vector3.Transform(flipped, PoseConverter.ToXnaRotation(orientation));
        return new SimVec(x.X, x.Y, x.Z);
    }

    /// <summary>P365 — one glow decal: native HSB tint/scale from the live thrust
    /// value (effect/thinggeo.cpp:440-460), anchored at the authored nozzle when
    /// the hull mesh is loaded, else at the documented radius-based aft centre.
    /// Render gate <c>m_scaleGlow &gt; 0</c> is the caller's value&gt;0 check.</summary>
    public static EngineGlowView GlowView(SimVec position, Orientation orientation, float radius, float value,
        SimVec? aftWorldOffset)
    {
        var (r, g, b, scale) = EngineNozzleGlow.Compute(value);
        SimVec anchor = aftWorldOffset is { } offset
            ? position + offset
            : position + (orientation.GetBackward() * (radius * EngineNozzleGlowRenderer.AftCenterFraction));
        return new EngineGlowView(anchor, scale * radius * EngineNozzleGlowRenderer.GlowSizeFactor, new Vector3(r, g, b));
    }

    /// <summary>P363 — feed one ship's ribbon anchor/right/colour to the pool.
    /// Anchor is the ship centre (documented port substitution, see
    /// ShipMotionTrailRibbons); right is <c>GetRight()·radius·0.25</c>; colour is
    /// the real side colour (caller supplies the neutral grey fallback).</summary>
    public static void AppendMotionTrail(ShipMotionTrailRibbons trail, short shipId, SimVec position,
        Orientation orientation, float radius, float r, float g, float b)
        => trail.SetAnchor(shipId, position, orientation.GetRight() * (radius * TrailWidthFraction), r, g, b);
}
