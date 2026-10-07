using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P561 — pure sizing and visibility rules for the ENHANCED smoke/fire path,
/// so every knob is unit-testable headless and every size is derived from the
/// NATIVE size curves / emitter sizes (see <see cref="NativeParticleCurves"/>
/// and the native <c>particlegeo.cpp</c> / <c>thinggeo.cpp</c> cites in the
/// trail-particle pools). The whole point of this class is to kill three P546
/// complaints at once:
/// <list type="number">
/// <item><b>oversized smoke</b> — the enhanced path drew each puff at
/// <c>p.Size * 2.4 * (0.5 + 2.2·(1−rem))</c> ≈ <b>3.2–4.3× native</b> (native
/// draws <c>p.Size · 0.5 · SmokeSizeScale(rem)</c>); here the enhanced half-size
/// is derived FROM native and kept ≤ <see cref="TrailMaxScale"/> (1.5)× native;</item>
/// <item><b>discrete ovals from afar</b> — puffs are smaller and split into
/// <see cref="TrailSubPuffs"/> overlapping sub-puffs (a continuous plume, not
/// beads), feathered at the sprite rim (VfxAssets) and faded by distance
/// (<see cref="ParticleFarFade"/>);</item>
/// <item><b>chase-view own-ship occlusion</b> — particles between the camera
/// and the own ship that overlap its screen footprint are faded
/// (<see cref="ChaseOccluder"/>).</item>
/// </list>
/// All methods are pure (no graphics device).
/// </summary>
public static class EnhancedSmokeSizing
{
    // ── 1) Trail smoke size, derived from native ───────────────────────────
    /// <summary>
    /// The half-size native actually renders a smoke puff at:
    /// <c>p.Size · 0.5 · SmokeSizeScale(rem)</c> (native <c>SmokeBehavior::Render</c>,
    /// <c>particlegeo.cpp:51-54</c>; the trail renderers' native branch,
    /// MissileTrailRenderer.cs:98 / ShipEngineTrailRenderer.cs:76). <paramref name="rem"/>
    /// is the remaining-lifetime fraction (1 = newborn → 0 = dying).
    /// </summary>
    public static float NativeTrailHalf(float pSize, float rem)
        => pSize * 0.5f * NativeParticleCurves.SmokeSizeScale(rem);

    /// <summary>
    /// The hard cap on the enhanced trail half-size, as a multiple of native:
    /// the enhanced puff is allowed at most <see cref="TrailMaxScale"/>× native
    /// (P561 scope 1: "≤ 1.5× native"). The P546 path was ~3.2–4.3× native.
    /// </summary>
    public const float TrailMaxScale = 1.5f;

    /// <summary>Nominal enhanced-trail scale (≤ <see cref="TrailMaxScale"/>).</summary>
    public const float TrailScaleNominal = 1.25f;

    /// <summary>Per-puff size jitter (±, multiplicative, applied AFTER the
    /// nominal scale) — the max (nominal × <see cref="TrailJitterMax"/>) stays
    /// ≤ <see cref="TrailMaxScale"/>.</summary>
    public const float TrailJitterMin = 0.92f, TrailJitterMax = 1.028f;

    /// <summary>How many overlapping sub-puffs each native trail smoke particle
    /// is split into when drawing the ENHANCED plume (smaller puffs that overlap
    /// merge into a continuous trail instead of separate ovals).</summary>
    public const int TrailSubPuffs = 3;

    /// <summary>
    /// The enhanced trail smoke half-size, derived from native and capped at
    /// ≤ <see cref="TrailMaxScale"/>× native: native half ×
    /// <see cref="TrailScaleNominal"/> × per-puff jitter in
    /// [<see cref="TrailJitterMin"/>, <see cref="TrailJitterMax"/>]. Guaranteed
    /// ≤ <c>NativeTrailHalf · TrailMaxScale</c> for every life fraction.
    /// </summary>
    public static float EnhancedTrailHalf(float pSize, float rem, float jitter = 1f)
        => NativeTrailHalf(pSize, rem) * TrailScaleNominal * Math.Clamp(jitter, TrailJitterMin, TrailJitterMax);

