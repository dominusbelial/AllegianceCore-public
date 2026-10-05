using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws the shield-hit flare (P463b) the native way: the real
/// animated <b>fx09bmp</b> lens-flare texture placed <b>tangent on the struck
/// ship's shield surface</b> at the hit, over the native 0.75&#8209;s life.
/// Replaces the old procedural white soft sprite.
/// </summary>
/// <remarks>
/// <para><b>Native (WinTrek/trekigc.cpp:1624-1645).</b> A shield-absorbed hit
/// calls <c>AddFlare</c>, which wraps the <c>m_pimageFlare</c>
/// (<c>AWF_SHIELD_FLARE_TEXTURE = "fx09bmp"</c>, artwork.h:209) in a
/// <c>TextureGeo</c> + <c>AnimatedImage</c>, orients it along the hit direction
/// onto the shield ellipsoid, wraps it in a <c>TimedGeo</c> over
/// <b>0.75&#8209;s</b> (thinggeo.cpp:1290-1301) and blends it
/// <c>BlendModeAdd</c> (:1297). Admission is capped at
/// <c>GetFlareCount() &lt; 4</c> (trekigc.cpp:1626) — reproduced by the pool
/// (<see cref="ShieldFlareParticles"/>).</para>
/// <para><b>Port shape.</b> The pool approximates the shield as a sphere of the
/// ship's render scale and sits <see cref="ShieldFlareView.Center"/> on that
/// sphere toward the hit, with <see cref="ShieldFlareView.Direction"/> the
/// outward hit normal. This renderer lays each flare quad <b>flat in the
/// tangent plane</b> of that surface (perpendicular to <c>Direction</c>, rolled
/// by a per-flare seed) — a patch on the shield bubble, the native
/// <c>TextureGeo</c> read — and animates through <c>fx09bmp</c>'s 9 frames over
/// the 0.75&#8209;s life. The texture carries the ice-blue-white colour; the
/// tint is white, fading by the remaining-life fraction (the <c>TimedGeo</c>
/// fade-off).</para>
/// </remarks>
public sealed class ShieldFlareRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    public ShieldFlareRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>
    /// Draw every live flare as an animated, shield-tangent patch with white
    /// tint fading over the native life (see
    /// <see cref="MissileTrailRenderer.Draw"/> for the camera-relative / view
    /// conventions).
    /// </summary>
    public void Draw(IReadOnlyList<ShieldFlareView> flares, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (flares.Count == 0) return;

        var frames = _native.FramesFor(NativeEffectTextures.ShieldFlareStem);
        if (frames is not { Length: > 0 })
            frames = new[] { _native.SoftFallback };

        // Group by animation frame so each frame's patches draw in one batch
        // (native AnimatedImage advances the lens over the 0.75-s life).
        var byFrame = new Dictionary<int, List<ParticlePatch>>();
        foreach (var f in flares)
        {
            float rem = MathHelper.Clamp(f.RemainingFrac, 0f, 1f);
            var centre = new XnaVec(f.Center.X, f.Center.Y, f.Center.Z) - cameraEye;
            var normal = new XnaVec(f.Direction.X, f.Direction.Y, f.Direction.Z);
            int frame = Math.Min(frames.Length - 1, (int)((1f - rem) * frames.Length));
            // White tint fading by remaining life (TimedGeo fade-off); the
            // texture carries the ice-blue-white flare colour.
            var col = new Color(1f, 1f, 1f, rem);
            if (!byFrame.TryGetValue(frame, out var list))
                byFrame[frame] = list = new List<ParticlePatch>();
            list.Add(new ParticlePatch(centre, normal, f.Size, col, ParticleBillboardBatcher.RotationSeed(centre)));
        }
        foreach (var (frame, patches) in byFrame)
            _batcher.DrawPatches(patches, frames[frame], view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
