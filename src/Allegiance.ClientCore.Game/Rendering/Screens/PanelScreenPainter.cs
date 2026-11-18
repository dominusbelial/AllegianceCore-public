using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Screens;

/// <summary>
/// P1042c — the Classic/Modern painter for the panel-style in-game screens (side select,
/// ready check, mission briefing, team pane, quick chat, key bindings, connection lost). It
/// draws only what the frame carries: every fill in the builder's order, then every text run
/// at its resolved anchor with the exact pre-boundary colour and scale. It owns no geometry
/// and no model, so a decoded-pixel compare against the pre-boundary renderers is zero by
/// construction and a skin can be swapped without touching the drawing.
/// </summary>
public sealed class PanelScreenPainter
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    public PanelScreenPainter(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the frame in the renderers' own self-contained SpriteBatch session
    /// (the pre-boundary overlays each opened one).</summary>
    public void Draw(in ScreenPanelFrame frame)
    {
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try { DrawInside(frame); }
        finally { _sb.End(); }
    }

    /// <summary>Paint into an already-open session (used by the proof tool's render target).</summary>
    public void DrawInside(in ScreenPanelFrame frame)
    {
        var fills = frame.Fills;
        for (int i = 0; i < fills.Count; i++)
        {
            var fill = fills[i];
            FillRect(fill.Rect.X, fill.Rect.Y, fill.Rect.Width, fill.Rect.Height, fill.Color);
        }

        var rows = frame.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Text.Length == 0) continue;
            _sb.DrawString(row.Large ? _big : _small, row.Text, row.Position, row.ClassicInk, 0f,
                Vector2.Zero, row.Scale, SpriteEffects.None, 0f);
        }

        if (frame.Art is { } art)
            _sb.Draw(art, new Vector2(frame.ArtBox.X, frame.ArtBox.Y), null, Color.White, 0f,
                Vector2.Zero, frame.ArtScale, SpriteEffects.None, 0f);
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);
}
