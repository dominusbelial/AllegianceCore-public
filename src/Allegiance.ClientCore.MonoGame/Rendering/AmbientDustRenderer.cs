using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One ambient-dust grain ready for drawing: its world position, the billboard
/// quad's half-extent in world units (derived from <c>0.1/focus</c> so its
/// on-screen angular size is FOV-independent, matching native), and the linear
/// RGB tint — the native bluish-white colour already multiplied by the
/// <c>1 − 0.25·t²</c> brightness fade (debrisgeo.cpp:174-180).
/// </summary>
public readonly record struct AmbientDustView(SimVec Position, float HalfExtent, Vector3 Rgb);

/// <summary>
/// Draws the P449a ambient space-dust field (<see cref="DebrisGeo"/>) as small
/// camera-facing soft quads — one per live grain — reproducing native's decal
/// render (<c>DebrisGeo::Render</c>, <c>effect/debrisgeo.cpp:163-190</c>): each
/// grain is the <c>debris1bmp</c> decal drawn at <c>scale = 0.1/focus</c> and
/// tinted by <c>brightness·colour</c>, skipped when it is within 20 m of the
/// camera (the near-camera shell). The real <c>debris1bmp</c> artwork is loaded
/// through the port's established <c>.mdl</c> bitmap pipeline
/// (<see cref="MdlTextureDecoder"/>, same as <see cref="GaugeTextures"/> /
/// <see cref="LockTextures"/>); if it can't be decoded the renderer falls back
/// to a small procedural soft dot and says so.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (debrisgeo.cpp:163-190).</b> Native draws, for
/// each live grain, <c>DrawDecal(m_psurface, brightness·m_color, position, …,
/// scale = 0.1/focus, …)</c> — a camera-facing decal — and skips grains whose
/// squared distance from the camera is ≤ 400 (within 20 m, debrisgeo.cpp:177).
/// The decal is the <c>debris1bmp</c> surface (loaded at construction,
/// debrisgeo.cpp:51). This renderer reproduces that as a camera-facing soft
/// quad per grain with the same 20 m skip and <c>0.1/focus</c> scale; because
/// the quad's world size is <c>(0.1/focus)·distance·Calibration</c>, the
/// perspective divide cancels the <c>focus</c> and each grain subtends the same
/// small angle regardless of camera FOV (see <see cref="DebrisGeo"/> remarks).</para>
/// <para><b>Deliberate substitution.</b> Native's decal surface is the authored
/// <c>debris1bmp</c>; this port renders a soft-edged radial sprite tinted
/// per-vertex (the repo's particle convention, exactly like
/// <see cref="EngineNozzleGlowRenderer"/>), and when the real
/// <c>debris1bmp.mdl</c> decodes cleanly it is used in place of the generated
/// sprite. Either way the drawn geometry is the same camera-facing soft quad —
/// the authored bitmap only changes the per-pixel alpha profile of the dot.</para>
/// </remarks>
public sealed class AmbientDustRenderer : IDisposable
{
    /// <summary>The artwork-root stem for the real native decal (debrisgeo.cpp:51).</summary>
    public const string DebrisBitmapMdl = "debris1bmp";

    /// <summary>
    /// Multiplies native's <c>0.1/focus</c> decal scale into a world-unit half-
    /// extent, calibrated so a grain subtends ≈ <c>0.1·Calibration</c> radians
    /// on screen (~0.013 rad with the default — a small, clearly-visible dust
    /// speck that does not drown the scene). The exact size is a visual tuning
    /// parameter; the FOV-independence (the point of baking focus in) is exact.
    /// </summary>
    public const float Calibration = 0.13f;

    /// <summary>The square edge-length (px) of the fallback generated sprite.</summary>
    private const int SpriteSize = 32;

    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;

