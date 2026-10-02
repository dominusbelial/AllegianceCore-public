using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The real original-game explosion artwork, decoded from the <c>.mdl</c>
/// texture strips at runtime and sliced into the square animation frames the
/// <see cref="ExplosionRenderer"/> draws. Mirrors the port's established
/// runtime-asset convention (see <see cref="MissileModelSource.ResolveArtworkDir"/>
/// / <see cref="GaugeTextureSource"/>): real <c>.mdl</c> files are read straight
/// from the <c>ALLEGIANCE_ARTWORK_PATH</c> tree, not compiled into Content.
/// </summary>
/// <remarks>
/// <para><b>Which files.</b> Native loads <c>exp20..exp25bmp.mdl</c> for the
/// fireball sprite films and <c>fx18bmp.mdl</c> for the shock wave
/// (<c>artwork.h:198-203</c>, <c>trekigc.cpp:2530-2560</c>). All are RGB565
/// (bitcount=16) so <see cref="MdlTextureDecoder"/> decodes them.</para>
/// <para><b>Slicing.</b> Each film strip is one wide image; native's
/// <c>AnimatedImage(Number*, Surface*, nRows=0, nCols=0)</c> splits it into
/// <c>width/height</c> square frames (<c>engine/image.cpp:588-625</c>). This
/// loader does the same slice, so frame <c>i</c> here is exactly native frame
/// <c>i</c>. The presets declare each film's <see cref="Allegiance.ClientCore.State.ExplosionSpriteFilm.FrameCount"/>;
/// a MonoGame test asserts the declared count equals what the real asset yields.</para>
/// <para><b>Fully opaque, drawn additively.</b> RGB565 has no alpha, so every
/// pixel is uploaded with alpha 255. The renderer uses additive blending
/// (SrcAlpha, One), under which the sprite's black surround contributes zero
/// and only the glowing fireball pixels add — the same reason native renders
/// these as additive decals.</para>
/// </remarks>
public sealed class ExplosionAssets
{
    private readonly Dictionary<string, Texture2D[]> _films = [];
    private Texture2D? _shockwave;

    /// <summary>The decoded fireball frames per strip stem (e.g. "exp25" →
    /// its 32 square frames), in film order. Empty/absent stems are missing
    /// files (a failed decode degrades to "that film just doesn't render").</summary>
    public IReadOnlyDictionary<string, Texture2D[]> Films => _films;

    /// <summary>The decoded shock-wave ring (fx18bmp), or null if it failed to
    /// load (categories then render fireballs only).</summary>
    public Texture2D? Shockwave => _shockwave;

    /// <summary>The single shared artwork-root resolver (same convention as the
    /// gauge/missile loaders) — see <see cref="AssetSources.ResolveRoot"/>.</summary>
    public static string ResolveArtworkDir() => AssetSources.ResolveRoot();

    /// <summary>Frame <paramref name="index"/> of a strip <paramref name="stem"/>,
    /// or null if that stem/index isn't loaded (missing file or bad index).</summary>
    public Texture2D? Frame(string stem, int index) =>
        _films.TryGetValue(stem, out var arr) && index >= 0 && index < arr.Length ? arr[index] : null;

    /// <summary>Back-compat convenience: load from an on-disk artwork directory
    /// (routes through <see cref="DirectoryAssetSource"/>).</summary>
    public static ExplosionAssets Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>Load all six explosion strips + the shock wave from
    /// <paramref name="assets"/>. Missing/undecodable files are skipped with
    /// a logged warning (graceful degradation — the renderer simply lacks that
    /// film), mirroring the GaugeTextures convention.</summary>
    public static ExplosionAssets Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new ExplosionAssets();

        foreach (string stem in new[] { "exp20", "exp22", "exp23", "exp24", "exp25" })
        {
            string rel = stem + "bmp.mdl";
            try
            {
                if (!assets.Exists(rel))
                {
                    Console.WriteLine($"[monogame] explosion film '{stem}' missing ({rel}) — skipped");
                    continue;
                }
                var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, rel));
                result._films[stem] = SliceStrip(gd, tex);
                Console.WriteLine($"[monogame] explosion film '{stem}' = {result._films[stem].Length} frames " +
                                  $"{tex.Width}x{tex.Height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] explosion film '{stem}' failed to decode: {ex.Message} — skipped");
            }
        }

        try
        {
            const string shockRel = "fx18bmp.mdl";
            if (assets.Exists(shockRel))
            {
                var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, shockRel));
                result._shockwave = ToTexture(gd, tex, 0, 0, tex.Width, tex.Height);
                Console.WriteLine($"[monogame] explosion shockwave fx18 = {tex.Width}x{tex.Height}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] explosion shockwave fx18 failed to decode: {ex.Message} — skipped");
        }

        return result;
    }

    /// <summary>Split one decoded strip into its square frames (native
    /// AnimatedImage slicing — cell size = the strip height).</summary>
    private static Texture2D[] SliceStrip(GraphicsDevice gd, MdlTexture tex)
    {
        int cell = tex.Height;                       // square frame side = strip height
        int cells = tex.Width / cell;                // width/height frames
        var frames = new Texture2D[cells];
        for (int c = 0; c < cells; c++)
            frames[c] = ToTexture(gd, tex, c * cell, 0, cell, cell);
        return frames;
    }

    /// <summary>Upload a rectangular region of a decoded <c>.mdl</c> texture as
    /// an opaque RGBA <see cref="Texture2D"/> (alpha 255 — additive rendering
    /// makes the black surround vanish).</summary>
    private static Texture2D ToTexture(GraphicsDevice gd, MdlTexture tex, int ox, int oy, int w, int h)
    {
        var surface = new Texture2D(gd, w, h);
        var colors = new Color[w * h];
        int src = tex.Width;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = ((oy + y) * src + (ox + x)) * 4;
                colors[y * w + x] = new Color(
                    (int)tex.Rgba[si], (int)tex.Rgba[si + 1], (int)tex.Rgba[si + 2], 255);
            }
        }
        surface.SetData(colors);
        return surface;
    }

    public void Dispose()
    {
        foreach (var arr in _films.Values)
            foreach (var t in arr) t?.Dispose();
        _shockwave?.Dispose();
    }
}
