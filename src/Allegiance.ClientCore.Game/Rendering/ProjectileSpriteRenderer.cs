using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P544 — draws a projectile round the native way: a real textured, spinning,
/// camera-facing sprite (billboard), or a directional decal stretched along the
/// travel direction — from the type's real <c>DataProjectileTypeIGC</c> visual
/// (<see cref="Allegiance.Core.Visual.ProjectileVisual.Resolve"/>). The artwork
/// is decoded from the original RGB565 <c>.mdl</c> texture strips at runtime from
/// <c>ALLEGIANCE_ARTWORK_PATH</c>, cached per stem — exactly the
/// <see cref="ExplosionAssets"/>/<see cref="GaugeTextureSource"/> runtime-asset
/// convention (real files read straight from the artwork tree, never compiled
/// into Content).
/// </summary>
/// <remarks>
/// <para><b>The native decal.</b> <c>CprojectileIGC::Initialize</c>
/// (<c>projectileIGC.cpp:59-66</c>) builds the visible bolt from
/// <c>&lt;textureName&gt;bmp</c> (an animated image, our 128×128 sprites decode to
/// a single frame) as an additive decal with the thing-site radius =
/// <c>radius</c> and a spin <c>rotation</c> rad/s (<c>:76-80</c>). Non-directional
/// decals are camera-facing billboards; directional ones (<c>bDirectional</c>)
/// stretch along the travel direction with <c>width</c>. Additive blending
/// (SrcAlpha, One) makes each sprite's black surround vanish and the glowing
/// pixels add — the reason native renders these as additive decals.</para>
/// <para><b>Sprite scale.</b> The port's established decal convention (the chaff
/// <see cref="ChaffRenderer.HalfExtentScale"/>/mine decal precedent) draws a
/// billboard's half-width as <c>radius × 2</c>, so the full sprite spans
/// <c>4 × radius</c>. Native's exact decal geo isn't shipped in this checkout, so
/// this is the same documented display convention the chaff/mine decals use —
/// real <c>radius</c> per type, so a Gat Gun (r 1) is a small 4-unit bolt while
/// an A-B Omega "power" round (r 50) is a huge 200-unit blast. See the P544
/// report.</para>
/// <para><b>State hygiene.</b> Additive blend + Cull-None rasterizer are set for
/// each draw and restored to the opaque/default states afterwards (the
/// <see cref="ExplosionRenderer"/> P339/P525/P529 restore precedent).</para>
/// </remarks>
public sealed class ProjectileSpriteRenderer : IDisposable
{
    /// <summary>World-space half-width of a billboard as a multiple of its type's
    /// <c>radius</c> (shared decal convention — see class doc).</summary>
    public const float HalfExtentScale = 2f;

    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly BasicEffect _effect;
    private readonly BlendState _additive;
    private readonly RasterizerState _cullNone;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);

    public ProjectileSpriteRenderer(GraphicsDevice gd, Assets.IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        // Additive (SrcAlpha, One): the sprite's black surround contributes 0 and
        // only the glowing pixels add — native's additive-decal look.
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>
    /// The decoded real sprite for a texture stem (e.g. <c>"f99"</c> →
    /// <c>f99bmp.mdl</c>), cached per stem. <c>null</c> when the file is missing or
    /// undecodable — logged once, so a caller can degrade to the box fallback
    /// (never an invisible round).
    /// </summary>
    public Texture2D? TextureFor(string stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        if (_textures.TryGetValue(stem, out var t)) return t;
        string rel = stem + "bmp.mdl";
        try
        {
            if (!_assets.Exists(rel))
            {
                LogMissing(stem, "not present");
                return null;
            }
            var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, rel));
            var tx = Upload(tex);
            _textures[stem] = tx;
            return tx;
        }
        catch (Exception ex)
        {
            LogMissing(stem, ex.Message);
            return null;
        }
    }

    private void LogMissing(string stem, string why)
    {
        if (_missingLogged.Add(stem))
            Console.WriteLine($"[monogame] projectile texture '{stem}bmp.mdl' unavailable ({why}) — box fallback");
    }

    private Texture2D Upload(MdlTexture tex)
    {
        // Opaque RGBA upload (RGB565 has no alpha); additive rendering makes the
        // black surround vanish (same as ExplosionAssets).
        var surface = new Texture2D(_gd, tex.Width, tex.Height);
        var colors = new Color[tex.Width * tex.Height];
        int n = 0;
        for (int y = 0; y < tex.Height; y++)
            for (int x = 0; x < tex.Width; x++)
            {
                int si = (y * tex.Width + x) * 4;
                colors[n++] = new Color((int)tex.Rgba[si], (int)tex.Rgba[si + 1], (int)tex.Rgba[si + 2], 255);
            }
        surface.SetData(colors);
        return surface;
    }

    /// <summary>
    /// Draw a camera-facing billboard sprite. <paramref name="centre"/> is the
    /// round's position already translated by the camera eye (world minus eye),
    /// <paramref name="half"/> its world half-width, <paramref name="spin"/> the
    /// accumulated screen-plane spin (radians), tinted <paramref name="tint"/>
    /// (white → texture carries the colour).
    /// </summary>
    public void DrawBillboard(XnaVec centre, Texture2D tex, float half, float spin, XnaVec tint, Matrix view, Matrix projection)
    {
        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);

        // Spin in the screen plane (native rotation about the view/forward axis).
        float c = MathF.Cos(spin), s = MathF.Sin(spin);
        void Corner(float dx, float dy, out XnaVec corner)
        {
            float rx = dx * c - dy * s;
            float ry = dx * s + dy * c;
            corner = centre + camRight * rx + camUp * ry;
        }
        Corner(-half, -half, out var tl);
        Corner(half, -half, out var tr);
        Corner(half, half, out var br);
        Corner(-half, half, out var bl);

        BeginAdditive(view, projection);
        DrawQuads(tex, [new Quad(tl, tr, br, bl, tint)]);
        EndAdditive();
    }

    /// <summary>
    /// Draw a directional decal stretched along <paramref name="dir"/>:
    /// <paramref name="length"/> along travel (native radius), cross-section
    /// <paramref name="width"/>. <paramref name="centre"/> is eye-relative;
    /// <paramref name="spin"/> rolls the cross-section about the travel axis.
    /// </summary>
    public void DrawDirectional(XnaVec centre, XnaVec dir, Texture2D tex, float length, float width, float spin, XnaVec tint, Matrix view, Matrix projection)
    {
        var travel = XnaVec.Normalize(dir);
        // A perpendicular cross basis (right/up) perpendicular to travel.
        var up = XnaVec.Cross(travel, Math.Abs(travel.Y) < 0.99f ? XnaVec.UnitY : XnaVec.UnitX);
        up = XnaVec.Normalize(up);
        var right = XnaVec.Normalize(XnaVec.Cross(up, travel));

        // Roll the cross basis about travel by spin (native rotation about forward).
        float c = MathF.Cos(spin), s = MathF.Sin(spin);
        var r0 = right * c + up * s;
        var u0 = up * c - right * s;

        float hl = length * 0.5f, hw = width * 0.5f;
        var centreEnd = centre + travel * hl;
        var centreBack = centre - travel * hl;
        var tl = centreEnd + r0 * -hw + u0 * hw;
        var tr = centreEnd + r0 * hw + u0 * hw;
        var br = centreBack + r0 * hw + u0 * -hw;
        var bl = centreBack + r0 * -hw + u0 * -hw;

        BeginAdditive(view, projection);
        DrawQuads(tex, [new Quad(tl, tr, br, bl, tint)]);
        EndAdditive();
    }

    private void BeginAdditive(Matrix view, Matrix projection)
    {
        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;
        _gd.BlendState = _additive;
        _gd.RasterizerState = _cullNone;
        _gd.DepthStencilState = DepthStencilState.DepthRead;
        _effect.CurrentTechnique.Passes[0].Apply();
    }

    private void DrawQuads(Texture2D tex, List<Quad> quads)
    {
        _effect.Texture = tex;
        _effect.CurrentTechnique.Passes[0].Apply();
        var verts = new VertexPositionColorTexture[quads.Count * 4];
        var indices = new short[quads.Count * 6];
        int v = 0, idx = 0;
        foreach (var q in quads)
        {
            var col = new Color(q.Tint.X, q.Tint.Y, q.Tint.Z, 1f);
            verts[v] = new VertexPositionColorTexture(q.TL, col, new Vector2(0f, 0f));
            verts[v + 1] = new VertexPositionColorTexture(q.TR, col, new Vector2(1f, 0f));
            verts[v + 2] = new VertexPositionColorTexture(q.BR, col, new Vector2(1f, 1f));
            verts[v + 3] = new VertexPositionColorTexture(q.BL, col, new Vector2(0f, 1f));
            indices[idx] = (short)v; indices[idx + 1] = (short)(v + 1); indices[idx + 2] = (short)(v + 2);
            indices[idx + 3] = (short)v; indices[idx + 4] = (short)(v + 2); indices[idx + 5] = (short)(v + 3);
            v += 4; idx += 6;
        }
        _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, quads.Count * 2);
    }

    private void EndAdditive()
    {
        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    private readonly record struct Quad(XnaVec TL, XnaVec TR, XnaVec BR, XnaVec BL, XnaVec Tint);

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _effect.Dispose();
        _additive.Dispose();
        _cullNone.Dispose();
    }
}
