using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One strobe-light decal for a frame: its world position, the quad's
/// half-extent (already scaled by the current pulse) and its linear RGB tint.</summary>
public readonly record struct NavStrobeView(SimVec Position, float HalfExtent, Vector3 Rgb);

/// <summary>
/// Draws the navigation strobe lights (P367) — a faithful port of native
/// <c>LightsGeo::Render</c> (<c>effect/efapp.cpp:171-222</c>): for each light the
/// pulse envelope (<see cref="NavStrobeMath.Pulse"/>) sizes a tinted decal sprite
/// (<c>0.5·value</c>, efapp.cpp:215) drawn at the light's position when
/// <c>value &gt; 0</c> (efapp.cpp:208), using the decal texture native registers
/// as <c>AWF_EFFECT_STROBE</c> ("f101bmp", <c>effect/efart.h:14</c>). The port
/// draws each strobe as a small camera-facing additive soft quad, tinted per-vertex
/// by the light's colour — the same soft-sprite/additive-blend conventions the
/// P365 glow and the particle renderers use (there is no dedicated strobe texture
/// asset in this port's content, so the procedural soft radial sprite stands in
/// for native's f101bmp).
/// </summary>
/// <remarks>
/// <para><b>Faithful to native.</b> The pulse value scales the decal's SIZE only
/// (native draws <c>DrawDecal</c> at <c>0.5·value</c> with the light's full colour
/// tint — it never multiplies the colour by value); so during the brief
/// ramp-up the light grows from a point, flashes at full size, shrinks on
/// ramp-down, then is not drawn at all while off — a real blink, not an instant
/// on/off toggle and not a static glow.</para>
/// <para><b>Size normalization (P358/P365 precedent).</b> Native draws at
/// <c>0.5·value</c> in native mesh units; the port normalizes to real hull units
/// (<c>halfExtent = value·radius·<see cref="NavStrobeLayout.SizeFactor"/></c>)
/// exactly as P358/P365 normalize their particle/glow sizes.</para>
/// </remarks>
public sealed class NavStrobeRenderer : IDisposable
{
    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;

    public NavStrobeRenderer(GraphicsDevice gd)
    {
        _softSprite = GenerateSoftSprite(gd, 64);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

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
    /// Draw every strobe light as a camera-facing soft quad tinted by its colour
    /// (solid alpha — the sprite's own alpha falloff + additive blend give the
    /// soft edge). See <see cref="EngineNozzleGlowRenderer.Draw"/> for the
    /// billboard/view conventions (camera-relative positions, the P207 lesson).
    /// </summary>
    public void Draw(IReadOnlyList<NavStrobeView> lights, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (lights.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[lights.Count * 4];
        var indices = new short[lights.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var l in lights)
        {
            var centre = new Vector3(l.Position.X, l.Position.Y, l.Position.Z) - cameraEye;
            float half = l.HalfExtent;
            var col = new Color(l.Rgb.X, l.Rgb.Y, l.Rgb.Z, 1f);

            var tl = centre - camRight * half + camUp * half;   // uv (0,0)
            var tr = centre + camRight * half + camUp * half;   // uv (1,0)
            var br = centre + camRight * half - camUp * half;   // uv (1,1)
            var bl = centre - camRight * half - camUp * half;   // uv (0,1)

            verts[v] = new VertexPositionColorTexture(tl, col, new Vector2(0f, 0f));
            verts[v + 1] = new VertexPositionColorTexture(tr, col, new Vector2(1f, 0f));
            verts[v + 2] = new VertexPositionColorTexture(br, col, new Vector2(1f, 1f));
            verts[v + 3] = new VertexPositionColorTexture(bl, col, new Vector2(0f, 1f));

            indices[idxBase] = (short)v;
            indices[idxBase + 1] = (short)(v + 1);
            indices[idxBase + 2] = (short)(v + 2);
            indices[idxBase + 3] = (short)v;
            indices[idxBase + 4] = (short)(v + 2);
            indices[idxBase + 5] = (short)(v + 3);

            v += 4;
            idxBase += 6;
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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, lights.Count * 2);

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>The same soft-edged circular sprite the P365 glow renderer uses:
    /// alpha 1 at the centre falling off smoothly (squared) to 0 at the rim, white
    /// RGB — the strobe colour is applied per-vertex.</summary>
    private static Texture2D GenerateSoftSprite(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f;
                float ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                a *= a;
                data[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        _softSprite.Dispose();
        _effect.Dispose();
        _additiveSoft.Dispose();
        _cullNone.Dispose();
    }
}
