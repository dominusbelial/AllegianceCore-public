using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws the warp-transit teleport spark spray (P296) the native way:
/// the real <b>e_telebmp</b> spark artwork tinted by native's hot-blue
/// <c>SparkBehavior</c> colour curve (<see cref="NativeParticleCurves.SparkColor"/>),
/// drawn with native's additive particle-decal blend. Replaces the procedural
/// white soft sprite.
/// </summary>
/// <remarks>
/// <para><b>Native.</b> The teleport/warp transit emits via
/// <c>m_pParticleGeo-&gt;AddSpark()</c> — <c>SparkBehavior::Render</c>
/// (<c>effect/particlegeo.cpp:209-250</c>), artwork <c>AWF_EFFECT_SPARK =
/// "e_telebmp"</c> (particlegeo.cpp:14). Colour <c>(1.5k, 2.0k, c, k)</c> with
/// <c>c = countdown</c>, <c>k = ½c²</c> — hot blue fading out, additive blend,
/// rotating by <c>angle + c·π</c>. The spark is drawn at the native world size
/// (<see cref="WarpSparkView.Size"/>), no inflation.</para>
/// </remarks>
public sealed class WarpTransitParticleRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;
    // P781 — reused billboard list (clear + reuse), never re-allocated per
    // frame: Draw used to `new` one every call, a steady GC churn in any scene
    // with live sparks.
    private readonly List<ParticleBillboard> _items = new();

    public WarpTransitParticleRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>Draw every live spark as a textured, rotated billboard with the
    /// native SparkBehaviour hot-blue colour (see
    /// <see cref="MissileTrailRenderer.Draw"/> for the camera-relative / view
    /// conventions).</summary>
    public void Draw(IReadOnlyList<WarpSparkView> sparks, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (sparks.Count == 0) return;

        var items = _items;
        items.Clear();
        if (items.Capacity < sparks.Count) items.Capacity = sparks.Count;
        for (int i = 0; i < sparks.Count; i++)   // indexed: no boxed IReadOnlyList enumerator per frame
        {
            var p = sparks[i];
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            var centre = new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            var sc = NativeParticleCurves.SparkColor(rem);
            // Native rotates the spark decal by angle + c·π (particlegeo.cpp:241) —
            // a continuous spin as it fades.
            float angle = ParticleBillboardBatcher.RotationSeed(centre) + rem * MathHelper.Pi;
            items.Add(new ParticleBillboard(centre, p.Size * 0.5f, new Color(sc.R, sc.G, sc.B, sc.A), angle));
        }
        _batcher.Draw(items, _native.TextureFor(NativeEffectTextures.TeleStem) ?? _native.SoftFallback, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
