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
    /// instead of blowing out to white.</summary>
    public const float PeakAlpha = 0.65f;

    private readonly ParticleBillboardBatcher _batcher;
    private readonly Texture2D[] _frames;
    private readonly ParticleDensityBudget _budget;

    /// <summary>One smoke puff to draw: its world position, billboard half-size
    /// and remaining fraction (1 = newborn → 0 = dying).</summary>
    public readonly record struct Puff(XnaVec Position, float Half, float RemainingFrac);

    public EnhancedSmokeField(GraphicsDevice gd, VfxAssets vfx)
    {
        _batcher = new ParticleBillboardBatcher(gd);
        _frames = vfx.Smoke;
        _budget = new ParticleDensityBudget(MaxSmokeOnScreen);
    }

    /// <summary>
    /// Draw every puff as a premultiplied, sector-lit, animated CC0 smoke
    /// billboard. <paramref name="eye"/> is the camera's world position,
    /// <paramref name="view"/>/<paramref name="projection"/> the frame's real
    /// camera matrices (supplying the billboard basis). <paramref name="near"/>
    /// enables the near-camera fade; the density budget is enforced per frame.
    /// </summary>
    public void Draw(
        IReadOnlyCollection<Puff> puffs,
        XnaVec eye,
        Matrix view,
        Matrix projection,
        SectorLighting? sectorLight,
        ParticleNearFade near)
    {
        if (puffs.Count == 0) return;

        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);
        var byTex = new Dictionary<Texture2D, List<ParticleBillboard>>();

        _budget.Reset();
        uint rng = 0xC0FFEEu;
        foreach (var p in puffs)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float local = 1f - rem;               // age 0..1
            if (local <= 0f || local >= 1f) continue;

            float alpha = MathHelper.Clamp(rem * rem, 0f, 1f) * PeakAlpha;
            float dist = XnaVec.Distance(p.Position, eye);
            alpha *= ParticleNearFade.Factor(dist, near.Enabled, near.FadeStart, near.FadeEnd);
            if (alpha <= 0.012f) continue;

            // Density budget: admit by age (younger = more visible); oldest are
            // thinned first when the screen-wide cap is reached.
            if (!_budget.TryAdmit(local)) continue;

            var col = EnhancedVfx.LitSmokeTint(sectorLight);
            rng = rng * 747796405u + 2891336453u;
            int frame = (int)((rng % (uint)_frames.Length) + local * 14f) % _frames.Length;
            float angle = (rng >> 16) % 628u / 100f;

            var tex = _frames.Length > 0 ? _frames[frame] : null;
            if (tex is null) continue;
            var centre = p.Position - eye;
            // Premultiplied colour: rgb already scaled by alpha.
            var premul = new Color(col.X * alpha, col.Y * alpha, col.Z * alpha, alpha);
            if (!byTex.TryGetValue(tex, out var list)) byTex[tex] = list = [];
            list.Add(new ParticleBillboard(centre, p.Half, premul, angle));
        }

        foreach (var (tex, list) in byTex)
            _batcher.Draw(list, tex, view, projection, _batcher.Premultiplied);
    }

    public void Dispose() => _batcher.Dispose();
}
