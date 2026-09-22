using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the continuous damage-debris smoke stream (P297) as soft-edged,
/// camera-facing billboard quads — one quad per live puff from
/// <see cref="DamageDebrisParticles"/> — reproducing native's
/// <c>SmokeBehavior::Render</c> (particlegeo.cpp:37-62): a grey puff that
/// <em>grows</em> 0.75&#8209;×&#8209;size &#8594; 3.0&#8209;×&#8209;size and fades
/// linearly to zero over its lifetime.
/// </summary>
/// <remarks>
/// <para><b>Colour (native particlegeo.cpp:45-49).</b> With
/// <c>c = 0.25 · fCountdownToFinish</c> (the grey smoke intensity that falls
/// linearly as the puff ages), native sets RGBA = <c>(c, 0.8·c, c, c)</c> —
/// grey, with green scaled back a tad to avoid a greenish hue. Reproduced
/// exactly here so a fresh puff is a medium-grey cloud that thins to nothing.</para>
/// <para><b>Size growth (native particlegeo.cpp:51-55).</b> Native interpolates
/// <c>fSize = m_fSize · (0.75 + 2.25·(1 − countdown))</c> — the puff starts at
/// 0.75× its base size and swells to 3.0× as it expires (smoke dispersing).
/// The renderer mirrors that using <see cref="DamageDebrisView.RemainingFrac"/>.</para>
/// <para><b>Batching / blend.</b> Mirrors <see cref="WarpTransitParticleRenderer"/>:
/// one camera-facing soft-quad per puff packed into a single
/// <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha blend
/// state; the soft radial sprite is generated procedurally, no asset.</para>
/// </remarks>
public sealed class DamageDebrisRenderer : IDisposable
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
    public DamageDebrisRenderer(GraphicsDevice gd, int spriteSize = 64)
    {
        _spriteSize = spriteSize;
        _softSprite = GenerateSoftSprite(gd, spriteSize);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — overlapping
        // smoke accumulates softly (the repo's particle convention).
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
    /// Draw every live puff as a camera-facing soft quad, grey and growing as
    /// it fades (native SmokeBehavior::Render).
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<DamageDebrisView> puffs, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (puffs.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[puffs.Count * 4];
        var indices = new short[puffs.Count * 6];

        int v = 0, idxBase = 0;
        for (int i = 0; i < puffs.Count; i++)
        {
            var p = puffs[i];
            var centre = new Vector3(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);

            // Growth 0.75 -> 3.0 over the puff's life (particlegeo.cpp:52-55).
            float fSize = p.Size * (0.75f + 2.25f * (1f - rem));
            float half = fSize * 0.5f;

            // Grey fade: c = 0.25·countdown; RGBA = (c, 0.8c, c, c)
            // (particlegeo.cpp:45-49).
            float c = 0.25f * rem;
            var col = new Color(
                (int)(c * 255f),
                (int)((c * 0.8f) * 255f),
                (int)(c * 255f),
                (int)(c * 255f));

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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, puffs.Count * 2);

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern;
        // previously only BlendState was restored, leaking DepthStencilState +
        // RasterizerState, and Default wrote depth that hard-clipped the later
        // explosion pass).
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