    /// <summary>The worst-case enhanced trail half (nominal × max jitter), the
    /// value the size-parity test pins ≤ <see cref="TrailMaxScale"/>× native.</summary>
    public static float EnhancedTrailHalfMax(float pSize, float rem)
        => NativeTrailHalf(pSize, rem) * TrailScaleNominal * TrailJitterMax;

    // ── 2) Explosion smoke size, derived from the native explosion radius ──
    /// <summary>
    /// Native's AddExplosion switch (<c>trekigc.cpp:95-177</c>) draws NO smoke —
    /// only fireball films + a shock ring. The natural native size reference for
    /// the enhanced plume is therefore the explosion's own
    /// <see cref="ExplosionPreset.RadiusExplosion"/> (the fireball decal size).
    /// The enhanced smoke puff half-size is a small fraction of that radius,
    /// grown with native's <c>SmokeSizeScale</c> (0.75→3.0×) so the puff swells
    /// exactly like native smoke but never rivals the fireball:
    /// <c>radius · ExplosionSmokeBase · SmokeSizeScale(rem)</c> ≤ ~0.18·radius.
    /// The P546 path drew <c>radius · 0.30 · (1 + 2.2·(1−rem))</c> ≈ up to 0.96·radius
    /// — a smoke puff as big as the whole blast, dwarfing the exploding hull.
    /// </summary>
    public const float ExplosionSmokeBase = 0.053f;

    public static float ExplosionSmokeHalf(float radius, float rem, float jitter = 1f)
        => radius * ExplosionSmokeBase * NativeParticleCurves.SmokeSizeScale(rem) * Math.Clamp(jitter, 0.9f, 1.1f);

    /// <summary>Worst-case explosion smoke half (max jitter) — used by the size
    /// sheet so the plume is guaranteed to stay well inside the explosion.</summary>
    public static float ExplosionSmokeHalfMax(float radius, float rem)
        => radius * ExplosionSmokeBase * NativeParticleCurves.SmokeSizeScale(rem) * 1.1f;

    /// <summary>Kept true once all constants land: max explosion smoke half is a
    /// small fraction (≤ 0.2) of the explosion radius.</summary>
    public static bool ExplosionSmokeWithinExplosion(float radius)
        => ExplosionSmokeHalfMax(radius, 0f) <= radius * 0.2f;

    // ── 3) Trail-plume subdivision (Priority 2: merge puffs, no ovals) ──────
    /// <summary>Multiplier on the (already native-derived) trail half applied to
    /// EACH sub-puff so that <see cref="TrailSubPuffs"/> overlapping sub-puffs
    /// keep roughly the same footprint as the single parent puff while reading
    /// as a softer, denser plume.</summary>
    public const float TrailSubScale = 0.68f;

    /// <summary>Split one native trail smoke particle (P546 drew a single big
    /// puff) into <see cref="TrailSubPuffs"/> smaller overlapping sub-puffs with
    /// per-puff random offset, rotation seed, size jitter and a slightly varied
    /// remaining fraction, so the plume is continuous instead of a string of
    /// ovals. Deterministic given <paramref name="seed"/>. Pure.</summary>
    public static void SplatTrailPuffs(Vector3 pos, float pSize, float rem, uint seed,
        ICollection<EnhancedSmokeField.Puff> into)
    {
        float subHalf = EnhancedTrailHalf(pSize, rem) * TrailSubScale;
        uint state = EnhancedVfx.Hash(seed ^ 0x9E3779B9u);
        for (int i = 0; i < TrailSubPuffs; i++)
        {
            uint s = EnhancedVfx.Hash(state ^ (uint)(i * 2654435761u));
            // Small random world offset (~ up to half the sub-puff radius) so
            // the sub-puffs overlap and merge into a volume.
            float r1 = EnhancedVfx.Rand01(ref state), r2 = EnhancedVfx.Rand01(ref state);
            float mag = subHalf * (0.25f + 0.6f * r1);
            float theta = r2 * MathF.PI * 2f, phi = EnhancedVfx.Rand01(ref state) * MathF.PI;
            var off = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Sin(phi) * MathF.Sin(theta), MathF.Cos(phi)) * mag;
            // Slight per-puff life jitter so the tail disperses softly.
            float subRem = Math.Clamp(rem - i * 0.030f + EnhancedVfx.Rand01(ref state) * 0.050f, 0f, 1f);
            float jitter = TrailJitterMin + (TrailJitterMax - TrailJitterMin) * EnhancedVfx.Rand01(ref state);
            float angle = EnhancedVfx.Rand01(ref state) * MathF.PI * 2f;
            into.Add(new EnhancedSmokeField.Puff(pos + off, subHalf, subRem, angle, jitter, s));
        }
    }
}

