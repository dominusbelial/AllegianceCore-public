using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P546 — the shared CC0 VFX atlas cache (adopted from the P543 spike's
/// <c>VfxAssets</c> into the game, renamed to the game's content loading).
/// Loads the six CC0 flipbook atlases from the game's <b>own</b> Content
/// (<c>Content/Vfx/atlas/*.png</c>, shipped with the game — NOT the native
/// artwork tree) and slices each into its grid frames, exactly as
/// <see cref="ExplosionAssets"/> slices the native <c>expNN</c> strips. One
/// atlas per layer type: a 5×5 additive fire-ball glow, a 5×5 solid fire ball,
/// and 8×8 / 16×4 smoke clouds. Licence table: <c>Content/Vfx/README.md</c>,
/// licences under <c>Content/Vfx/licenses/</c> (all CC0).
/// </summary>
/// <remarks>
/// <para><b>Loading on a clean checkout.</b> Unlike native art (read from
/// <c>ALLEGIANCE_ARTWORK_PATH</c>), these PNGs are ours to ship. Each atlas is
/// opened via <see cref="TitleContainer.OpenStream"/> at the content-relative
/// path <c>Content/Vfx/atlas/&lt;stem&gt;.png</c>, decoded with
/// <see cref="Texture2D.FromStream"/> and sliced into its grid frames with
/// <c>GetData</c>/<c>SetData</c>. <see cref="TitleContainer"/> resolves
/// identically on DesktopGL (against the output Content/ tree) and Android
/// (against the APK's packed assets), so one loader serves both heads.</para>
/// <para><b>Blend.</b> The fire-ball atlases are drawn additively (black
/// surround contributes 0); the smoke atlases are drawn with premultiplied
/// alpha (the renderers premultiply the sector-lit tint by the puff alpha). The
/// atlases carry raw RGBA so both blend paths see unpainted-elsewhere = 0.</para>
/// </remarks>
public sealed class VfxAssets : IDisposable
{
    /// <summary>Content-relative directory holding the atlas PNGs.</summary>
    public const string AtlasContentDir = "Content/Vfx/atlas";

    /// <summary>Additive fire-ball glow atlas (Explosion01-light, 5×5).</summary>
    public Texture2D[] FireGlow { get; }

    /// <summary>Solid fire-ball atlas (Explosion01, 5×5).</summary>
    public Texture2D[] FireBall { get; }

    /// <summary>Billowy smoke atlas (Cloud01, 8×8).</summary>
    public Texture2D[] Smoke { get; }

    /// <summary>Wispy smoke atlas (WispySmoke01, 8×8).</summary>
    public Texture2D[] WispySmoke { get; }

    /// <summary>Soft disc smoke atlas (DiscSmoke01, 16×4).</summary>
    public Texture2D[] DiscSmoke { get; }

    /// <summary>A small procedural radial-gradient sprite (used for the flash
    /// and for spark particles) — sharp white centre to transparent edge.</summary>
    public Texture2D SoftDisc { get; }

    /// <summary>Number of 5×5-grid fire-ball frames (also the glow grid).</summary>
    public const int FireGridFrames = 25;

    /// <summary>Number of 8×8-grid billowy-smoke frames.</summary>
    public const int SmokeGridFrames = 64;

    public VfxAssets(GraphicsDevice gd)
    {
        FireGlow = LoadAtlas(gd, AtlasContentDir, "Explosion01-light", 5, 5);
        FireBall = LoadAtlas(gd, AtlasContentDir, "Explosion01", 5, 5);
        // The three SMOKE atlases are radially feathered (P561): the CC0 smoke
        // frames were filled to the cell edge (measured: Cloud01 rim alpha
        // ~0.78 mean, 38% of pixels >0.9) — that hard rim is what read as a
        // "flat card / discrete oval". Feathering scales RGB and alpha by a
        // radial smoothstep so the rim disappears and puffs merge into a soft
        // plume. Fire-ball atlases stay sharp (additive, drawn over the blast).
        Smoke = LoadAtlas(gd, AtlasContentDir, "Cloud01", 8, 8, feather: true);
        WispySmoke = LoadAtlas(gd, AtlasContentDir, "WispySmoke01", 8, 8, feather: true);
        DiscSmoke = LoadAtlas(gd, AtlasContentDir, "DiscSmoke01", 16, 4, feather: true);
        SoftDisc = MakeSoftDisc(gd, 64);
    }

    /// <summary>Slice an atlas PNG (loaded via <see cref="TitleContainer"/>
    /// at <paramref name="contentDir"/>) into its <c>cols</c>×<c>rows</c> grid
    /// frames (row-major, top-left first) as separate square textures. An atlas
    /// that can't be opened/decoded yields zero frames (logged once) and the
    /// caller degrades to whatever layers remain. <paramref name="feather"/>
    /// (P561) applies a radial soft-edge mask to each frame (RGB and alpha
    /// together, so premultiplied blending stays correct) to kill the hard
    /// smoke-card rim.</summary>
    private static Texture2D[] LoadAtlas(GraphicsDevice gd, string contentDir, string stem, int cols, int rows,
        bool feather = false)
    {
        string rel = contentDir + "/" + stem + ".png";
        try
        {
            using var stream = TitleContainer.OpenStream(rel);
            using var img = Texture2D.FromStream(gd, stream);
            int fw = img.Width / cols, fh = img.Height / rows;
            int n = cols * rows;
            var frames = new Texture2D[n];
            var sub = new Color[fw * fh];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    img.GetData(0, new Rectangle(c * fw, r * fh, fw, fh), sub, 0, sub.Length);
                    if (feather)
                        RadialFeather(sub, fw, fh);
                    int i = r * cols + c;
                    frames[i] = new Texture2D(gd, fw, fh);
                    frames[i].SetData(sub);
                }
            }
            Console.WriteLine($"[monogame] vfx atlas {stem}: {n} frames {fw}x{fh}" + (feather ? " (feathered)" : ""));
            return frames;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] vfx atlas '{stem}' unavailable ({rel}): {ex.Message} — skipped");
            return Array.Empty<Texture2D>();
        }
    }

    /// <summary>P561 — multiply each frame pixel's RGB and alpha by a radial
    /// smoothstep mask that is 1 inside ~72% of the cell's inscribed radius and
    /// fades to 0 at the cell edge, so a smoke puff has a soft edge instead of
    /// the hard CC0-frame rim. Scaling RGB with alpha preserves the frame's
    /// colour/alpha ratio (premultiplied-safe).</summary>
    private static void RadialFeather(Color[] data, int w, int h)
    {
        float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f;
        float norm = MathF.Min(cx, cy);   // inset mask radius base (r=1 ≈ half-min-dimension)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = (x - cx) / norm, dy = (y - cy) / norm;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float m = SmoothEdge(r, 0.72f, 1.0f);
                int idx = y * w + x;
                var p = data[idx];
                data[idx] = new Color(
                    (int)(p.R * m), (int)(p.G * m), (int)(p.B * m), (int)(p.A * m));
            }
        }
    }

    /// <summary>Unit smoothstep: 1 at <paramref name="r"/> ≤ <paramref name="inner"/>,
    /// 0 at <paramref name="r"/> ≥ <paramref name="outer"/>, C¹-smooth between.</summary>
    private static float SmoothEdge(float r, float inner, float outer)
    {
        float t = MathHelper.Clamp((r - inner) / (outer - inner), 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    private static Texture2D MakeSoftDisc(GraphicsDevice gd, int size)
    {
        var t = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f, ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                data[y * size + x] = new Color(1f, 1f, 1f, a * a * a);
            }
        t.SetData(data);
        return t;
    }

    public void Dispose()
    {
        foreach (var arr in new[] { FireGlow, FireBall, Smoke, WispySmoke, DiscSmoke })
            foreach (var t in arr) t?.Dispose();
        SoftDisc.Dispose();
    }
}
