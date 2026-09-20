using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the warp-transit teleport spark spray (P296) as soft-edged,
/// camera-facing billboard quads — one quad per live spark from
/// <see cref="WarpTransitParticles"/> — reproducing native's
/// <c>SparkBehavior::Render</c> (particlegeo.cpp:209-250): a "hot blue, fading
/// out" additive glow whose colour is driven by the particle's remaining
/// lifetime, sized at the native world size.
/// </summary>
/// <remarks>
/// <para><b>Colour (native particlegeo.cpp:230-236).</b> With
/// <c>c = fCountdownToFinish = (duration − age)/duration</c> and
/// <c>k = ½·c²</c>, native sets RGBA = <c>(1.5k, 2.0k, c, k)</c> — a hot blue
/// that fades to black as the spark dies. This is reproduced exactly here, so
/// a fresh spark is a bright saturated blue that dims and shrinks-intensity as
/// it expires (the native "hot blue, fading out" read).</para>
/// <para><b>Size.</b> Native draws the spark decal at <c>m_fSize</c> world units
/// (DrawDecal size = quad diameter). Unlike the missile-trail renderer there is
/// no big billboard inflation: native's teleport sparks are already
/// ship-radius-sized (<c>(0.45…0.9)·radius</c>), so they are drawn at native
/// world size (<see cref="BillboardScale"/> = 1, kept tunable for readability).</para>
/// <para><b>Batching / blend.</b> Mirrors <see cref="MissileTrailRenderer"/>:
/// one camera-facing soft-quad per spark packed into a single
/// <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha blend
/// state; the soft radial sprite is generated procedurally, no asset.</para>
/// </remarks>
public sealed class WarpTransitParticleRenderer : IDisposable
{
    /// <summary>Multiplies each spark's native size to the rendered quad
    /// world-size. Native world size is kept (1) — see class remarks.</summary>
    public const float BillboardScale = 1f;

    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;
    private readonly int _spriteSize;

    /// <param name="gd">The graphics device the generated sprite/effect are
    /// created on.</param>
    /// <param name="spriteSize">Edge length (px) of the generated soft sprite;
    /// a power of two (64 default) is typical.</param>
    public WarpTransitParticleRenderer(GraphicsDevice gd, int spriteSize = 64)
    {
        _spriteSize = spriteSize;
        _softSprite = GenerateSoftSprite(gd, spriteSize);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — each spark glows
        // (accumulates) and its fade + soft edge are respected via alpha.
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
    /// Draw every live spark as a camera-facing soft quad, hot blue and fading
    /// with remaining lifetime (native SparkBehavior::Render).
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<WarpSparkView> sparks, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (sparks.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[sparks.Count * 4];
        var indices = new short[sparks.Count * 6];

        int v = 0, idxBase = 0;
        for (int i = 0; i < sparks.Count; i++)
        {
            var p = sparks[i];
            var centre = new Vector3(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            float half = p.Size * BillboardScale * 0.5f;

            // Native hot-blue fade (particlegeo.cpp:230-236): c = countdown,
            // k = ½·c²; RGBA = (1.5k, 2.0k, c, k).
            float c = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float k = 0.5f * c * c;
            var col = new Color(
                (int)((k * 1.5f) * 255f),
                (int)((k * 2.0f) * 255f),
                (int)(c * 255f),
                (int)(k * 255f));

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
        gd.DepthStencilState = DepthStencilState.Default;
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, sparks.Count * 2);

        gd.BlendState = BlendState.Opaque;   // restore the caller's expectations
    }

    /// <summary>A soft-edged circular sprite: alpha 1 at the centre falling off
    /// smoothly (squared) to 0 at the rim, white RGB — the particle colour is
    /// applied per-vertex. Fully deterministic, no asset.</summary>
    private static Texture2D GenerateSoftSprite(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f;   // [-1, 1]
                float ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                a *= a;   // smooth falloff
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
