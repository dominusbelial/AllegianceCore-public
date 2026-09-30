using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shield-hit flare (P463b) as soft-edged, camera-facing additive
/// billboard quads — one per live flare from <see cref="ShieldFlareParticles"/>
/// — reproducing native's shield-bubble flash cue
/// (<c>ThingGeo::AddFlare</c>, <c>effect/thinggeo.cpp:1243-1301</c>): a bright
/// lens blended <c>BlendModeAdd</c> on the struck ship's shield at the hit,
/// flashing on then fading off over the native 0.75&#8209;s life.
/// </summary>
/// <remarks>
/// <para><b>Shape (ported).</b> Native draws the additive <c>TextureGeo</c> of
/// the shield-flare artwork (surface <c>AWF_SHIELD_FLARE_TEXTURE</c> =
/// <c>"fx09bmp"</c>, geo <c>lens30</c> — trekigc.cpp:2512-2522, artwork.h:208-209)
/// on the ship's shield ellipsoid toward the hit. The port approximates that
/// with the same procedural soft sprite the repo's other particle renderers use
/// (alpha 1 at centre → 0 at rim), positioned on the shield toward the hit
/// (see <see cref="ShieldFlareParticles"/> remarks) and drawn camera-facing.</para>
/// <para><b>Colour (this port's choice, cited).</b> Native tints the flare by
/// the artwork's <c>fx09bmp</c> surface; the port chooses a light ice-blue-white
/// so the flash reads unmistakably as the <em>shield</em> (distinct from the
/// warm-white hull spark's <see cref="HullHitSparkRenderer"/>), scaled by the
/// remaining-life fraction so it flashes bright on birth and fades to nothing
/// over the native lifetime.</para>
/// <para><b>Batching / blend.</b> Mirrors <see cref="HullHitSparkRenderer"/>:
/// one camera-facing soft quad per flare packed into a single
/// <c>DrawUserIndexedPrimitives</c> call under one additive soft-alpha blend
/// state; the soft sprite is generated procedurally, no asset.</para>
/// </remarks>
public sealed class ShieldFlareRenderer : IDisposable
{
    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;

    /// <param name="gd">The graphics device the generated sprite/effect are
    /// created on.</param>
    /// <param name="spriteSize">Edge length (px) of the generated soft sprite;
    /// a power of two (64 default) is typical.</param>
    public ShieldFlareRenderer(GraphicsDevice gd, int spriteSize = 64)
    {
        _softSprite = GenerateSoftSprite(gd, spriteSize);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — bright flares
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
    /// Draw every live flare as a camera-facing soft quad, light ice-blue-white
    /// and fading over the native 0.75&#8209;s life.
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's
    /// real camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<ShieldFlareView> flares, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (flares.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[flares.Count * 4];
        var indices = new short[flares.Count * 6];

        int v = 0, idxBase = 0;
        for (int i = 0; i < flares.Count; i++)
        {
            var f = flares[i];
            var centre = new Vector3(f.Center.X, f.Center.Y, f.Center.Z) - cameraEye;
            float rem = MathHelper.Clamp(f.RemainingFrac, 0f, 1f);
            float half = f.Size;

            // Light ice-blue-white shield flash, fading over life.
            var col = new Color(
                (int)((0.62f * 255f) * rem),
                (int)((0.86f * 255f) * rem),
                (int)(255f * rem),
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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, flares.Count * 2);

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
