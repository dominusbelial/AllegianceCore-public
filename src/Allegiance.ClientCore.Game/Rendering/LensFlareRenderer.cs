using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P589 — the <b>native sun + lens flare</b>, drawn at the per-cluster light
/// direction. Faithful port of <c>LensFlareImage</c>
/// (<c>effect/efimage.cpp:573-760</c>) driven by the sector's
/// <c>LightDirection</c> (<c>WinTrek/WinTrek.cpp:3614-3617, 3639</c> →
/// <c>SetLightDirection(pcluster->GetLightDirection())</c>).
///
/// Native behaviour reproduced:
/// <list type="bullet">
/// <item>The eight <c>g_lensFlareData</c> elements
/// (<c>efimage.cpp:601-613</c>) are laid out along the line through the screen
/// center: each is centred at <c>screenCenter + pointLight·m_pos</c> where
/// <c>pointLight</c> is the projected offset of the light direction, scaled by
/// <c>m_scale·(viewportWidth/800)</c>, drawn additively
/// (<c>BlendModeAdd</c>), flat-shaded (<c>ShadeModeFlat</c>),
/// <c>efimage.cpp:724-726</c>.</item>
/// <item>The <c>STAR_AURA</c> element (surface 1, <c>f105bmp</c>) sits at
/// <c>pos +1.00</c> — exactly the projected light position — and is the visible
/// <b>sun aura</b> (each sector's light source). It is the only element native
/// rotates, by <c>(screenX+screenY)/200</c> rad (<c>efimage.cpp:740-743</c>).</item>
/// <item>Blue elements tint <c>Color::Blue()</c>, white elements
/// <c>Color::White()</c>, multiplied additively into the frame.</item>
/// </list>
///
/// The flare only appears when the light direction projects into the viewport
/// (<c>efimage.cpp:716-721</c>); occlusion against ships is not performed by
/// native (it is a 2-D composite drawn with the backdrop) — the ships, drawn
/// afterwards by the opaque scene, naturally read over the flare.
///
/// Render states are pinned by the caller (the black-sky draw); this renderer
/// only sets the additive blend inside its own orthographic pass and restores
/// it, leaving depth/raster/sampler as it found them.
/// </summary>
public sealed class LensFlareRenderer : IDisposable
{
    /// <summary>Nominal reference width native scales the flare against
    /// (<c>rect.XSize()/800.0f</c>, efimage.cpp:713).</summary>
    private const float ReferenceWidth = 800f;

    /// <summary>Base half-size in pixels (at the reference width) for an
    /// element with scale 1.0 — chosen so the sun aura (scale 1.5) reads as a
    /// prominent but not overwhelming disc and the smallest ghosts (0.25) stay
    /// subtle. Matches native's intent that elements are a modest fraction of
    /// the frame.</summary>
    private const float BaseHalfSizePx = 34f;

    private readonly GraphicsDevice _gd;
    private readonly MdlTexture2D _mdl;
    private readonly Texture2D?[] _surfaces = new Texture2D?[LensFlareElements.SurfaceArt.Length];

    private Vector3? _lightDir;
    private bool _loaded;

