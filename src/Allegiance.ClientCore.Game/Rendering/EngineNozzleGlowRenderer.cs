using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One ship's engine-nozzle glow decal for a frame: its aft-centre world position
/// (already offset to the nozzle), the quad's half-extent in world units, and the
/// linear RGB tint from <see cref="EngineNozzleGlow.Compute"/>.</summary>
public readonly record struct EngineGlowView(SimVec Position, float HalfExtent, Vector3 Rgb);

/// <summary>
/// P545 — draws the engine-nozzle glow (P365) the native way: the real
/// <b>f101bmp</b> afterburner-glow decal artwork
/// (<c>AWF_EFFECT_AFTERBURNER_GLOW = "f101bmp"</c>, efart.h:12) tinted by the
/// native HSB thrust math (<see cref="EngineNozzleGlow"/>), replacing the
/// procedural white soft sprite.
/// </summary>
/// <remarks>
/// <para><b>Native (ThingGeoImpl::RenderGlow, effect/thinggeo.cpp:1419-1443).</b>
/// For every <c>"thrust"</c>-named hardpoint frame while <c>m_scaleGlow &gt; 0</c>,
/// native draws <c>m_pimageGlow</c> (f101bmp) as a <c>DrawDecal</c> tinted by
/// <c>m_colorGlow</c> — additive (DrawDecal default <c>BlendModeAdd</c>). This
/// renderer reproduces it with the real f101 texture, per-vertex HSB tint and
/// additive blend, at the same port-normalized nozzle position/size
/// (<see cref="EngineNozzleGlowRenderer"/> documentation).</para>
/// </remarks>
public sealed class EngineNozzleGlowRenderer : IDisposable
{
    /// <summary>How far behind the ship's centre the nozzle glow sits, as a
    /// fraction of its radius — consumed by the game's glow-anchor builder
    /// (Game1.Rendering.cs).</summary>
    public const float AftCenterFraction = 0.95f;

    /// <summary>The port's normalization of native's decal scale to the port's
    /// real-hull world units: the glow's half-extent is
    /// <c>scaleGlow·radius·GlowSizeFactor</c> — consumed by the game's
    /// glow-anchor builder (Game1.Rendering.cs).</summary>
    public const float GlowSizeFactor = 0.45f;

    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    public EngineNozzleGlowRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>
    /// Draw every glow as a camera-facing textured quad tinted by its HSB colour
    /// (solid alpha — additive blend gives the soft edge; the f101 artwork
    /// supplies the glow's shape). See <see cref="MissileTrailRenderer.Draw"/>
    /// for the camera-relative / view conventions.
    /// </summary>
    public void Draw(IReadOnlyList<EngineGlowView> glows, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (glows.Count == 0) return;

        var items = new List<ParticleBillboard>(glows.Count);
        foreach (var g in glows)
        {
            var centre = new XnaVec(g.Position.X, g.Position.Y, g.Position.Z) - cameraEye;
            items.Add(new ParticleBillboard(centre, g.HalfExtent, new Color(g.Rgb.X, g.Rgb.Y, g.Rgb.Z, 1f), 0f));
        }
        _batcher.Draw(items, _native.TextureFor(NativeEffectTextures.GlowStrobeStem) ?? _native.SoftFallback, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
