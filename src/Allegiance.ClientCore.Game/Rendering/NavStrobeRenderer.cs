using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One strobe-light decal for a frame: its world position, the quad's
/// half-extent (already scaled by the current pulse) and its linear RGB tint.</summary>
public readonly record struct NavStrobeView(SimVec Position, float HalfExtent, Vector3 Rgb, float Alpha = 1f);

/// <summary>
/// P545 — draws the navigation strobe lights (P367) the native way: the real
/// <b>f101bmp</b> strobe decal artwork (<c>AWF_EFFECT_STROBE = "f101bmp"</c>,
/// efart.h:14) tinted per-vertex by each light's colour, replacing the
/// procedural white soft sprite. Native <c>LightsGeo::Render</c>
/// (effect/efapp.cpp:171-222) draws a <c>DrawDecal</c> of f101bmp tinted by the
/// light's colour at <c>0.5·value</c> whenever <c>value &gt; 0</c> — additive
/// (DrawDecal default <c>BlendModeAdd</c>); the pulse scales the size only.
/// </summary>
public sealed class NavStrobeRenderer : IDisposable
{
    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    public NavStrobeRenderer(GraphicsDevice gd, NativeEffectTextures native)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);
    }

    /// <summary>
    /// Draw every strobe light as a camera-facing textured quad tinted by its
    /// colour (solid alpha — additive blend + the f101 artwork give the soft
    /// edge). See <see cref="MissileTrailRenderer.Draw"/> for the camera-
    /// relative / view conventions.
    /// </summary>
    public void Draw(IReadOnlyList<NavStrobeView> lights, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (lights.Count == 0) return;

        var items = new List<ParticleBillboard>(lights.Count);
        foreach (var l in lights)
        {
            var centre = new XnaVec(l.Position.X, l.Position.Y, l.Position.Z) - cameraEye;
            items.Add(new ParticleBillboard(centre, l.HalfExtent, new Color(l.Rgb.X, l.Rgb.Y, l.Rgb.Z, l.Alpha), 0f));
        }
        _batcher.Draw(items, _native.TextureFor(NativeEffectTextures.GlowStrobeStem) ?? _native.SoftFallback, view, projection);
    }

    public void Dispose() => _batcher.Dispose();
}
