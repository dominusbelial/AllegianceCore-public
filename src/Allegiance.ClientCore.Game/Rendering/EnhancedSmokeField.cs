using System;
using System.Collections.Generic;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P546 — the reusable <b>animated CC0 smoke field</b> (adopted from the P543
/// spike's <c>Cc0SmokeField</c> into the game, renamed and reworked onto the
/// shared <see cref="ParticleBillboardBatcher"/>). The replacement for the
/// static flat smoke texture on every emitter trail and the explosion plume:
/// each puff is a randomly-rotated, random-start-frame CC0 smoke-atlas
/// billboard, alpha-blended with <b>premultiplied</b> alpha and tinted by the
/// P530 sector light (<see cref="EnhancedVfx.LitSmokeTint"/>) so it reads as a
/// grey volume, not a glow. Supports the <b>near-camera fade</b> (so your own
/// missiles' smoke never covers the screen) and the <b>density budget</b>
/// (oldest puffs are thinned first once the screen-wide cap is reached).
/// Shares the one <see cref="ParticleBillboardBatcher"/> (no second batcher),
/// grouped per frame texture.
/// </summary>
public sealed class EnhancedSmokeField : IDisposable
{
    /// <summary>Screen-wide smoke density budget (named constant): at most this
    /// many smoke puffs are drawn across ALL emitters in a frame; older puffs
    /// are thinned first once the cap is reached (see <see cref="ParticleDensityBudget"/>).</summary>
    public const int MaxSmokeOnScreen = 320;

    /// <summary>Peak smoke translucency per puff (premultiplied colour is scaled
    /// by this × the life curve), so overlapping smoke accumulates translucency
    /// instead of blowing out to white. P561 lowered this from 0.65 → 0.40 so
    /// that the re-added, more numerous smaller puffs (TrailSubPuffs× the trail
    /// smoke) keep the cumulative screen alpha inside the ≤~15% blown-out budget
    /// while still reading as a coherent plume.</summary>
    public const float PeakAlpha = 0.40f;

    /// <summary>Animation pace: how many atlas frames the smoke film advances
    /// over one unit of the puff's age (kept from P546 — slide by 14 frames/sec
    /// of life).</summary>
    public const float FrameSlidePerAge = 14f;

    /// <summary>Slow spin applied over a puff's whole life (radians), derived
    /// per-puff from its seed so a cloud reads as tumbling volume, not a frozen
    /// card. Kept modest so it never looks like fast rotation.</summary>
    public const float SpinMaxRadians = 1.4f;

    private readonly ParticleBillboardBatcher _batcher;
    private readonly Texture2D[] _frames;
    private readonly ParticleDensityBudget _budget;
    private readonly SoftParticleRenderer? _soft;

    /// <summary>One smoke puff to draw: its world position, billboard half-size,
    /// remaining fraction (1 = newborn → 0 = dying), initial billboard angle and
    /// a per-puff random seed driving the frame offset / spin (0 → hashed from
    /// the position). <paramref name="SizeJitter"/> is an optional per-puff size
    /// multiplier in [0.8, 1.2] applied on top of the caller's half-size.</summary>
    public readonly record struct Puff(XnaVec Position, float Half, float RemainingFrac,
        float Angle = 0f, float SizeJitter = 1f, uint Seed = 0);

    public EnhancedSmokeField(GraphicsDevice gd, VfxAssets vfx, SoftParticleRenderer? soft = null)
    {
        _batcher = new ParticleBillboardBatcher(gd);
        _frames = vfx.Smoke;
        _budget = new ParticleDensityBudget(MaxSmokeOnScreen);
        _soft = soft;
    }

    /// <summary>
    /// Draw every puff as a premultiplied, sector-lit, animated CC0 smoke
    /// billboard. <paramref name="eye"/> is the camera's world position,
    /// <paramref name="view"/>/<paramref name="projection"/> the frame's real
    /// camera matrices (supplying the billboard basis). <paramref name="near"/>
    /// enables the near-camera fade; the density budget is enforced per frame.
    /// <paramref name="soft"/> (P547) enables the proper soft-particle path —
    /// each puff additionally fades where it meets the opaque scene's linear
    /// depth — when the shader/target are available; when null/unusable the
    /// near-fade-only path runs (never render nothing).
    /// </summary>
    public void Draw(
        IReadOnlyCollection<Puff> puffs,
        XnaVec eye,
        Matrix view,
        Matrix projection,
        SectorLighting? sectorLight,
        ParticleNearFade near,
        ParticleFarFade far,
        ChaseOccluder occ,
        SoftParticleContext? soft = null)
    {
        if (puffs.Count == 0) return;

        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);
        var byTex = new Dictionary<Texture2D, List<ParticleBillboard>>();

        _budget.Reset();
        foreach (var p in puffs)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float local = 1f - rem;               // age 0..1
            if (local <= 0f || local >= 1f) continue;

            float dist = XnaVec.Distance(p.Position, eye);

            // Per-puff deterministic seed (frame offset + spin).
            uint s = p.Seed != 0 ? p.Seed : EnhancedVfx.Hash(FloatBits(p.Position));

            float half = p.Half * MathHelper.Clamp(p.SizeJitter, 0.8f, 1.2f);
            half *= ParticleFarFade.SizeFactor(dist, far.Enabled, far.FadeStart, far.FadeEnd, far.FarSizeScale);

            float alpha = MathHelper.Clamp(rem * rem, 0f, 1f) * PeakAlpha;
            alpha *= ParticleNearFade.Factor(dist, near.Enabled, near.FadeStart, near.FadeEnd);
            alpha *= ParticleFarFade.AlphaFactor(dist, far.Enabled, far.FadeStart, far.FadeEnd, far.FarAlpha);

            // Chase-view own-ship occluder: fade puffs between camera and ship.
            var centre = p.Position - eye;
            alpha *= ChaseOccluder.AlphaFactor(occ, centre, projection);

            if (alpha <= 0.004f) continue;

            // Density budget: admit by age (younger = more visible); oldest are
            // thinned first when the screen-wide cap is reached.
            if (!_budget.TryAdmit(local)) continue;

            var col = EnhancedVfx.LitSmokeTint(sectorLight);
            // Random start frame offset (per-puff), advancing as the puff ages.
            int frame = (int)((s % (uint)_frames.Length) + local * FrameSlidePerAge) % _frames.Length;
            // Initial rotation + a slow spin over life (derived from the seed).
            float spin = ((s >> 8) % 617u) / 613f * 2f - 1f;   // [-1, 1]
            float angle = p.Angle + spin * SpinMaxRadians * (1f - rem);

            var tex = _frames.Length > 0 ? _frames[frame] : null;
            if (tex is null) continue;
            // Premultiplied colour: rgb already scaled by alpha.
            var premul = new Color(col.X * alpha, col.Y * alpha, col.Z * alpha, alpha);
            if (!byTex.TryGetValue(tex, out var list)) byTex[tex] = list = [];
            list.Add(new ParticleBillboard(centre, half, premul, angle));
        }

        if (soft is { } sc && _soft is { } sr && sr.IsUsable)
        {
            foreach (var (tex, list) in byTex)
                _soft!.TryDraw(list, tex, view, projection, sc);
        }
        else
        {
            foreach (var (tex, list) in byTex)
                _batcher.Draw(list, tex, view, projection, _batcher.Premultiplied);
        }
    }

    private static uint FloatBits(XnaVec v)
        => EnhancedVfx.Hash((uint)BitConverter.SingleToInt32Bits(v.X) ^
                            (uint)(BitConverter.SingleToInt32Bits(v.Y) * 0x9E3779B1) ^
                            (uint)(BitConverter.SingleToInt32Bits(v.Z) * 0x85EBCA77));

    public void Dispose() => _batcher.Dispose();
}
