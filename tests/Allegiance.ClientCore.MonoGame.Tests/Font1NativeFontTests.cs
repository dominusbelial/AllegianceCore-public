using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// FONT-1 — native HUD fonts (bundled DejaVu Sans / DejaVu Sans Bold at the
/// native pixel sizes). Guards: (a) the registry maps every classic-HUD native
/// font identity to a (face, px, bold) pair with both density assets built; the
/// density selector; (a2) every id's 1x/@2x .spritefont face, Size, Style and
/// double-density Size match a font.mdl-derived expected table; (b) no
/// classic-HUD text site in HudRenderer/ChatRenderer passes the mono small/big
/// fonts to a draw/measure call (exact-line whitelist); (c) every .spritefont
/// points at a bundled .ttf that exists, and nothing names Verdana; (d) a
/// behaviour test of Modern's HudFont override (its own small/big fonts for
/// every id at its own TextScale, no GraphicsDevice); (e) the DejaVu licence
/// sits next to the bundled .ttfs.
/// </summary>
public class Font1NativeFontTests
{
    private static string Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] rel) => File.ReadAllText(Path.Combine(Root(), Path.Combine(rel)));

    // ── (a) registry mapping + built assets ───────────────────────────────
    [Fact]
    public void Every_native_hud_font_maps_to_a_face_px_bold_pair_with_both_density_assets_built()
    {
        string mgcb = Read("src", "Allegiance.ClientCore.MonoGame", "Content", "Content.mgcb");
        foreach (var id in Enum.GetValues<NativeFont>())
        {
            var spec = NativeFontCatalog.Spec(id);
            Assert.InRange(spec.EmPx, 4f, 64f);
            Assert.Equal(spec.Bold ? "DejaVu Sans Bold" : "DejaVu Sans", spec.Face);
            Assert.True(spec.NativeCellHeight > 0f);
            Assert.Equal(spec.EmPx, NativeFontCatalog.Size[id] * 96f / 72f, 3);   // MonoGame Size is points
            foreach (var asset in new[] { spec.Asset1x, spec.Asset2x })
            {
                string rel = Path.Combine("src", "Allegiance.ClientCore.MonoGame", "Content", asset + ".spritefont");
                Assert.True(File.Exists(Path.Combine(Root(), rel)), $"missing {rel}");
                Assert.Contains("/build:" + asset + ".spritefont", mgcb);
            }
        }
    }

    [Fact]
    public void Exactly_seven_unique_face_size_bakes_cover_the_classic_hud()
    {
        var stems = new HashSet<string>();
        foreach (var id in Enum.GetValues<NativeFont>())
            stems.Add(NativeFontCatalog.Spec(id).Asset1x);
        var expected = new[] { "Fonts/NativeR6", "Fonts/NativeR8", "Fonts/NativeR9",
            "Fonts/NativeB7", "Fonts/NativeB8", "Fonts/NativeB9", "Fonts/NativeB10" };
        Assert.Equal(expected.OrderBy(x => x), stems.OrderBy(x => x));
    }

    [Fact]
    public void Density_selector_uses_one_x_up_to_threshold_and_two_x_above_with_residual()
    {
        var s1 = NativeFonts.Select(1f);     Assert.False(s1.UseTwoX); Assert.Equal(1f, s1.Residual);
        var s2 = NativeFonts.Select(1.25f);  Assert.False(s2.UseTwoX); Assert.Equal(1.25f, s2.Residual);
        var s3 = NativeFonts.Select(0.5f);   Assert.False(s3.UseTwoX); Assert.Equal(1f, s3.Residual);   // floored at 1
        var s4 = NativeFonts.Select(1.26f);  Assert.True(s4.UseTwoX);  Assert.Equal(0.63f, s4.Residual, 3);
        var s5 = NativeFonts.Select(2f);     Assert.True(s5.UseTwoX);  Assert.Equal(1f, s5.Residual);
        var s6 = NativeFonts.Select(2.118f); Assert.True(s6.UseTwoX);  Assert.Equal(1.059f, s6.Residual, 3);
    }

    // ── (a2) per-id spritefont contents (font.mdl is the expected table) ──
    /// <summary>What every native identity must bake, measured from
    /// <c>~/Development/Artwork/font.mdl</c>: each <c>ImportFont</c>'s header is
    /// DWORD height, DWORD width, DWORD dataSize followed by
    /// <c>CharData m_data[256]</c> (12 bytes: WinPoint m_size + int m_offset,
    /// <c>engine/font.cpp:29-50</c>), read in symbol order by
    /// <c>engine/font.cpp:353-366</c>; the cell height below is that header's
    /// <c>m_size.y</c>. The Size/em columns are FONT-1's measured fit (MonoGame
    /// &lt;Size&gt; is points; em = Size*96/72) with FONT-1b's medBoldVerdana
    /// change to Size 9 (measured ASCII width error -62 at 8 vs +16 at 9; its
    /// native cell is 13 px, promptFont's is 12, so they must not share a bake).</summary>
    private static readonly (NativeFont Id, string Symbol, int Size, bool Bold, float NativeCell)[] FontMdl =
    [
        (NativeFont.SmVerdana,      "smVerdana",      6, false, 10f),
        (NativeFont.MedVerdana,     "medVerdana",     8, false, 12f),
        (NativeFont.MedBoldVerdana, "medBoldVerdana", 9, true,  13f),
        (NativeFont.LgBoldVerdana,  "lgBoldVerdana", 10, true,  16f),
        (NativeFont.SmallFont,      "smallFont",      8, false, 12f),
        (NativeFont.SmallBoldFont,  "smallBoldFont",  7, true,  12f),
        (NativeFont.LargeFont,      "largeFont",      9, false, 14f),
        (NativeFont.LargeBoldFont,  "largeBoldFont",  9, true,  14f),
        (NativeFont.PromptFont,     "promptFont",     8, true,  12f),
    ];

    [Fact]
    public void Every_native_id_bakes_the_expected_face_size_and_style_at_one_and_two_x()
    {
        Assert.Equal(Enum.GetValues<NativeFont>().OrderBy(v => v), FontMdl.Select(r => r.Id).OrderBy(v => v));
        var sizeRe = new Regex(@"<Size>\s*(\d+(?:\.\d+)?)\s*</Size>");
        var styleRe = new Regex(@"<Style>\s*(\w+)\s*</Style>");
        var nameRe = new Regex(@"<FontName>\s*(.*?)\s*</FontName>");
        foreach (var (id, symbol, expectedSize, expectedBold, expectedCell) in FontMdl)
        {
            // The registry must agree with the font.mdl-derived table.
            Assert.True(NativeFontCatalog.Size[id] == expectedSize, $"{symbol}: Size {NativeFontCatalog.Size[id]} != {expectedSize}");
            Assert.True(NativeFontCatalog.Spec(id).Bold == expectedBold, $"{symbol}: bold mismatch");
            Assert.True(NativeFontCatalog.NativeCellHeight[id] == expectedCell,
                $"{symbol}: cell {NativeFontCatalog.NativeCellHeight[id]} != font.mdl {expectedCell}");

            var spec = NativeFontCatalog.Spec(id);
            string ttf = expectedBold ? "DejaVuSans-Bold.ttf" : "DejaVuSans.ttf";
            string style = expectedBold ? "Bold" : "Regular";
            foreach (var (asset, multiply) in new[] { (spec.Asset1x, 1), (spec.Asset2x, 2) })
            {
                string rel = Path.Combine("src", "Allegiance.ClientCore.MonoGame", "Content", asset + ".spritefont");
                string text = Read(rel);
                Assert.Equal(ttf, nameRe.Match(text).Groups[1].Value);
                Assert.Equal(style, styleRe.Match(text).Groups[1].Value);
                Assert.Equal(expectedSize * multiply, int.Parse(sizeRe.Match(text).Groups[1].Value));
            }
        }
    }

    // ── (b) source guard: classic sites never pass the mono fonts ─────────
    // FONT-1b — an exact-line whitelist instead of "any line containing
    // '_big : _small'": only the HudRenderer.HudFont FALLBACK may combine the two
    // mono fonts (renderer tests that supply no NativeFonts); every other use in
    // the classic files would be a leaked mono font at a native-font site.
    private static readonly HashSet<string> AllowedMonoFontLines = new(StringComparer.Ordinal)
    {
        "protected readonly SpriteFont _small;",
        "protected readonly SpriteFont _big;",
        "_small = small;",
        "_big = big;",
        "return (id == NativeFont.LgBoldVerdana ? _big : _small, TextScale);",   // HudFont fallback only
    };

    [Fact]
    public void Classic_hud_sites_never_pass_the_mono_small_big_fonts_to_a_draw_or_measure()
    {
        foreach (var file in new[] { "HudRenderer.cs", "ChatRenderer.cs" })
        {
            string src = Read("src", "Allegiance.ClientCore.Game", "Rendering", file);
            foreach (var line in src.Split('\n'))
            {
                if (!line.Contains("_small") && !line.Contains("_big")) continue;
                Assert.True(AllowedMonoFontLines.Contains(line.Trim()),
                    $"{file}: mono font leaked into draw/measure: {line.Trim()}");
            }
        }
    }

    // ── (c) spritefont guard + no Verdana ─────────────────────────────────
    [Fact]
    public void Every_spritefont_points_at_a_bundled_ttf_that_exists_relative_to_it()
    {
        string contentRoot = Path.Combine(Root(), "src", "Allegiance.ClientCore.MonoGame", "Content");
        var re = new Regex(@"<FontName>(.*?)</FontName>");
        foreach (var sf in Directory.EnumerateFiles(contentRoot, "*.spritefont", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(sf);
            var m = re.Match(text);
            Assert.True(m.Success, $"no <FontName> in {sf}");
            string fontName = m.Groups[1].Value;
            Assert.EndsWith(".ttf", fontName);
            string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sf)!, fontName));
            Assert.True(File.Exists(full), $"{fontName} referenced by {sf} does not exist");
        }
    }

    [Fact]
    public void Nothing_in_spritefonts_mgcb_or_csproj_names_Verdana()
    {
        string dir = Path.Combine(Root(), "src", "Allegiance.ClientCore.MonoGame");
        foreach (var ext in new[] { "*.spritefont", "*.mgcb", "*.csproj" })
            foreach (var f in Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories))
                Assert.DoesNotContain("verdana", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase);
    }

    // ── (d) Modern unchanged — behaviour, not source text ─────────────────
    /// <summary>FONT-1b — call ModernHudRenderer's real HudFont override on an
    /// uninitialised instance whose _small/_big/_scale are populated by
    /// reflection, so no GraphicsDevice is needed. Every native identity must
    /// resolve to Modern's own small font except lgBoldVerdana (the big one), at
    /// Modern's own TextScale — never at the pane scale the caller passes.</summary>
    [Fact]
    public void Modern_overrides_HudFont_to_return_its_own_small_big_for_every_id()
    {
        var small = (SpriteFont)RuntimeHelpers.GetUninitializedObject(typeof(SpriteFont));
        var big = (SpriteFont)RuntimeHelpers.GetUninitializedObject(typeof(SpriteFont));
        var modern = (ModernHudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ModernHudRenderer));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(HudRenderer).GetField("_small", flags)!.SetValue(modern, small);
        typeof(HudRenderer).GetField("_big", flags)!.SetValue(modern, big);
        typeof(ModernHudRenderer).GetField("_scale", flags)!.SetValue(modern, 1.734f);
        var hudFont = typeof(ModernHudRenderer).GetMethod("HudFont", flags)
            ?? throw new MissingMethodException("ModernHudRenderer.HudFont");

        foreach (var id in Enum.GetValues<NativeFont>())
        {
            // The pane scale must be ignored: Modern keeps its own TextScale.
            var (font, scale) = ((SpriteFont Font, float Scale))hudFont.Invoke(modern, [id, 4.25f])!;
            Assert.True(ReferenceEquals(id == NativeFont.LgBoldVerdana ? big : small, font),
                $"{id} did not resolve to Modern's own {(id == NativeFont.LgBoldVerdana ? "big" : "small")} font");
            Assert.Equal(1.734f, scale);
        }
    }

    // ── (e) licence + bundled ttfs ────────────────────────────────────────
    [Fact]
    public void Licence_file_sits_next_to_the_bundled_ttfs()
    {
        string dir = Path.Combine(Root(), "src", "Allegiance.ClientCore.MonoGame", "Content", "Fonts");
        Assert.True(File.Exists(Path.Combine(dir, "LICENSE-DejaVu.txt")), "LICENSE-DejaVu.txt missing");
        foreach (var ttf in new[] { "DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSansMono.ttf" })
            Assert.True(File.Exists(Path.Combine(dir, ttf)), $"{ttf} missing");
    }
}
