using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws the ship engine-thrust trail the native way: real <b>smoke</b>
/// and <b>fire</b> artwork (<c>e_smokebmp</c> / <c>e_firebmp</c>) tinted by
/// native's exact colour/alpha/size curves (<see cref="NativeParticleCurves"/>)
/// with native's additive particle-decal blend — replacing the procedural
/// white soft blob. Native FireBehavior (particlegeo.cpp:68-129) gives the
/// bright orange/white nozzle flame; SmokeBehavior (particlegeo.cpp:24-66)
/// gives the dim grey expanding plume. See
/// <see cref="ShipEngineTrailParticles"/> for the emitter's native size/velocity
/// math; this renderer draws each particle at that native base size times the
/// native Fire/Smoke size curves.
/// </summary>
public sealed class ShipEngineTrailRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    public ShipEngineTrailRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>Draw every live particle as a textured, rotated billboard with
    /// native colour/size curves (see <see cref="MissileTrailRenderer.Draw"/>
    /// for the camera-relative / view conventions).</summary>
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
                var f = NativeParticleCurves.ComputeFire(1f - rem);
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
