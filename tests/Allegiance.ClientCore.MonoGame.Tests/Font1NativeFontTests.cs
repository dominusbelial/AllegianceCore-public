using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// FONT-1 — native HUD fonts (bundled DejaVu Sans / DejaVu Sans Bold at the
/// native pixel sizes). Guards: (a) the registry maps every classic-HUD native
/// font identity to a (face, px, bold) pair with both density assets built; the
/// density selector; (b) no classic-HUD text site in HudRenderer/ChatRenderer
/// passes the mono small/big fonts to a draw/measure call; (c) every
/// .spritefont points at a bundled .ttf that exists, and nothing names Verdana;
/// (d) Modern's HudFont override returns its own small/big fonts for every id;
/// (e) the DejaVu licence sits next to the bundled .ttfs.
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

    // ── (b) source guard: classic sites never pass the mono fonts ─────────
    [Fact]
    public void Classic_hud_sites_never_pass_the_mono_small_big_fonts_to_a_draw_or_measure()
    {
        foreach (var file in new[] { "HudRenderer.cs", "ChatRenderer.cs" })
        {
            string src = Read("src", "Allegiance.ClientCore.Game", "Rendering", file);
            foreach (var line in src.Split('\n'))
            {
                if (!line.Contains("_small") && !line.Contains("_big")) continue;
                bool allowed =
                    line.Contains("readonly SpriteFont _small") || line.Contains("readonly SpriteFont _big")
                    || line.Contains("_small = small") || line.Contains("_big = big")
                    || line.Contains("_big : _small") || line.Contains("_small : _big");   // HudFont fallback
                Assert.True(allowed, $"{file}: mono font leaked into draw/measure: {line.Trim()}");
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

    // ── (d) Modern unchanged ──────────────────────────────────────────────
    [Fact]
    public void Modern_overrides_HudFont_to_return_its_own_small_big_for_every_id()
    {
        string src = Read("src", "Allegiance.ClientCore.Game", "Rendering", "ModernHudRenderer.cs");
        Assert.Contains("HudFont(NativeFont id, float paneScale)", src);
        Assert.Contains("id == NativeFont.LgBoldVerdana ? _big : _small", src);
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
