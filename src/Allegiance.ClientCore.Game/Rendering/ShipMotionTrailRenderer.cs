using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the P363 ship side-coloured motion trail as real additive strip
/// geometry — one continuous ribbon per visible moving ship, tinted by that
/// ship's real side colour at the native's half intensity. Ports native
/// <c>TrailGeo::RenderCallback</c> (<c>effect/efgeo.cpp:641-660</c>): Gouraud
/// shade, cull-none, additive blend, no z-write, drawn as a quad strip whose
/// per-section colour fades from the (bright) head to the (transparent) tail.
/// </summary>
/// <remarks>
/// <para><b>Geometry.</b> Each <see cref="ShipTrailStrip"/> is a fan of
/// <c>Sections</c> quads connecting consecutive anchors; each quad is two
/// triangles over the four corners
/// <c>(L_k, R_k, R_{k+1}, L_{k+1})</c> where <c>L_k = Centres[k]−Rights[k]</c>
/// and <c>R_k = Centres[k]+Rights[k]</c>. Texcoords run <c>u</c> across the
/// width (left = 0, right = 1, native's per-endpoint <c>(0,0)/(1,0)</c>,
/// efgeo.cpp:539-543) against a soft lateral-gradient strip texture, so the
/// ribbon is a glowing centre line that softens toward its edges.</para>
/// <para><b>Colour.</b> Native halves the side colour before building the trail
/// (<c>SetTrailColor</c>: <c>Color(color.R()·0.5f, ...)</c>, thinggeo.cpp:941)
/// and then scales each section by its brightness
/// <c>1 − dtime − k/maxSections</c> (<c>UpdateColors</c>, efgeo.cpp:570-592) —
/// reproduced here as <c>rgb = side·0.5·bright</c> with alpha = <c>bright</c>,
/// so the head glows and the tail fades to nothing (additive accumulation
/// makes the fade a real dissolve, native <c>BlendModeAdd</c>).</para>
/// </remarks>
public sealed class ShipMotionTrailRenderer : IDisposable
{
    private readonly Texture2D _softStrip;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;

    /// <param name="gd">The graphics device the generated texture/effect are created on.</param>
    public ShipMotionTrailRenderer(GraphicsDevice gd)
    {
        _softStrip = GenerateSoftStrip(gd, 32, 4);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softStrip,
        };

        // Additive soft-alpha (the repo's particle convention) — output += src*α,
        // so the ribbon's overlapping quads accumulate to a hot core and the
        // background stays visible behind it (native BlendModeAdd, efgeo.cpp:646).
        _additiveSoft = new BlendState
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
    /// Draw every ribbon strip additively on top of the scene.
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices.
    /// </summary>
    public void Draw(IReadOnlyList<ShipTrailStrip> strips, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (strips.Count == 0) return;

        int vertCount = 0, indexCount = 0;
        foreach (var s in strips) { vertCount += s.Sections * 4; indexCount += s.Sections * 6; }

        var verts = new VertexPositionColorTexture[vertCount];
        var indices = new short[indexCount];

        int v = 0, idx = 0;
        foreach (var s in strips)
        {
            for (int k = 0; k < s.Sections; k++)
            {
                float b0 = Brightness(s, k);
                float b1 = Brightness(s, k + 1);
                var c0 = Centred(s, k, cameraEye);
                var c1 = Centred(s, k + 1, cameraEye);
                Vector3 rk = ToXna(s.Rights[k]);
                Vector3 rk1 = ToXna(s.Rights[k + 1]);
                Vector3 l0 = c0 - rk;
                Vector3 r0 = c0 + rk;
                Vector3 l1 = c1 - rk1;
                Vector3 r1 = c1 + rk1;

                verts[v]     = new VertexPositionColorTexture(l0, Col(s, b0), new Vector2(0f, 0f));
                verts[v + 1] = new VertexPositionColorTexture(r0, Col(s, b0), new Vector2(1f, 0f));
                verts[v + 2] = new VertexPositionColorTexture(r1, Col(s, b1), new Vector2(1f, 0f));
                verts[v + 3] = new VertexPositionColorTexture(l1, Col(s, b1), new Vector2(0f, 0f));

                indices[idx]     = (short)v;
                indices[idx + 1] = (short)(v + 1);
                indices[idx + 2] = (short)(v + 2);
                indices[idx + 3] = (short)v;
                indices[idx + 4] = (short)(v + 2);
                indices[idx + 5] = (short)(v + 3);

                v += 4;
                idx += 6;
            }
        }

        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additiveSoft;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;   // test, never write (P339 B2)
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, indexCount / 3);

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>Native <c>UpdateColors</c> brightness for section <paramref name="k"/>
    /// (0 = head): delegates to <see cref="ShipMotionTrailRibbons.BrightnessFor"/>
    /// with the strip's <see cref="ShipTrailStrip.DTime"/>.</summary>
    private static float Brightness(in ShipTrailStrip s, int k) =>
        ShipMotionTrailRibbons.BrightnessFor(s.DTime, k);

    private static Vector3 Centred(in ShipTrailStrip s, int k, Vector3 cameraEye)
    {
        var c = s.Centres[k];
        return new Vector3(c.X - cameraEye.X, c.Y - cameraEye.Y, c.Z - cameraEye.Z);
    }

    private static Vector3 ToXna(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The vertex colour: the ship's side colour at half intensity
    /// (native <c>SetTrailColor</c>, thinggeo.cpp:941) scaled by the section
    /// brightness, alpha = brightness (additive soft-alpha fade to the tail).</summary>
    private static Color Col(in ShipTrailStrip s, float b)
    {
        byte a = (byte)MathHelper.Clamp(b * 255f, 0f, 255f);
        return new Color(
            (byte)MathHelper.Clamp(s.R * 0.5f * b * 255f, 0f, 255f),
            (byte)MathHelper.Clamp(s.G * 0.5f * b * 255f, 0f, 255f),
            (byte)MathHelper.Clamp(s.B * 0.5f * b * 255f, 0f, 255f),
            a);
    }

    /// <summary>A soft <b>strip</b> texture: alpha falls off smoothly across the
    /// width (<c>u</c>, 0..1) — a glowing centre line that softens to
    /// transparency at both edges — with a constant vertical (<c>v</c>) profile,
    /// white RGB (colour applied per-vertex). Deterministic, no asset; the
    /// analogue of native's <c>AWF_EFFECT_TRAIL</c> image.</summary>
    private static Texture2D GenerateSoftStrip(GraphicsDevice gd, int width, int height)
    {
        var tex = new Texture2D(gd, width, height);
        var data = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float nu = (x + 0.5f) / width * 2f - 1f;   // [-1, 1] across the width
                float a = MathHelper.Clamp(1f - Math.Abs(nu), 0f, 1f);
                a *= a;   // smooth soft falloff toward the edges
                data[y * width + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        _softStrip.Dispose();
        _effect.Dispose();
        _additiveSoft.Dispose();
        _cullNone.Dispose();
    }
}
