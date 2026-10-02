using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the ship engine-thrust trail (P358) as soft-edged, camera-facing
/// billboard quads — one quad per live particle from
/// <see cref="ShipEngineTrailParticles"/> — reproducing native's afterburner
/// exhaust look: a bright orange/white <b>fire</b> flame at the nozzle that
/// fades fast (native FireBehavior, particlegeo.cpp:68-129) trailed by a dull
/// grey <b>smoke</b> plume that expands as it disperses (native SmokeBehavior,
/// particlegeo.cpp:24-66).
/// </summary>
/// <remarks>
/// <para><b>Batching / blend.</b> Mirrors <see cref="MissileTrailRenderer"/> /
/// <see cref="DamageDebrisRenderer"/>: all live particles packed into one
/// <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha blend
/// state, soft radial sprite generated procedurally (no asset). Billboards
/// face the camera via the rotation-only view matrix's right/up columns.</para>
/// <para><b>Display size.</b> The pool already emits world-unit sizes
/// proportional to the hull (radius·SmokeSizeFactor), so — unlike the missile
/// trail's tiny stand-in sizes — no extra BillboardScale amplification is
/// needed; each quad spans <c>±Size·0.5</c> around its centre, with smoke
/// ballooning up to <see cref="SmokeGrowth"/>× as it expires.</para>
/// </remarks>
public sealed class ShipEngineTrailRenderer : IDisposable
{
    /// <summary>How much older smoke balloons up beyond its birth size (a smoke
    /// quad's half-extent is <c>Size·0.5·growth</c>, growth 1.0 → 1+<see cref="SmokeGrowth"/>
    /// over its life) — the classic expanding smoke plume, so the far end of the
    /// trail fattens as the cloud disperses (native SmokeBehavior grows 1×→2×).</summary>
    public const float SmokeGrowth = 1.0f;

    private static readonly Vector3 FireColor = new(1.00f, 0.62f, 0.25f);    // bright orange flame (native FireBehavior r-dominant)
    private static readonly Vector3 SmokeColor = new(0.62f, 0.62f, 0.66f);   // dull grey plume

    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;
    private readonly int _spriteSize;

    /// <param name="gd">The graphics device the generated sprite/effect are created on.</param>
    /// <param name="spriteSize">Edge length (px) of the generated soft sprite; a power of two (64 default).</param>
    public ShipEngineTrailRenderer(GraphicsDevice gd, int spriteSize = 64)
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
        // particles accumulate softly (the repo's particle convention).
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
    /// Draw every particle as a camera-facing soft quad.
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<TrailParticleView> particles, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (particles.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[particles.Count * 4];
        var indices = new short[particles.Count * 6];

        int v = 0, idxBase = 0;
        for (int i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            var centre = new Vector3(p.Position.X, p.Position.Y, p.Position.Z) - cameraEye;
            // Older smoke balloons up (classic expanding plume); fire holds size.
            float growth = p.Kind == TrailParticleKind.Smoke ? 1f + SmokeGrowth * (1f - p.RemainingFrac) : 1f;
            float half = p.Size * 0.5f * growth;

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

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern).
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
