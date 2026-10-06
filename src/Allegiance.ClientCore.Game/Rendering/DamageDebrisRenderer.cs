using System.Collections.Generic;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws the continuous damage-debris smoke stream (P297) the native
/// way: the real <b>e_smokebmp</b> smoke artwork tinted by native's exact
/// <c>SmokeBehavior</c> colour and growth (<see cref="NativeParticleCurves"/>),
/// drawn with native's additive particle-decal blend. Replaces the old
/// procedural white soft sprite — so stacked vent-smoke from low-hull ships
/// stays dim grey instead of summing to a white patch.
/// </summary>
/// <remarks>
/// <para><b>Native.</b> <c>AddDamageParticles</c> (effect/thinggeo.cpp:699-760)
/// emits via <c>m_pParticleGeo-&gt;AddSmoke()</c>, so the damage vent shares the
/// missile/ship <c>SmokeBehavior</c> (<c>e_smokebmp</c>, particlegeo.cpp:24-66):
/// RGBA <c>(0.25c, 0.2c, 0.25c, 0.25c)</c> — dim grey, max alpha 0.25 — growing
/// 0.75→3.0× as the puff ages, additive blend (DrawDecal default
/// <c>BlendModeAdd</c>). This renderer reproduces the exact colour/size curves
/// from <paramref name="DamageDebrisView.RemainingFrac"/>.</para>
/// <para><b>P546 Enhanced smoke.</b> Constructed with an
/// <see cref="EnhancedSmokeField"/> (Enhanced mode), the vent smoke is drawn
/// through that field (animated CC0 smoke, premultiplied, sector-lit P530,
/// near-faded). With <c>null</c> (Native mode) it is byte-for-byte the P545 path.</para>
/// </remarks>
public sealed class DamageDebrisRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;
    private readonly EnhancedSmokeField? _enhancedSmoke;

    public DamageDebrisRenderer(GraphicsDevice gd, NativeEffectTextures native,
        EnhancedSmokeField? enhancedSmoke = null)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
        _enhancedSmoke = enhancedSmoke;
    }

    /// <summary>Draw every live puff as a textured, rotated billboard with the
    /// native SmokeBehaviour colour/growth curves (see
    /// <see cref="MissileTrailRenderer.Draw"/> for the camera-relative / view
    /// conventions). <paramref name="sectorLight"/>/<paramref name="near"/> are
    /// used only in Enhanced mode.</summary>
    public void Draw(IReadOnlyList<DamageDebrisView> puffs, Vector3 cameraEye, Matrix view, Matrix projection,
        SectorLighting? sectorLight = null, ParticleNearFade near = default)
    {
        if (puffs.Count == 0) return;

        if (_enhancedSmoke is { } enh)
        {
            var smokePuffs = new List<EnhancedSmokeField.Puff>(puffs.Count);
            foreach (var p in puffs)
            {
                float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
                if (rem <= 0f || rem >= 1f) continue;
                // Enhanced growth mirrors native SmokeBehaviour 0.75→3.0× scaled
                // to the puff's emitted size.
                float half = p.Size * 2.4f * (0.5f + 2.2f * (1f - rem));
                smokePuffs.Add(new EnhancedSmokeField.Puff(
                    new XnaVec(p.Position.X, p.Position.Y, p.Position.Z), half, rem));
            }
            if (smokePuffs.Count > 0)
                enh.Draw(smokePuffs, cameraEye, view, projection, sectorLight, near);
            return;
        }

        var items = new List<ParticleBillboard>(puffs.Count);
        foreach (var p in puffs)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            var centre = new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            var sc = NativeParticleCurves.SmokeColor(rem);
            items.Add(new ParticleBillboard(centre,
                p.Size * 0.5f * NativeParticleCurves.SmokeSizeScale(rem),
                new Color(sc.R, sc.G, sc.B, sc.A),
                ParticleBillboardBatcher.RotationSeed(centre)));
        }
        _batcher.Draw(items, _native.TextureFor(NativeEffectTextures.SmokeStem) ?? _native.SoftFallback, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
