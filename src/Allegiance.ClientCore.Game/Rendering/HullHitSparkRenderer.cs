using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the projectile-impact spark (P364) as soft-edged, camera-facing
/// billboard quads — one quad per live bit from
/// <see cref="HullHitSparkParticles"/> — reproducing native's
/// <c>BitsGeoImpl::Render</c> (<c>efgeo.cpp:822-846</c>): each bit is drawn as a
/// small particle scaled to its native <c>m_size</c>, flying straight at constant
/// velocity, fading over its <c>g_bitsLifeTime = 2</c>&#8209;s life.
/// </summary>
/// <remarks>
/// <para><b>Colour (this port's choice, cited).</b> Native renders the
/// <c>AWF_EFFECT_BIT</c> geo with whatever surface the bit geo carries (the
/// checkout's bit artwork is a loose/ambiguously-lit sprite); the port chooses a
/// bright warm-white so the spark is unmistakably <em>distinct</em> from the grey
/// damage-vent smoke stream (<see cref="DamageDebrisRenderer"/>) it accompanies —
/// the task's explicit bar. The bit's brightness fades linearly to zero over its
/// life (native removes the bit at <c>dtime &gt; g_bitsLifeTime</c>,
/// efgeo.cpp:831-834, with no per-frame fade, but the additive blend makes a
/// hard cut pop; a gentle alpha ramp reads as a dying spark without changing the
/// lifetime contract).</para>
/// <para><b>Size (native efgeo.cpp:840).</b> The quad stays at the native
/// <c>m_size</c> (= <c>radius/2</c>) for its whole life — the bits do not grow
/// like smoke — so unlike <see cref="DamageDebrisRenderer"/> there is no growth
/// interpolation.</para>
/// <para><b>Batching / blend.</b> Mirrors <see cref="DamageDebrisRenderer"/>: one
/// camera-facing soft-quad per bit packed into a single
/// <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha blend
/// state; the soft radial sprite is generated procedurally, no asset.</para>
/// </remarks>
public sealed class HullHitSparkRenderer : IDisposable
{
    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;
    private readonly int _spriteSize;

    /// <param name="gd">The graphics device the generated sprite/effect are
    /// created on.</param>
    /// <param name="spriteSize">Edge length (px) of the generated soft sprite;
    /// a power of two (64 default) is typical.</param>
    public HullHitSparkRenderer(GraphicsDevice gd, int spriteSize = 64)
    {
        _spriteSize = spriteSize;
        _softSprite = GenerateSoftSprite(gd, spriteSize);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — bright sparks
        // accumulate softly (the repo's particle convention).
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
    /// Draw every live spark bit as a camera-facing soft quad, bright warm-white
    /// and fading over its life (native BitsGeoImpl::Render).
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<HullHitSparkView> bits, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (bits.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[bits.Count * 4];
        var indices = new short[bits.Count * 6];

        int v = 0, idxBase = 0;
        for (int i = 0; i < bits.Count; i++)
        {
            var b = bits[i];
            var centre = new Vector3(b.Position.X, b.Position.Y, b.Position.Z) - cameraEye;
            float rem = MathHelper.Clamp(b.RemainingFrac, 0f, 1f);

            // Constant size (native keeps m_size for the bit's whole life).
            float half = b.Size * 0.5f;

            // Bright warm-white spark, fading over life (see class remarks).
            var col = new Color(
                (int)(255f * rem),
                (int)((0.92f * 255f) * rem),
                (int)((0.65f * 255f) * rem),
                (int)(255f * rem));

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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, bits.Count * 2);

        // Restore all three states (P339 B2 — the repo's particle-renderer pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
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
