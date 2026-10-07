using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P546 — the <b>Enhanced</b> explosion renderer (adopted from the P543 spike's
/// <c>EnhancedExplosionRenderer</c> into the game, reworked onto the shared
/// <see cref="ParticleBillboardBatcher"/>). Same
/// <see cref="ExplosionPreset"/> / <see cref="ExplosionSampler"/> timing and
/// size as the native-art <see cref="ExplosionRenderer"/> — parity — but the
/// fire-ball and smoke layers use the CC0 flipbook atlases from
/// <see cref="VfxAssets"/> and are stacked as separate passes:
/// <list type="number">
/// <item><b>flash</b> — a brief bright additive core during the first
/// ~0.12&nbsp;s;</item>
/// <item><b>fire-ball glow</b> — additive CC0 fire-ball flipbook decals at the
/// sampler's staggered offsets, with <b>capped intensity</b> so stacked blasts
/// don't blow out to white;</item>
/// <item><b>sparks</b> — additive streak quads thrown outward in the first
/// ~0.3&nbsp;s;</item>
/// <item><b>shock wave</b> — the original fx18 ring (when the category has
/// one);</item>
/// <item><b>smoke</b> — premultiplied CC0 smoke via the shared
/// <see cref="EnhancedSmokeField"/>, tinted by the P530 sector light, lingering
/// longer than the fire ball, near-camera-faded and density-budgeted.</item>
/// </list>
/// The smoke pass is deliberately NOT additive and uses premultiplied-alpha
/// blending (with a per-particle alpha budget) so overlapping effects keep
/// their contrast instead of clipping to white — the saturation-control item.
/// </summary>
public sealed class EnhancedExplosionRenderer : IDisposable
{
    private readonly VfxAssets _vfx;
    private readonly Texture2D? _shockwave;
    private readonly ParticleBillboardBatcher _batcher;
    private readonly EnhancedSmokeField _smoke;

    /// <summary>Smoke lingers <see cref="LingerExtraSeconds"/> past the fire
    /// ball's last frame. P561 shortened this 1.2 → 0.8 so a spent blast's
    /// plume disperses sooner (shorter smoke exposure = less chance to blanket
    /// the view).</summary>
    public const float LingerExtraSeconds = 0.8f;

    /// <summary>CC0 atlas playback rate (parity with ExplosionSampler.FramesPerSecond).</summary>
    public const float Fps = 20f;

    /// <summary>Per-explosion smoke cap (density budget — smoke thins under
    /// load; see <see cref="EnhancedSmokeField.MaxSmokeOnScreen"/> for the
    /// screen-wide cap). P561 trimmed this 14 → 10 (fewer, tighter puffs after
    /// the size reduction keeps the plume from overflowing the hull).</summary>
    public const int MaxSmokePerExplosion = 10;

    /// <summary>Cap on the number of stacked fire-ball decals per blast.</summary>
    public const int MaxFireDecals = 8;

    /// <summary>Peak per-decal fire-ball alpha — fire is additive but intensity-
    /// capped below white so stacked blasts stay readable (never 1.0). Tuned so
    /// that even a dense stack of overlapping fire-balls stays below the
    /// blown-out threshold (dense-combat target ≤ ~15%), matching the P543 spike.</summary>
    public const float FireballMaxAlpha = 0.75f;

    /// <param name="smoke">The shared enhanced smoke field — the explosion plume
    /// and the emitter trails use the SAME field so the on-screen density budget
    /// is enforced globally across all smoke sources.</param>
    public EnhancedExplosionRenderer(GraphicsDevice gd, VfxAssets vfx, ExplosionAssets? original,
        EnhancedSmokeField smoke)
    {
        _vfx = vfx;
        _shockwave = original?.Shockwave;
        _batcher = new ParticleBillboardBatcher(gd);
        _smoke = smoke;
    }

    /// <summary>
    /// Draw every live explosion in the local sector, enhanced. Timing/size from
    /// <see cref="ExplosionSampler"/> (native parity); <paramref name="sectorLight"/>
    /// tints the smoke; <paramref name="near"/> enables the near-camera smoke fade.
    /// <paramref name="soft"/> (P547) enables the soft-particle depth-fade for the
    /// smoke plume when available (falls back to near-fade only).
    /// </summary>
    public void Draw(
        IReadOnlyList<ExplosionEntity> explosions,
        short ownSectorId,
        long nowLocalMs,
        XnaVec cameraEye,
        Matrix view,
        Matrix projection,
        SectorLighting? sectorLight,
        ParticleNearFade near = default,
        ParticleFarFade far = default,
        ChaseOccluder occ = default,
        SoftParticleContext? soft = null)
    {
        if (explosions.Count == 0) return;

        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);

        var glow = new List<ParticleBillboard>();
        var flash = new List<ParticleBillboard>();
        var sparks = new List<ParticleBillboard>();
        var wave = new List<ParticleBillboard>();
        var smoke = new List<EnhancedSmokeField.Puff>();

        foreach (var e in explosions)
        {
            if (e.ClusterId != ownSectorId) continue;
            ExplosionPreset preset;
            try { preset = ExplosionPresets.For(e.Type); }
            catch (ArgumentOutOfRangeException) { continue; }

            float elapsed = Math.Max(0f, (nowLocalMs - e.ReceivedLocalMs) / 1000.0f);
            var sample = ExplosionSampler.Sample(preset, elapsed);
            var centre = new XnaVec(e.Position.X + e.Velocity.X * elapsed,
                                    e.Position.Y + e.Velocity.Y * elapsed,
                                    e.Position.Z + e.Velocity.Z * elapsed) - cameraEye;

            // Smoke — emitted before/during/after the fire ball (born >0 earlier),
            // drawn through the shared (budgeted) smoke field.
            int nSmoke = SmokeCount(preset);
            for (int i = 0; i < nSmoke; i++)
                if (TrySmokePuff(i, e, preset, elapsed, cameraEye, near, sectorLight, camRight, camUp) is { } p)
                    smoke.Add(p);

            // 1) FLASH — brief bright core, first ~0.12 s.
            if (elapsed < 0.12f)
            {
                float k = elapsed / 0.12f;
                float half = preset.RadiusExplosion * (0.9f - 0.55f * k);
                float a = (1f - k) * 0.9f;
                flash.Add(new ParticleBillboard(centre, half,
                    new Color(1f, 0.96f, 0.9f, a), 0f));
            }

            // 2) FIRE-BALL GLOW — additive CC0 flipbook, sampler offsets, capped
            //    decal count and per-frame alpha so stacked blasts stay readable.
            int nFire = Math.Min(preset.CountDecals, MaxFireDecals);
            for (int i = 0; i < nFire && i < sample.Fireballs.Count; i++)
            {
                var fb = sample.Fireballs[i];
                int frame = Math.Abs((int)(elapsed * Fps) + i * 3) % Math.Max(1, _vfx.FireGlow.Length);
                var off = new XnaVec(fb.Offset.X, fb.Offset.Y, fb.Offset.Z);
                float half = preset.RadiusExplosion * (0.38f + 0.080f * (i % 3));
                float a = ElapsedFade(elapsed, preset.TotalDurationSeconds) * FireballMaxAlpha;
                if (_vfx.FireGlow.Length > 0)
                    glow.Add(new ParticleBillboard(centre + off, half,
                        new Color(1f, 1f, 1f, a), fb.Angle));
            }

            // 3) SPARKS — additive streaks thrown outward, first ~0.3 s.
            if (elapsed < 0.30f)
            {
                for (int i = 0; i < 10; i++)
                {
                    uint seed = EnhancedVfx.Hash((uint)(i * 7919 + (int)(e.Position.X * 7f)));
                    float ang = (seed % 628u) / 100f;
                    var dir = new XnaVec(MathF.Cos(ang), MathF.Sin(ang) * 0.6f, MathF.Sin(ang));
                    float k = elapsed / 0.30f;
                    float half = preset.RadiusExplosion * (0.025f + 0.035f * k);
                    float a = (1f - k) * 0.8f;
                    var basePos = centre + dir * preset.RadiusExplosion * 0.2f;
                    sparks.Add(new ParticleBillboard(basePos, half,
                        new Color(1f, 0.85f, 0.5f, a), ang));
                }
            }

            // 4) SHOCK WAVE — the original fx18 ring (additive).
            if (sample.Shockwave is { } sw && _shockwave is not null)
            {
                var col = new Color((int)(preset.ShockR * sw.Bright * 255f),
                                    (int)(preset.ShockG * sw.Bright * 255f),
                                    (int)(preset.ShockB * sw.Bright * 255f), 255);
                wave.Add(new ParticleBillboard(centre, sw.Scale, col, 0f));
            }
        }

        // Pass A: additive layers (grouped per texture by the batcher's caller).
        DrawBatched(glow, _vfx.FireGlow, view, projection);
        DrawBatched(flash, _vfx.SoftDisc, view, projection);
        DrawBatched(sparks, _vfx.SoftDisc, view, projection);
        // Shock wave uses the native fx18 ring texture.
        if (_shockwave is not null) DrawBatched(wave, _shockwave, view, projection);

        // Pass B: premultiplied smoke through the shared (budgeted) field.
        if (smoke.Count > 0)
            _smoke.Draw(smoke, cameraEye, view, projection, sectorLight, near, far, occ, soft);
    }

    public static float ElapsedFade(float t, float total)
    {
        if (t > total * 0.7f) return MathHelper.Clamp((total - t) / (total * 0.3f), 0f, 1f);
        return 1f;
    }

    private static int SmokeCount(ExplosionPreset preset)
        => Math.Clamp((int)(preset.RadiusExplosion / 28f) + 3, 5, MaxSmokePerExplosion);

    private EnhancedSmokeField.Puff? TrySmokePuff(int i, ExplosionEntity e, ExplosionPreset preset,
        float elapsed, XnaVec cameraEye, ParticleNearFade near, SectorLighting? sectorLight,
        XnaVec camRight, XnaVec camUp)
    {
        float life = preset.TotalDurationSeconds + LingerExtraSeconds;
        uint seed = EnhancedVfx.Hash((uint)(i * 2654435761u + (uint)(e.Position.X * 31f)));
        float born = EnhancedVfx.Rand01(ref seed) * 0.5f * preset.TotalDurationSeconds;
        float local = elapsed - born;
        if (local < 0f || local > life) return null;

        float rem = 1f - local / life;
        float alpha = MathHelper.Clamp(rem * rem, 0f, 1f) * 0.5f;

        var dir = new XnaVec(EnhancedVfx.Rand01(ref seed) - 0.5f, 1f + (EnhancedVfx.Rand01(ref seed) - 0.5f),
                             EnhancedVfx.Rand01(ref seed) - 0.5f);
        float spread = preset.RadiusExplosion * 0.55f * MathHelper.Clamp(local / 0.5f, 0f, 1f);
        var pos = new XnaVec(e.Position.X + dir.X * spread,
                             e.Position.Y + dir.Y * spread * 0.4f,
                             e.Position.Z + dir.Z * spread);
        // P561 — the explosion smoke half derives from the NATIVE explosion
        // radius via native's SmokeBehaviour growth (0.75→3.0×) and stays a
        // small fraction (≤ 0.2×radius) of the blast, so the plume matches the
        // exploding hull instead of rivaling the whole fireball (P546 was
        // radius·0.30·(1+2.2(1−rem)) ≈ up to 0.96×radius).
        float jitter = 0.9f + EnhancedVfx.Rand01(ref seed) * 0.2f;
        float half = EnhancedSmokeSizing.ExplosionSmokeHalf(preset.RadiusExplosion, rem, jitter);

        // Near-camera fade: keep your own missiles' smoke off the screen.
        float dist = XnaVec.Distance(pos, cameraEye);
        alpha *= ParticleNearFade.Factor(dist, near.Enabled, near.FadeStart, near.FadeEnd);
        if (alpha <= 0.008f) return null;

        float angle = EnhancedVfx.Rand01(ref seed) * MathF.PI * 2f;
        uint puffSeed = EnhancedVfx.Hash(seed);
        return new EnhancedSmokeField.Puff(pos, half, rem, angle, 1f, puffSeed);
    }

    /// <summary>Draw a homogeneous-colour/soft-disc layer split across the first
    /// frame of an animated atlas (or a single texture), one batcher call per
    /// group, additive.</summary>
    private void DrawBatched(List<ParticleBillboard> items, Texture2D? tex, Matrix view, Matrix projection)
    {
        if (items.Count == 0 || tex is null) return;
        _batcher.Draw(items, tex, view, projection);
    }

    /// <summary>Draw an animated-atlas layer: one batcher call per frame texture
    /// actually referenced (the fire-ball decals cycle through the glow frames).</summary>
    private void DrawBatched(List<ParticleBillboard> items, Texture2D[] frames, Matrix view, Matrix projection)
    {
        if (items.Count == 0 || frames.Length == 0) return;
        var byFrame = new Dictionary<Texture2D, List<ParticleBillboard>>();
        // Assign billboards round-robin across frames (visual variety without
        // tracking which frame each decal picked in the gather loop above).
        int fi = 0;
        foreach (var b in items)
        {
            var tex = frames[fi % frames.Length];
            if (!byFrame.TryGetValue(tex, out var list)) byFrame[tex] = list = [];
            list.Add(b);
            fi++;
        }
        foreach (var (tex, list) in byFrame)
            _batcher.Draw(list, tex, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
