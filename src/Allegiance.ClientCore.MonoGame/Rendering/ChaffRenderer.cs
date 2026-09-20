using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P292 — draws every live deployed chaff as a single native-style camera-facing
/// decal. Native <c>CchaffIGC::Initialize</c> loads the chaff with one
/// <c>LoadDecal(m_pChaffTypeData->textureName, NULL, Color(1,1,1,1), false,
/// 1.0f, ...)</c> (<c>src/Igc/chaffigc.cpp:42-46</c>): a flat <b>billboard</b> of
/// the chaff type's texture (assets <c>fxminebmp.mdl</c> / <c>cc_cmtexbmp.mdl</c>,
/// see <see cref="ChaffModelSource"/>) — <em>not</em> a solid mesh (no chaff type
/// in the corpus has a ModelName) and <em>not</em> a decal cloud (that is the
/// mine's <c>MineFieldGeo</c>). This renderer reproduces that faithfully: one
/// shared billboard quad per chaff, texture picked per type, sized to the chaff's
/// growing radius (<see cref="ChaffAnimation.CurrentRadius"/>), source-alpha
/// blended, and spun in the screen plane at the native rate
/// (<c>chaffigc.cpp:55</c>). It mirrors <see cref="MineRenderer"/>'s
/// per-frame <c>DrawUserIndexedPrimitives</c> billboard machinery (itself the
/// <c>ExplosionRenderer</c> pattern), reduced to a single quad.
/// </summary>
/// <remarks>
/// <para><b>Source-alpha, not additive.</b> As with the mine decals, the chaff
/// sprite (RGB565, no alpha) is alpha-keyed via
/// <see cref="MdlTextureDecoder.ToPremultipliedRgba"/> and drawn under ordinary
/// source-alpha blending (<c>BlendState.AlphaBlend</c>) so its black surround
/// vanishes and the glow reads on the scene — the same P241/P242 convention.</para>
/// <para><b>No colour/tint.</b> Native passes a pure white decal colour for
/// chaff (<c>chaffigc.cpp:43</c>), so none is applied here either.</para>
/// <para><b>Display tuning.</b> <see cref="HalfExtentScale"/> sizes the
/// billboard relative to the chaff's current radius: native passes a fixed
/// decal width of 1.0 to <c>LoadDecal</c> whose exact world scale is
/// engine-internal, so this is the port's display constant making the (small,
/// radius 5–15) sprite legible — kept out of the pure animation helper.</para>
/// </remarks>
public sealed class ChaffRenderer : IDisposable
{
    /// <summary>World-space half-width of a chaff billboard as a multiple of its
    /// current radius (display tuning — see class doc). Radius 5 (PCore001's
    /// real value) → a 20-unit-wide sprite, comparable to a mine's single
    /// <c>DecalHalfExtent=10</c> mote.</summary>
    public const float HalfExtentScale = 2f;

    private readonly Dictionary<string, Texture2D> _textures;
    private readonly BasicEffect _effect;
    private readonly BlendState _blend;
    private readonly RasterizerState _rasterizer;

    /// <param name="gd">The graphics device.</param>
    /// <param name="textures">The decoded real chaff sprites keyed by stem
    /// (e.g. <c>"fxminebmp"</c>), preloaded from the artwork path.</param>
    public ChaffRenderer(GraphicsDevice gd, IReadOnlyDictionary<string, Texture2D> textures)
    {
        _textures = new Dictionary<string, Texture2D>(textures, StringComparer.OrdinalIgnoreCase);
        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        // Ordinary source-alpha (the mine decal convention) so the alpha-keyed
        // sprite's black surround vanishes and its glow reads on the scene.
        _blend = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        _rasterizer = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>
    /// Draw one chaff's single camera-facing billboard.
    /// <paramref name="centre"/> is the chaff's real current position already
    /// translated by the camera eye (world minus eye — the caller's convention),
    /// <paramref name="radius"/> its current rendered radius
    /// (<see cref="ChaffAnimation.CurrentRadius"/>), <paramref name="stem"/> the
    /// resolved texture stem, <paramref name="rollAngle"/> the screen-plane spin
    /// angle, and <paramref name="view"/>/<c>projection</c> the frame's real
    /// camera data (giving the billboard basis).
    /// </summary>
    public void Draw(XnaVec centre, float radius, string stem, float rollAngle, Matrix view, Matrix projection)
    {
        float half = radius * HalfExtentScale;
        if (half <= 0f) return;

        if (!_textures.TryGetValue(stem, out var tex))
            tex = _textures[ChaffModelSource.DefaultStem];

        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);

        // Native spin about the view axis (chaffigc.cpp:55): rotate the
        // billboard basis in the screen plane by the accumulated roll angle.
        float c = MathF.Cos(rollAngle), s = MathF.Sin(rollAngle);
        var rotRight = camRight * c + camUp * s;
        var rotUp = -camRight * s + camUp * c;

        var tl = centre - rotRight * half + rotUp * half;
        var tr = centre + rotRight * half + rotUp * half;
        var br = centre + rotRight * half - rotUp * half;
        var bl = centre - rotRight * half - rotUp * half;

        var verts = new[]
        {
            new VertexPositionColorTexture(tl, Color.White, new Vector2(0f, 0f)),
            new VertexPositionColorTexture(tr, Color.White, new Vector2(1f, 0f)),
            new VertexPositionColorTexture(br, Color.White, new Vector2(1f, 1f)),
            new VertexPositionColorTexture(bl, Color.White, new Vector2(0f, 1f)),
        };
        short[] indices = { 0, 1, 2, 0, 2, 3 };

        _effect.Texture = tex;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _blend;
        gd.RasterizerState = _rasterizer;
        gd.DepthStencilState = DepthStencilState.DepthRead;

        _effect.CurrentTechnique.Passes[0].Apply();
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2);

        gd.BlendState = BlendState.Opaque;          // restore (P235/P291 precedent)
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        foreach (var kv in _textures) kv.Value.Dispose();
        _effect.Dispose();
        _blend.Dispose();
        _rasterizer.Dispose();
    }
}
