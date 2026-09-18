using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P257 — draws the H keybinding-reference overlay: a large translucent panel
/// over the current scene listing every current client binding, organised into
/// the logical groups from <see cref="KeyBindingTable"/> (movement, weapons,
/// targeting/autopilot, camera/view, chat, lobby) laid out by
/// <see cref="HelpOverlayLayout"/> into balanced columns.
///
/// Conventions mirror <see cref="StarmapRenderer"/>: it begins/ends its own
/// <see cref="SpriteBatch"/> session (so it can render over the 3D scene or into
/// an offscreen capture), draws a translucent panel + teal border, and keeps
/// every drawn string pure ASCII (the HUD spritefonts bake ASCII 32..126 only).
/// It is drawn last in the frame, so it is fully legible on top of the HUD and
/// starmap; toggling it on/off is independent of every other overlay flag, so
/// the prior view (starmap, HUD, chat log) resumes unchanged when it closes.
///
/// The columns are measured at scale 1; if their combined width would overflow
/// the panel the whole panel's text is scaled DOWN uniformly (never clipped) —
/// a defensive guarantee so a long-lived table or a narrow viewport can never
/// truncate a binding.
/// </summary>
public sealed class HelpOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color PanelBg = new(0.017f, 0.036f, 0.046f, 0.93f);
    private static readonly Color PanelBorder = new(0.16f, 0.42f, 0.40f);
    private static readonly Color HeaderColor = new(0.38f, 0.90f, 0.84f);
    private static readonly Color GroupTitleColor = new(0.44f, 0.92f, 0.86f);
    private static readonly Color KeyColor = new(1.00f, 0.90f, 0.55f);
    private static readonly Color DescColor = new(0.88f, 0.95f, 0.94f);
    private static readonly Color NoteColor = new(0.58f, 0.74f, 0.72f);

    public HelpOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the help panel for the current viewport.</summary>
    public void Draw(Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // A large, near-full-screen reference panel (unlike the starmap's
        // compact pane): the player consults this briefly and closes it.
        float panelW = w * 0.982f;
        float panelH = h * 0.90f;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float padding = HelpOverlayLayout.Padding;

        var groups = KeyBindingTable.All;

        // Measure each group's height and lay them into balanced columns inside
        // the panel's usable vertical band.
        var heights = new float[groups.Count];
        for (int i = 0; i < groups.Count; i++)
            heights[i] = groups[i].Height;

        var slots = HelpOverlayLayout.Layout(heights, panelY + padding,
            panelH - 2f * padding);

        // Column count = max slot column + 1; per-column width = the widest
        // measured line among its groups' titles and key+description cells.
        int columns = 0;
        foreach (var s in slots)
            columns = System.Math.Max(columns, s.Column + 1);

        var colWidths = new float[columns];
        for (int g = 0; g < groups.Count; g++)
        {
            var slot = slots[g];
            float colW = _small.MeasureString(groups[g].Title).X;
            foreach (var e in groups[g].Entries)
            {
                float cell = _small.MeasureString(e.Key).X
                           + 14f                         // key/desc gutter
                           + _small.MeasureString(e.Description).X;
                if (cell > colW) colW = cell;
            }
            if (colW > colWidths[slot.Column]) colWidths[slot.Column] = colW;
        }

        // Defensive uniform text scale: shrink the whole table so it always
        // fits the panel width (avail = panel inner width), never clipping.
        float avail = panelW - 2f * padding;
        float needed = 0f;
        for (int c = 0; c < columns; c++)
            needed += colWidths[c];
        needed += (columns - 1) * HelpOverlayLayout.ColumnGap;
        float scale = needed > avail
            ? System.MathF.Max(0.5f, avail / needed)
            : 1f;
        float scaledGap = HelpOverlayLayout.ColumnGap * scale;

        // Scaled column origins.
        var colX = new float[columns];
        float cx = panelX + padding;
        for (int c = 0; c < columns; c++)
        {
            colX[c] = cx;
            cx += colWidths[c] * scale + scaledGap;
        }

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            // Header line.
            string header = "KEYBINDINGS  (H TOGGLE)";
            var hs = _small.MeasureString(header) * scale;
            Draw(header, new Vector2((w - hs.X) / 2f, panelY + 8f), HeaderColor, scale);

            // Groups.
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var slot = slots[g];
                float x = colX[slot.Column];
                float titleY = slot.Y;

                Draw(group.Title, new Vector2(x, titleY), GroupTitleColor, scale);

                // Aligned key column: max key label width within this group.
                float keyW = 0f;
                foreach (var e in group.Entries)
                {
                    float kw = _small.MeasureString(e.Key).X;
                    if (kw > keyW) keyW = kw;
                }

                float rowY = titleY + HelpOverlayLayout.RowHeight
                                        + HelpOverlayLayout.TitleGap;
                foreach (var e in group.Entries)
                {
                    Draw(e.Key, new Vector2(x, rowY), KeyColor, scale);
                    Draw(e.Description,
                        new Vector2(x + keyW * scale + 14f * scale, rowY),
                        DescColor, scale);
                    rowY += HelpOverlayLayout.RowHeight;
                }
            }

            // Footer note.
            string note =
                "Most keys are disabled while typing chat or in the quick-chat menu.  "
                + "Esc quits (or cancels chat / closes menus); gamepad Back does the same.";
            var nsz = _small.MeasureString(note) * scale;
            Draw(note, new Vector2((w - nsz.X) / 2f, panelY + panelH - 24f),
                NoteColor, scale);
        }
        finally
        {
            _sb.End();
        }
    }

    private void Draw(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)System.MathF.Round(x),
            (int)System.MathF.Round(y), (int)System.MathF.Round(w),
            (int)System.MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