    public LensFlareRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _mdl = new MdlTexture2D(gd, assets, keyBelow: 10);
    }

    /// <summary>Whether a light direction is available to draw a flare.</summary>
    public bool HasLight => _lightDir is not null;

    /// <summary>Set the per-sector light direction (toward the sun), from the
    /// cluster's <c>LightDirection</c>.</summary>
    public void SetLightDirection(Vector3? towardLight) => _lightDir = towardLight;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        for (int i = 0; i < LensFlareElements.SurfaceArt.Length; i++)
            _surfaces[i] = _mdl.Get(LensFlareElements.SurfaceArt[i]);
        _loaded = true;
    }

    /// <summary>
    /// Draw the flare additively in screen space, centred on the projected
    /// light direction. <paramref name="view"/>/<paramref name="projection"/>
    /// project the light direction to screen pixels; the quads are then drawn
    /// with a self-contained orthographic pass. Sets/restores only the blend
    /// state (the caller has already pinned depth-off / raster).
    /// </summary>
    public void Draw(Matrix view, Matrix projection)
    {
        if (_lightDir is not Vector3 light) return;
        EnsureLoaded();
        if (!HasAnySurface()) return;

        var gd = _gd;
        int w = gd.Viewport.Width, h = gd.Viewport.Height;
        if (w <= 0 || h <= 0) return;

        // Project the light direction (a point far along it) to screen space.
        var p = Vector4.Transform(new Vector4(light * 100000f, 1f), view * projection);
        if (p.W <= 0f) return; // behind the camera
        float sx = (p.X / p.W * 0.5f + 0.5f) * w;
        float sy = (0.5f - p.Y / p.W * 0.5f) * h;
        if (sx < 0 || sx > w || sy < 0 || sy > h) return; // outside the viewport

        float cx = w / 2f, cy = h / 2f;
        var pointLight = new Vector2(sx - cx, sy - cy);
        float scale = w / ReferenceWidth;

        // Pin the states this 2-D backdrop element needs (mirroring the sky
        // convention: the flare must never depth-test against the opaque scene
        // and must not be clipped by an inherited rasterizer). Capture + restore
        // so the caller's state is untouched.
        var prevDepth = gd.DepthStencilState;
        var prevRaster = gd.RasterizerState;
        var prevBlend = gd.BlendState;
        var prevSampler = gd.SamplerStates[0];
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.BlendState = BlendState.Additive;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            LightingEnabled = false,
            View = Matrix.Identity,
            Projection = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, 1),
            World = Matrix.Identity,
        };

        foreach (var elem in LensFlareElements.Table)
        {
            if (elem.Index < 0 || elem.Index >= _surfaces.Length) continue;
            var tex = _surfaces[elem.Index];
            if (tex is null) continue;

            var center = new Vector2(cx, cy) + pointLight * elem.Pos;
            float half = BaseHalfSizePx * (w / ReferenceWidth) * elem.Scale;

            var color = elem.White ? new Color(255, 255, 255) : new Color(40, 40, 255);

            // Native rotates only the STAR_AURA (surface index 1).
            float angle = elem.Index == 1 ? (sx + sy) / 200f : 0f;
            DrawQuad(effect, gd, center, half, tex, color, angle);
        }

        gd.DepthStencilState = prevDepth;
        gd.RasterizerState = prevRaster;
        gd.BlendState = prevBlend;
        gd.SamplerStates[0] = prevSampler;
    }

    private void DrawQuad(BasicEffect effect, GraphicsDevice gd, Vector2 center, float half, Texture2D tex, Color color, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        static Vector2 Rot(Vector2 d, float c, float s) => new(d.X * c - d.Y * s, d.X * s + d.Y * c);

        var tl = Rot(new Vector2(-half, -half), c, s);
        var tr = Rot(new Vector2(+half, -half), c, s);
        var bl = Rot(new Vector2(-half, +half), c, s);
        var br = Rot(new Vector2(+half, +half), c, s);

        var verts = new[]
        {
            new VertexPositionColorTexture(new Vector3(center + tl, 0), color, new Vector2(0, 0)),
            new VertexPositionColorTexture(new Vector3(center + tr, 0), color, new Vector2(1, 0)),
            new VertexPositionColorTexture(new Vector3(center + bl, 0), color, new Vector2(0, 1)),
            new VertexPositionColorTexture(new Vector3(center + br, 0), color, new Vector2(1, 1)),
        };
        var indices = new short[] { 0, 1, 2, 2, 1, 3 };

        effect.Texture = tex;
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2);
        }
    }

    private bool HasAnySurface()
    {
        for (int i = 0; i < _surfaces.Length; i++)
            if (_surfaces[i] is not null) return true;
        return false;
    }

    public void Dispose()
    {
        _mdl.Dispose();
        for (int i = 0; i < _surfaces.Length; i++) _surfaces[i] = null;
    }
}
