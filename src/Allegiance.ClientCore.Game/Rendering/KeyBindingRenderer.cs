using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 - renders the in-game rebind popup (native's InputMapPopup,
/// WinTrek/trekinput.cpp:1631+, cleaned/native-styled: the command list with
/// each command's current key, a selection cursor, a "press a new key" capture
/// banner, a conflict warning with a swap offer, and a reset-to-defaults
/// prompt). The popup reads the LIVE <see cref="ActionMap"/>, so every change
/// made here is immediately reflected in the help overlay.
///
/// Rendering conventions mirror <see cref="HelpOverlayRenderer"/> (own
/// SpriteBatch session, translucent panel + teal border, ASCII-only strings).
/// </summary>
public sealed class KeyBindingRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly KeyBindingController _ctl;

    private static readonly Color PanelBg = new(0.012f, 0.028f, 0.038f, 0.96f);
    private static readonly Color PanelBorder = new(0.16f, 0.42f, 0.40f);
    private static readonly Color HeaderColor = new(0.42f, 0.94f, 0.88f);
    private static readonly Color GroupTitleColor = new(0.50f, 0.90f, 0.78f);
    private static readonly Color KeyColor = new(1.00f, 0.90f, 0.55f);
    private static readonly Color DescColor = new(0.85f, 0.93f, 0.92f);
    private static readonly Color SelBg = new(0.06f, 0.30f, 0.28f, 0.55f);
    private static readonly Color WarnColor = new(1.00f, 0.62f, 0.30f);
    private static readonly Color NoteColor = new(0.58f, 0.74f, 0.72f);

    public const float RowHeight = 20f;
    private const float Padding = 18f;

    public KeyBindingRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, KeyBindingController ctl)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _ctl = ctl;
    }

    /// <summary>Draw the rebind popup for the current viewport.</summary>
    public void Draw(Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;
        float panelW = w * 0.72f;
        float panelH = h * 0.90f;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        float innerX = panelX + Padding;
        float innerTop = panelY + 52f;
        float innerBottom = panelY + panelH - 46f;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);

            // Header: title + contextual instruction.
            string title = PhaseHeader();
            var hs = _small.MeasureString(title);
            Draw(title, new Vector2((w - hs.X) / 2f, panelY + 10f), HeaderColor, 1f);

            // -- scrollable command list ------------------------------------
            // Precompute display rows: a category header row per category change,
            // then one row per action. This keeps header/scroll accounting exact.
            var rows = new System.Collections.Generic.List<(bool Header, string? Action)>();
            string? lastCategory = null;
            foreach (var action in _ctl.Actions)
            {
                string cat = _ctl.DefinitionOf(action).Category;
                if (cat != lastCategory) { rows.Add((true, cat)); lastCategory = cat; }
                rows.Add((false, action));
            }
            int selRow = -1;
            for (int i = 0; i < rows.Count; i++)
                if (!rows[i].Header && rows[i].Action == _ctl.CurrentAction) { selRow = i; break; }

            int visRows = Math.Max(1, (int)MathF.Floor((innerBottom - innerTop) / RowHeight));
            int firstRow = Math.Max(0, selRow - visRows / 2);
            for (int i = firstRow; i < Math.Min(firstRow + visRows, rows.Count); i++)
            {
                float ry = innerTop + (i - firstRow) * RowHeight;
                if (rows[i].Header)
                {
                    Draw(rows[i].Action!, new Vector2(innerX, ry), GroupTitleColor, 1f);
                    continue;
                }
                string action = rows[i].Action!;
                if (i == selRow)
                    FillRect(innerX - 4f, ry, panelW - 2f * Padding + 8f, RowHeight - 2f, SelBg);
                var kb = _ctl.Map.BindingOf(action);
                Draw(action.Replace("TK_", ""), new Vector2(innerX, ry), DescColor, 1f);
                var kl = _small.MeasureString(kb.Label);
                Draw(kb.Label, new Vector2(innerX + 330f, ry), KeyColor, 1f);
                Draw(_ctl.DefinitionOf(action).Description,
                    new Vector2(innerX + 330f + kl.X + 16f, ry), NoteColor, 0.82f);
            }

            // Footer: phase message + nav hint.
            DrawFooter(panelX, panelY, panelH, w);
        }
        finally
        {
            _sb.End();
        }
    }

    private string PhaseHeader() => _ctl.Phase switch
    {
        KeyBindPhase.Browse =>
            "CONTROLS - pick a command, press Enter to rebind (Esc closes)",
        KeyBindPhase.Capture =>
            $"REBIND {_ctl.CapturingAction} - press the new key (Esc cancels)",
        KeyBindPhase.Conflict => "CONFLICT - that key is taken",
        KeyBindPhase.ConfirmReset => "RESET KEY BINDINGS",
        _ => "CONTROLS",
    };

    private void DrawFooter(float panelX, float panelY, float panelH, float w)
    {
        string msg = _ctl.Message;
        float my = panelY + panelH - 20f;
        var sz = _small.MeasureString(msg);
        // Centre the message; color it amber when it is a warning/prompt.
        bool warn = _ctl.Phase is KeyBindPhase.Conflict or KeyBindPhase.ConfirmReset;
        Draw(msg, new Vector2((w - sz.X) / 2f, my), warn ? WarnColor : NoteColor, 1f);

        string hint = "Up/Down = move   Enter = pick   R = reset to defaults   Esc = back/close";
        var hz = _small.MeasureString(hint);
        Draw(hint, new Vector2((w - hz.X) / 2f, panelY + panelH - 40f), NoteColor, 0.9f);
    }

    private void Draw(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
