using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>SpriteBatch.DrawString and SpriteFont.MeasureString throw on any character
/// the font has no glyph for. Text built in code (credits, skirmish warnings) uses
/// em/en dashes the desktop fonts lack, which crashed the client (F12 credits; the
/// skirmish Grand size warning). Dashes become ASCII '-', any other missing character
/// the font's default character or '?'. Display only.</summary>
internal static class FontText
{
    internal static string Safe(SpriteFont font, string text)
    {
        var glyphs = font.Characters;
        System.Text.StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n' || ch == '\r' || glyphs.Contains(ch)) { sb?.Append(ch); continue; }
            sb ??= new System.Text.StringBuilder(text, 0, i, text.Length);
            sb.Append(ch is '—' or '–' or '−' ? '-' : font.DefaultCharacter ?? '?');
        }
        return sb?.ToString() ?? text;
    }
}