/// <summary>
/// P561 — the distance fade for the ENHANCED smoke, complementary to the
/// existing <see cref="ParticleNearFade"/> (which clears smoke AT the eye).
/// <see cref="ParticleFarFade"/> fades the DISTANT puffs of a trail/plume down
/// in alpha (and slightly in size) so a long trail reads as a narrowing line
/// rather than a string of distinct beads — Domingo's "from far it looks like
/// lots of ovals". Full below <see cref="FadeStart"/> metres, linearly
/// approaching <see cref="FarAlpha"/> between start and <see cref="FadeEnd"/>.
/// Pure and unit-testable headless.
/// </summary>
public readonly record struct ParticleFarFade(bool Enabled, float FadeStart, float FadeEnd, float FarAlpha, float FarSizeScale)
{
    /// <summary>The far-fade alpha multiplier for a puff <paramref name="dist"/>
    /// metres from the eye: 1.0 inside (≤ <paramref name="fadeStart"/>), down to
    /// <paramref name="farAlpha"/> at/ beyond <paramref name="fadeEnd"/>, linear
    /// between. Disabled → 1.0.</summary>
    public static float AlphaFactor(float dist, bool enabled, float fadeStart, float fadeEnd, float farAlpha)
    {
        if (!enabled) return 1f;
        float span = fadeEnd - fadeStart;
        if (span <= 0f) return dist >= fadeEnd ? farAlpha : 1f;
        float t = MathHelper.Clamp((dist - fadeStart) / span, 0f, 1f);
        return 1f - (1f - farAlpha) * t;
    }

    /// <summary>The far-fade size multiplier: 1.0 inside, down to
    /// <paramref name="farSizeScale"/> beyond <paramref name="fadeEnd"/>.</summary>
    public static float SizeFactor(float dist, bool enabled, float fadeStart, float fadeEnd, float farSizeScale)
    {
        if (!enabled) return 1f;
        float span = fadeEnd - fadeStart;
        if (span <= 0f) return dist >= fadeEnd ? farSizeScale : 1f;
        float t = MathHelper.Clamp((dist - fadeStart) / span, 0f, 1f);
        return 1f - (1f - farSizeScale) * t;
    }

    /// <summary>The default: fade only the genuinely distant part of a trail
    /// (past ~600&nbsp;u), bottoming at ~35% alpha / ~0.75 size by ~2600&nbsp;u.</summary>
    public static readonly ParticleFarFade Flight = new(true, 650f, 2600f, 0.35f, 0.75f);

    /// <summary>Disabled (no far fade).</summary>
    public static readonly ParticleFarFade Off = new(false, 0f, 0f, 1f, 1f);
}

