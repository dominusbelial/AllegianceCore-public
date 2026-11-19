using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Regression: F12 on the default black sky crashed the desktop client —
/// the non-HD credits text contains em dashes the small font has no glyph for, and
/// SpriteBatch.DrawString throws on any unresolvable character.</summary>
public sealed class CreditsOverlayFontTests
{
    // Printable ASCII only, no default character: the desktop small font's shape.
    private static SpriteFont AsciiFont(char? defaultCharacter = null)
    {
        var chars = Enumerable.Range(32, 95).Select(c => (char)c).ToList();
        return new SpriteFont(null!, chars.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(),
            chars.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(), chars,
            16, 0f, chars.Select(_ => new Vector3(0, 8, 0)).ToList(), defaultCharacter);
    }

    [Theory]
    [InlineData("Body")]
    [InlineData("NonHdBody")]
    public void Every_credits_line_is_drawable_by_an_ascii_font(string field)
    {
        var font = AsciiFont();
        var lines = (string[])typeof(CreditsOverlayRenderer)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            var safe = CreditsOverlayRenderer.FontSafe(font, line);
            Assert.All(safe, ch => Assert.Contains(ch, font.Characters));
            _ = font.MeasureString(safe); // throws on any glyph the font cannot resolve
        }
    }

    [Fact]
    public void The_shipped_non_hd_text_really_contains_the_crashing_dash()
    {
        var lines = (string[])typeof(CreditsOverlayRenderer)
            .GetField("NonHdBody", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var font = AsciiFont();
        Assert.Contains(lines, line => line.Contains('—'));
        Assert.Throws<ArgumentException>(() => font.MeasureString(lines.First(l => l.Contains('—'))));
    }

    [Fact]
    public void Dashes_become_hyphens_other_missing_glyphs_the_default_or_question_mark()
    {
        Assert.Equal("a - b - c - d", CreditsOverlayRenderer.FontSafe(AsciiFont(), "a — b – c − d"));
        Assert.Equal("caf? ok", CreditsOverlayRenderer.FontSafe(AsciiFont(), "café ok"));
        Assert.Equal("caf* ok", CreditsOverlayRenderer.FontSafe(AsciiFont('*'), "café ok"));
        const string plain = "already safe";
        Assert.Same(plain, CreditsOverlayRenderer.FontSafe(AsciiFont(), plain));
    }
}
