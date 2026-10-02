using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the missile exhaust trail (P235) as soft-edged, camera-facing billboard
/// quads — one quad per live particle from <see cref="MissileTrailParticles"/>.
/// The soft circular sprite is generated procedurally (no texture asset, the
/// same "generated, not loaded" family as <see cref="Starfield"/> /
/// <see cref="RockMeshFactory"/>), fire particles tinted bright orange/white and
/// smoke particles a duller gray; every quad fades out (alpha → 0) as the
/// particle's remaining lifetime elapses rather than popping out of existence.
/// </summary>
/// <remarks>
/// <para><b>Batching.</b> All live particles are packed into one
/// <see cref="VertexPositionColorTexture"/> array per frame and drawn with a
/// single <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha
/// blend state, so a crowded trail costs one draw call, not one per particle.</para>
/// <para><b>Billboarding.</b> The camera's world-space right/up basis vectors are
/// recovered from the row-major view matrix's first two columns (a rotation-only
/// view maps column 0 → camera right, column 1 → camera up); each quad spans
/// ±half along those axes around its centre, so it always faces the camera.</para>
/// <para><b>Display size.</b> The pool reports native-faithful particle sizes
/// (which are small in absolute units for the port's stand-in missile radius);
/// <see cref="BillboardScale"/> scales them up to quad world-size so the trail
/// is plainly readable next to the missile — a deliberate display tuning, kept
/// out of the pure pool.</para>
/// </remarks>
public sealed class MissileTrailRenderer : IDisposable
{
    /// <summary>Multiplies each particle's native size to the rendered quad
    /// world-size — the visibility tuning (see class remarks).</summary>
    public const float BillboardScale = 90f;

    /// <summary>How much older smoke balloons up beyond its birth size: a smoke
    /// particle's quad half-extent is <c>Size*BillboardScale/2*growth</c> where
    /// <c>growth</c> runs from 1.0 (fresh) to <c>1+SmokeGrowth</c> (about to
    /// expire) — the classic "expanding smoke plume" read, so the far end of the
    /// trail fattens as the cloud disperses.</summary>
    public const float SmokeGrowth = 1.2f;

    private static readonly Vector3 FireColor = new(1.00f, 0.85f, 0.45f);   // hot orange/white
    private static readonly Vector3 SmokeColor = new(0.62f, 0.62f, 0.66f);  // dull gray

    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;
    private readonly int _spriteSize;

    /// <param name="gd">The graphics device the generated sprite/effect are
    /// created on.</param>
    /// <param name="spriteSize">Edge length (px) of the generated soft sprite;
    /// a power of two (64 default) is typical.</param>
    public MissileTrailRenderer(GraphicsDevice gd, int spriteSize = 64)
    {
        _spriteSize = spriteSize;
        _softSprite = GenerateSoftSprite(gd, spriteSize);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha, so each sprite
        // glows (accumulates) and its fade + soft edge are respected via alpha.
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
    /// Draw every particle as a camera-facing soft quad. <paramref name="particles"/>
    /// is the pool's live set; <paramref name="cameraEye"/> is the camera's true
    /// world position (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's real
    /// camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<TrailParticleView> particles, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (particles.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[particles.Count * 4];
        var indices = new short[particles.Count * 6];   // DrawUserIndexedPrimitives takes short indices; fine — max 768*4=3072 verts

        int v = 0, idxBase = 0;
        for (int i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            var centre = new Vector3(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            // Older smoke balloons up (classic expanding plume); fire holds size.
            float growth = p.Kind == TrailParticleKind.Smoke ? 1f + SmokeGrowth * (1f - p.RemainingFrac) : 1f;
            float half = p.Size * BillboardScale * 0.5f * growth;

            // Fade by remaining lifetime — smoothed so the trail doesn't pop out.
            float a = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            a *= a; // ease toward a smooth tail
            var baseCol = p.Kind == TrailParticleKind.Fire ? FireColor : SmokeColor;
            var col = new Color(
                (int)(baseCol.X * a * 255f),
                (int)(baseCol.Y * a * 255f),
                (int)(baseCol.Z * a * 255f),
                (int)(a * 255f));

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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, particles.Count * 2);

        // Restore all three states we touched so nothing leaks into the passes
        // that draw after this one (P339 B2 — ExplosionRenderer's exact pattern;
        // previously only BlendState was restored, leaking DepthStencilState and
        // RasterizerState, and Default wrote depth that hard-clipped later
        // additive renders).
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