    /// <summary>
    /// <param name="gd">The graphics device the sprite/effect are created on.</param>
    /// <param name="artworkDir">The artwork root (see
    /// <see cref="ResolveArtworkDir"/>) from which the real <c>debris1bmp.mdl</c>
    /// is decoded; a missing/undecodable asset falls back to the procedural
    /// soft dot (and is reported on the console).</param>
    /// </summary>
    public AmbientDustRenderer(GraphicsDevice gd, string artworkDir)
    {
        _softSprite = LoadDebrisTexture(gd, artworkDir);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — the repo's particle
        // convention, so overlapping dust grains accumulate softly (bright clumps
        // of specks where the field overlaps the line of sight).
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
    /// Draw every live dust grain as a camera-facing soft quad tinted by its
    /// (brightness-multiplied) colour. <paramref name="cameraEye"/> is the
    /// camera's true world position (subtracted so vertex positions stay small,
    /// the P207 lesson); <paramref name="view"/>/<paramref name="projection"/>
    /// are the frame's real camera matrices (also supplying the billboard
    /// right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<AmbientDustView> grains, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (grains.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[grains.Count * 4];
        var indices = new short[grains.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var g in grains)
        {
            var centre = new Vector3(g.Position.X, g.Position.Y, g.Position.Z) - cameraEye;
            float half = g.HalfExtent;
            // Solid tint at alpha 1 — the soft sprite's own alpha falloff provides
            // the soft edge and the brightness fade (folded into Rgb by the caller)
            // carries the dimming.
            var col = new Color(g.Rgb.X, g.Rgb.Y, g.Rgb.Z, 1f);

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
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, grains.Count * 2);

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>
    /// Load the real <c>debris1bmp.mdl</c> decal through the port's <c>.mdl</c>
    /// bitmap pipeline (MdlTextureDecoder, the P428/GaugeTextures convention).
    /// A missing or undecodable file falls back to a generated soft radial
    /// sprite and is reported on the console. Never throws.
    /// </summary>
    private static Texture2D LoadDebrisTexture(GraphicsDevice gd, string artworkDir)
    {
        string path = System.IO.Path.Combine(artworkDir, DebrisBitmapMdl + ".mdl");
        try
        {
            if (File.Exists(path))
            {
                var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
                var surface = new Texture2D(gd, tex.Width, tex.Height);
                var colors = new Color[tex.Width * tex.Height];
                for (int c = 0; c < colors.Length; c++)
                {
                    int b = c * 4;
                    colors[c] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                Console.WriteLine($"[monogame] ambient dust decal: {tex.Width}x{tex.Height} '{Path.GetFileName(path)}'");
                return surface;
            }
            Console.WriteLine($"[monogame] ambient dust decal missing ({path}) — procedural soft-dot fallback");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] ambient dust decal '{Path.GetFileName(path)}' failed to decode: {ex.Message} — procedural soft-dot fallback");
        }
        return GenerateSoftDot(gd);
    }

    /// <summary>A soft-edged circular sprite: alpha 1 at the centre falling off
    /// smoothly (squared) to 0 at the rim, white RGB — the per-grain colour is
    /// applied per-vertex. The fallback used when the real <c>debris1bmp</c>
    /// cannot be decoded.</summary>
    private static Texture2D GenerateSoftDot(GraphicsDevice gd)
    {
        var tex = new Texture2D(gd, SpriteSize, SpriteSize);
        var data = new Color[SpriteSize * SpriteSize];
        float inv = 2f / SpriteSize;
        for (int y = 0; y < SpriteSize; y++)
        {
            for (int x = 0; x < SpriteSize; x++)
            {
                float nx = (x + 0.5f) * inv - 1f;   // [-1, 1]
                float ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                a *= a;   // smooth falloff
                data[y * SpriteSize + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetData(data);
        return tex;
    }

    /// <summary>The configured artwork root (same convention as the other
    /// <c>.mdl</c> asset resolvers).</summary>
    public static string ResolveArtworkDir() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";

    public void Dispose()
    {
        _softSprite.Dispose();
        _effect.Dispose();
        _additiveSoft.Dispose();
        _cullNone.Dispose();
    }
}
