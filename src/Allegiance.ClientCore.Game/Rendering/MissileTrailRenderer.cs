using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws the missile exhaust trail the native way: real <b>smoke</b>
/// and <b>fire</b> artwork textures (<c>e_smokebmp</c> / <c>e_firebmp</c>,
/// loaded through <see cref="NativeEffectTextures"/>) tinted by native's exact
/// colour/alpha/size curves (<see cref="NativeParticleCurves"/>) and drawn
/// with native's additive particle-decal blend. Replaces the old procedurally
/// generated <b>white</b> soft blob, whose bright stacked quads summed to a
/// solid white patch wherever fire concentrated.
/// </summary>
/// <remarks>
/// <para><b>Native (particlegeo.cpp).</b> Missiles reuse the afterburner
/// exhaust: <c>AddFire</c> (<c>FireBehavior::Render</c>,
/// particlegeo.cpp:68-129) and <c>AddSmoke</c> (<c>SmokeBehavior::Render</c>,
/// particlegeo.cpp:24-66). Smoke is tinted <c>(0.25c, 0.2c, 0.25c, 0.25c)</c>
/// (max alpha 0.25) and grows 0.75→3.0×; fire ramps up white then yellow→red
/// while shrinking. Both are drawn with the default <c>DrawDecal</c> blend
/// <c>BlendModeAdd</c> (engine/context.h:929) — additive — with a per-particle
/// rotation angle. The real grey smoke texture + dim tint means stacked puffs
/// stay grey, never blowing out to white.</para>
/// <para><b>Sizes.</b> The pool (<see cref="MissileTrailParticles"/>) emits
/// native <c>m_fSize</c> (≈0.45 u for the stand-in missile radius), and native
/// Fire/Smoke behaviours scale from that base — so this renderer draws the
/// native base size directly, dropping the old artificial
/// <c>BillboardScale = 90</c> inflation that turned a few puffs into
/// screen-filling white discs (the reported white-patch bug).</para>
/// </remarks>
public sealed class MissileTrailRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    public MissileTrailRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>
    /// Draw every live particle as a textured, rotated billboard with native
    /// colour/size curves. <paramref name="cameraEye"/> is the camera's true
    /// world position (subtracted so vertex positions stay small, the P207
    /// lesson); <paramref name="view"/>/<paramref name="projection"/> are the
    /// frame's real camera matrices (supplying the billboard basis).
    /// </summary>
    public void Draw(IReadOnlyList<TrailParticleView> particles, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (particles.Count == 0) return;

        var fire = new List<ParticleBillboard>();
        var smoke = new List<ParticleBillboard>();

        foreach (var p in particles)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            var centre = new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            float angle = ParticleBillboardBatcher.RotationSeed(centre);
            if (p.Kind == TrailParticleKind.Fire)
            {
                var f = NativeParticleCurves.ComputeFire(1f - rem);   // lifeFrac
                fire.Add(new ParticleBillboard(centre, p.Size * 0.5f * f.SizeScale,
                    new Color(f.R, f.G, f.B, f.A), angle));
            }
            else
            {
                var sc = NativeParticleCurves.SmokeColor(rem);
                smoke.Add(new ParticleBillboard(centre, p.Size * 0.5f * NativeParticleCurves.SmokeSizeScale(rem),
                    new Color(sc.R, sc.G, sc.B, sc.A), angle));
            }
        }

        if (fire.Count > 0)
            _batcher.Draw(fire, _native.TextureFor(NativeEffectTextures.FireStem) ?? _native.SoftFallback, view, projection);
        if (smoke.Count > 0)
            _batcher.Draw(smoke, _native.TextureFor(NativeEffectTextures.SmokeStem) ?? _native.SoftFallback, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