/// <summary>
/// P561 — the chase-view own-ship occluder. In the external/chase camera the
/// own ship's engine, freshly-launched-missile and damage smoke sits between
/// the camera and the ship and blankets it (Domingo's "the smoke is so heavy
/// they block the external view"). This occluder fades any particle that lies
/// (a) nearer to the camera than the own ship's surface and (b) inside an
/// inflated screen-space ellipse around the ship's projected bounds, so the
/// ship stays readable in the chase view while genuine background smoke is
/// unaffected. Pure (vector math only) — the renderer projects each puff and
/// multiplies its alpha by <see cref="AlphaFactor"/>.
/// </summary>
public readonly record struct ChaseOccluder(bool Active, Vector2 ShipNdc, float ShipHalfWNdc, float ShipHalfHNdc, float ShipDepth)
{
    /// <summary>
    /// Build the occluder for the own ship at <paramref name="shipWorld"/> with
    /// world <paramref name="radius"/> from a camera at <paramref name="eye"/>,
    /// using the frame's <paramref name="view"/>/<paramref name="projection"/>.
    /// Projects the ship centre plus a right/up offset of <paramref name="radius"/>
    /// to NDC to derive the screen ellipse and the ship's clip depth. The ellipse
    /// is inflated by <paramref name="inflate"/> (≥1) to cover the thruster/
    /// muzzle smoke just beside the hull. Returns an inactive occluder when the
    /// ship is behind the camera or off the near plane.
    /// </summary>
    public static ChaseOccluder Create(
        Vector3 shipWorld, float radius, Vector3 eye, Matrix view, Matrix projection,
        float inflate = 1.45f)
    {
        var shipCam = shipWorld - eye;
        if (!ProjectToNdc(shipCam, projection, out var centre, out float shipDepth))
            return new ChaseOccluder(false, default, 0f, 0f, 0f);

        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);
        // Right/up extents of the ship in world units → NDC deltas from centre.
        if (!ProjectToNdc(shipCam + camRight * radius, projection, out var rightNdc, out _) ||
            !ProjectToNdc(shipCam + camUp * radius, projection, out var upNdc, out _))
            return new ChaseOccluder(false, default, 0f, 0f, 0f);

        float halfW = MathF.Abs(rightNdc.X - centre.X) * inflate;
        float halfH = MathF.Abs(upNdc.Y - centre.Y) * inflate;
        if (halfW <= 1e-4f || halfH <= 1e-4f || shipDepth < 0f)
            return new ChaseOccluder(false, default, 0f, 0f, 0f);
        return new ChaseOccluder(true, centre, halfW, halfH, shipDepth);
    }

    /// <summary>
    /// The alpha multiplier for a puff at camera-space position
    /// <paramref name="puffCam"/> (eye-relative) with the frame's
    /// <paramref name="projection"/>: 1.0 when the occluder is inactive, the
    /// puff is at/behind the ship's depth, or outside the ellipse; smoothly
    /// toward 0 for puffs inside the ellipse nearer than the ship. Pure.
    /// </summary>
    public static float AlphaFactor(in ChaseOccluder occ, Vector3 puffCam, Matrix projection)
    {
        if (!occ.Active) return 1f;
        if (!ProjectToNdc(puffCam, projection, out var puffNdc, out float puffDepth)) return 1f;
        // Behind the ship → cannot be occluding it.
        if (puffDepth >= occ.ShipDepth) return 1f;

        float dx = (puffNdc.X - occ.ShipNdc.X) / (occ.ShipHalfWNdc > 1e-4f ? occ.ShipHalfWNdc : 1e-4f);
        float dy = (puffNdc.Y - occ.ShipNdc.Y) / (occ.ShipHalfHNdc > 1e-4f ? occ.ShipHalfHNdc : 1e-4f);
        float r2 = dx * dx + dy * dy;
        // Inside the inner core (r < 0.72) → fully hidden; feather out to the
        // ellipse edge (r = 1.0) → fully visible.
        float r = MathF.Sqrt(Math.Max(0f, r2));
        float t = MathHelper.Clamp((r - 0.72f) / (1f - 0.72f), 0f, 1f);
        return t * t * (3f - 2f * t);   // smoothstep: 0 in core, 1 at edge
    }

    /// <summary>Project a camera-space point to NDC [−1,1] and return its NDC
    /// depth (clip.z/clip.w in [0,1]). False when behind the near plane.</summary>
    private static bool ProjectToNdc(Vector3 camPos, Matrix projection, out Vector2 ndc, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(camPos, 1f), projection);
        if (MathF.Abs(clip.W) < 1e-6f) { ndc = default; depth = 1f; return false; }
        float invW = 1f / clip.W;
        ndc = new Vector2(clip.X * invW, clip.Y * invW);
        depth = clip.Z * invW;
        return clip.W > 0f;
    }
}
