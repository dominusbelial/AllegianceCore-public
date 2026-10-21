using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The native classic-HUD font identities. These mirror the names in
/// the original's pre-rasterised bitmap fonts (<c>~/Development/Artwork/font.mdl</c>,
/// 14 <c>ImportFont</c> objects: smallFont smallBoldFont largeFont largeBoldFont
/// hugeFont hugeBoldFont smVerdana medVerdana medBoldVerdana lgBoldVerdana
/// smTahoma titleFont promptFont smEuro; <c>engine/font.cpp:353-366</c> reads each
/// via <c>FontImpl(IBinaryReaderSite*)</c>: DWORD height, DWORD width, DWORD
/// dataSize, then <c>CharData m_data[256]</c> (12 bytes each: <c>WinPoint m_size</c>
/// + <c>int m_offset</c>, <c>engine/font.cpp:29-50</c>), then the glyph bits).
/// FONT-1 maps each HUD font to a bundled DejaVu Sans / DejaVu Sans Bold bake at
/// the native pixel size (Verdana is not redistributable; DejaVu is the Bitstream
/// Vera lineage). The face/size fit below is derived from parsing <c>font.mdl</c>:
/// the cell height is each header's <c>m_size.y</c> and the DejaVu size is the one
/// whose ASCII 32-126 advance-width sum is closest to the native width table.</summary>
public enum NativeFont
{
    SmVerdana,
    MedVerdana,
    MedBoldVerdana,
    LgBoldVerdana,
    SmallFont,
    SmallBoldFont,
    LargeFont,
    LargeBoldFont,
    PromptFont,
}

/// <summary>A concrete (face, pixel-size) bake pair with the native cell height
/// it substitutes and the two density assets built for it.</summary>
public readonly record struct NativeFontSpec(
    string Face, float EmPx, bool Bold, float NativeCellHeight, string Asset1x, string Asset2x);

/// <summary>The pure native-name → (face, px, bold) mapping plus the built asset
/// names. Static and GraphicsDevice-free so it is testable without a device.
/// Sizing notes (FONT-1, measured from font.mdl and verified on this MonoGame
/// 3.8.5.1 pipeline): MonoGame &lt;Size&gt; is in POINTS; the baked em is
/// Size·96/72 = Size·1.3333 (empirically verified — LineSpacing + glyph advances
/// of Size N match FreeType at em N·1.3333). Integer sizes give em 8.0/9.33/10.67/
/// 12.0/13.33, so each native font is assigned the integer Size whose achieved em
/// best fits its font.mdl width table:</summary>
public static class NativeFontCatalog
{
    /// <summary>The monoGame <c>&lt;Size&gt;</c> (points) used for each pair —
    /// the concrete value in the .spritefont, kept alongside the achieved em.</summary>
    public static readonly IReadOnlyDictionary<NativeFont, int> Size =
        new Dictionary<NativeFont, int>
        {
            [NativeFont.SmVerdana] = 6,        // Regular, em 8.00
            [NativeFont.MedVerdana] = 8,       // Regular, em 10.67
            [NativeFont.SmallFont] = 8,        // Regular, em 10.67 (shares with medVerdana)
            [NativeFont.LargeFont] = 9,        // Regular, em 12.00
            [NativeFont.SmallBoldFont] = 7,    // Bold,    em 9.33
            [NativeFont.MedBoldVerdana] = 9,   // Bold,    em 12.00 (measured width fit, see FONT-1b)
            [NativeFont.PromptFont] = 8,       // Bold,    em 10.67
            [NativeFont.LargeBoldFont] = 9,    // Bold,    em 12.00
            [NativeFont.LgBoldVerdana] = 10,   // Bold,    em 13.33
        };

    /// <summary>The native cell height (px) measured from each font.mdl header.</summary>
    public static readonly IReadOnlyDictionary<NativeFont, float> NativeCellHeight =
        new Dictionary<NativeFont, float>
        {
            [NativeFont.SmVerdana] = 10f,
            [NativeFont.MedVerdana] = 12f,
            [NativeFont.MedBoldVerdana] = 13f,
            [NativeFont.LgBoldVerdana] = 16f,
            [NativeFont.SmallFont] = 12f,
            [NativeFont.SmallBoldFont] = 12f,
            [NativeFont.LargeFont] = 14f,
            [NativeFont.LargeBoldFont] = 14f,
            [NativeFont.PromptFont] = 12f,
        };

    private static bool IsBold(NativeFont id) =>
        id is NativeFont.MedBoldVerdana or NativeFont.LgBoldVerdana
            or NativeFont.SmallBoldFont or NativeFont.LargeBoldFont or NativeFont.PromptFont;

    /// <summary>The achieved em (px) for a pair: Size·96/72.</summary>
    public static float EmPx(NativeFont id) => Size[id] * 96f / 72f;

    /// <summary>The (face, px, bold) spec plus the two density asset base names.</summary>
    public static NativeFontSpec Spec(NativeFont id)
    {
        bool bold = IsBold(id);
        string stem = (bold ? "NativeB" : "NativeR") + Size[id];
        float em = EmPx(id);
        return new NativeFontSpec(
            bold ? "DejaVu Sans Bold" : "DejaVu Sans",
            em,
            bold,
            NativeCellHeight[id],
            $"Fonts/{stem}",
            $"Fonts/{stem}@2x");
    }
}

/// <summary>The loaded classic-HUD font bakes. Each native identity maps to a
/// (face, em) pair baked at 1× and 2× (see <see cref="NativeFontCatalog"/>).
/// <see cref="Get"/> picks the 1× bake while <paramref name="drawScale"/> ≤
/// <see cref="HighDensityThreshold"/>, else the 2× bake, returning the residual
/// scale so glyphs are only ever slightly scaled (never upsampled from a small
/// atlas). Construct once in the lifecycle next to the existing font loads.</summary>
public sealed class NativeFonts
{
    /// <summary>Below ~1.25 the 1× bake is crisp enough; above it the 2× bake
    /// (exactly double) keeps the glyphs sharp with only a ≤2× residual scale.</summary>
    public const float HighDensityThreshold = 1.25f;

    private readonly IReadOnlyDictionary<NativeFont, (SpriteFont One, SpriteFont Two)> _fonts;

    public NativeFonts(IReadOnlyDictionary<NativeFont, (SpriteFont One, SpriteFont Two)> fonts)
        => _fonts = fonts;

    /// <summary>The pure bake-density decision: which density to use and the
    /// residual draw scale, given the pane's position scale (floored at 1).</summary>
    public static (bool UseTwoX, float Residual) Select(float drawScale)
    {
        float d = MathF.Max(1f, drawScale);
        return d <= HighDensityThreshold ? (false, d) : (true, d / 2f);
    }

    /// <summary>Return the bake and residual draw scale for a native identity at
    /// the given on-screen pane scale (floored at 1 — a 0.72-pane font would be a
    /// sub-pixel stub). <paramref name="drawScale"/> is the pane's position scale.</summary>
    public (SpriteFont Font, float Scale) Get(NativeFont id, float drawScale)
    {
        var sel = Select(drawScale);
        var pair = _fonts[id];
        return sel.UseTwoX ? (pair.Two, sel.Residual) : (pair.One, sel.Residual);
    }
}
