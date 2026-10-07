using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P291 — draws every live deployed mine as a native-<c>MineFieldGeo</c> decal
/// cloud (<c>src/effect/efgeo.cpp:15-87</c>): a mine is <b>not a mesh</b> (no
/// corpus mine type carries a <c>ModelName</c> — see <see cref="MineModelSource"/>);
/// native renders it as <c>1 + int(strength·radius²/100)</c> camera-facing
/// textured decals of the real <c>fxmine</c> sprite, scattered uniformly within
/// a sphere of the mine's radius, each dimmed by
/// <c>bright = bound(100/dist, 0, 1)</c> and source-alpha blended
/// (<c>MineFieldGeoImpl::Render</c>, <c>efgeo.cpp:64-87</c>). This renderer
/// reproduces that faithfully: one shared fxmine texture (parsed at runtime),
/// per-mine deterministic decal offsets (<see cref="MineFieldGeometry"/>), and
/// the native distance-brightness falloff. It mirrors <see cref="ExplosionRenderer"/>'s
/// billboard machinery (per-frame <c>DrawUserIndexedPrimitives</c>).
/// </summary>
/// <remarks>
/// <para><b>Source-alpha, not additive.</b> Native draws the mine decals with
/// <c>BlendModeSourceAlpha</c> (<c>efgeo.cpp:83</c>) — ordinary source-alpha
/// blending, unlike the additive explosions. The fxmine RGB565 sprite has no
/// alpha channel, so its black surround is keyed to transparent via the
/// established <see cref="MdlTextureDecoder.ToPremultipliedRgba"/> convention
/// (P241/P242) and drawn under <c>BlendState.AlphaBlend</c>.</para>
/// <para><b>No side tint (faithful).</b> Unlike the probe/station renderers,
/// native colours mine decals with a pure grayscale <c>Color(bright, bright,
/// bright)</c> (<c>efgeo.cpp:70</c>) — there is no team tint on a mine, so none
/// is applied here either.</para>
/// <para><b>Native sprite size (P563).</b> <see cref="DecalHalfExtent"/> is the
/// world-space half-width of each mine billboard, derived from native's decal
/// math rather than display-tuned. Native draws each mine decal with scale 1,
/// angle 0 and zero forward/right (<c>efgeo.cpp:75-84</c>), which yields
/// <c>decal.m_scale = mat.GetScale()·1</c> (<c>engine/context.cpp:2295</c>);
/// because forward is zero, <c>Decal::GetVertices</c> builds a quad spanning
/// ±<c>m_scale</c> (context.cpp:218-240), so the world half-extent is
/// <c>m_scale</c>. <c>Matrix::GetScale()</c> is the first-column length
/// (<c>zlib/matrix.cpp:80-84</c>), which for the mine field thing's rigid
/// local-to-eye transform is exactly 1.0 — hence this constant equals 1.0,
/// the native sprite size (prior to P563 it was a 10 u display tune, ~10×
/// native). Only the cloud extent (not each sprite) scales with the mine's
/// radius, matching native.</para>
/// </remarks>
public sealed class MineRenderer : IDisposable
{
    /// <summary>World-space half-width of each mine decal billboard, equal to
    /// native's <c>m_scale = mat.GetScale()·1 = 1.0</c> (P563; see class doc for
    /// the derivation through <c>Decal::GetVertices</c>).</summary>
    public const float DecalHalfExtent = 1f;

    private readonly Texture2D _texture;
    private readonly BasicEffect _effect;
    private readonly BlendState _blend;
    private readonly RasterizerState _rasterizer;

    /// <param name="gd">The graphics device.</param>
    /// <param name="texture">The decoded real fxmine sprite.</param>
    public MineRenderer(GraphicsDevice gd, MdlTexture texture)
    {
        var rgba = MdlTextureDecoder.ToPremultipliedRgba(texture);
        _texture = new Texture2D(gd, texture.Width, texture.Height);
        _texture.SetData(rgba);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };

        // Native BlendModeSourceAlpha (efgeo.cpp:83): ordinary source-alpha
        // blending. Combined with the alpha-keyed premultiplied sprite (P241
        // convention) the black surround vanishes and the glow reads on the scene.
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
    /// Draw one mine's decal cloud. <paramref name="centre"/> is the mine's real
    /// position already translated by the camera eye (world minus eye — the
    /// caller's convention), <paramref name="radius"/> its type radius,
    /// <paramref name="seed"/> the mine's id (deterministic cloud),
    /// <paramref name="cameraEye"/>/<c>view</c>/<c>projection</c> the frame's
    /// real camera data (giving the billboard basis and the distance falloff).
    /// </summary>
    public void DrawCloud(XnaVec centre, float radius, float strength, int seed, XnaVec cameraEye, Matrix view, Matrix projection)
    {
        // P574 — the decal count falls with the mine's strength (native
        // SetMineStrength → GetCount(strength, radius), efgeo.cpp:21-24,43-51),
        // and native truncates by index (keeps the first N), so we draw only the
        // first count decals.
        int count = MineFieldGeometry.OffsetCount(strength, radius);
        var offsets = new XnaVec[count];
        MineFieldGeometry.SampleUniformInSphere(radius, seed, offsets);

        // Native bright = bound(100 / vecEye.Length(), 0, 1) where vecEye is the
        // distance from the camera to the geo origin (the mine centre) — so the
        // whole cloud dims together with approach (efgeo.cpp:66-70).
        float dist = centre.Length();
        float bright = Math.Clamp(100f / dist, 0f, 1f);
        if (bright <= 0f) return;

        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);
        var colour = new Color(bright, bright, bright, 255);

        var verts = new VertexPositionColorTexture[count * 4];
        var indices = new short[count * 6];
        int v = 0, idx = 0;
        foreach (var off in offsets)
        {
            var pos = centre + off;
            float half = DecalHalfExtent;
            // Camera-facing quad corners (ExplosionRenderer pattern).
            var tl = pos - camRight * half + camUp * half;
            var tr = pos + camRight * half + camUp * half;
            var br = pos + camRight * half - camUp * half;
            var bl = pos - camRight * half - camUp * half;
            verts[v] = new VertexPositionColorTexture(tl, colour, new Vector2(0f, 0f));
            verts[v + 1] = new VertexPositionColorTexture(tr, colour, new Vector2(1f, 0f));
            verts[v + 2] = new VertexPositionColorTexture(br, colour, new Vector2(1f, 1f));
            verts[v + 3] = new VertexPositionColorTexture(bl, colour, new Vector2(0f, 1f));
            indices[idx] = (short)v; indices[idx + 1] = (short)(v + 1); indices[idx + 2] = (short)(v + 2);
            indices[idx + 3] = (short)v; indices[idx + 4] = (short)(v + 2); indices[idx + 5] = (short)(v + 3);
            v += 4; idx += 6;
        }

        _effect.Texture = _texture;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _blend;
        gd.RasterizerState = _rasterizer;
        gd.DepthStencilState = DepthStencilState.DepthRead;

        _effect.CurrentTechnique.Passes[0].Apply();
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, count * 2);

        gd.BlendState = BlendState.Opaque;         // restore (P235 precedent)
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _texture.Dispose();
        _effect.Dispose();
        _blend.Dispose();
        _rasterizer.Dispose();
    }
}
