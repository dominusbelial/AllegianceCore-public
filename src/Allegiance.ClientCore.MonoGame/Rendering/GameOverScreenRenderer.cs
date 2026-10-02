using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P262 — draws the real game-over result screen: a large, centred,
/// high-contrast panel over the frozen scene announcing the winner and the
/// <em>real</em> reason text from the wire, exactly the "why did everything
/// just stop" answer the mission-end previously hid (before this the client
/// only played a sound and set a flag — <c>Game1</c>).
///
/// Text is supplied already-resolved by <see cref="GameOverPanelInfo.From"/>
/// from the live <c>FM_S_GAME_OVER(62)</c> snapshot — never a hardcoded
/// placeholder. Lay out here, derive there.
///
/// Conventions mirror <see cref="HelpOverlayRenderer"/> (the established
/// overlay pattern): it begins/ends its own <see cref="SpriteBatch"/> session
/// (so it renders over the 3D scene or into an offscreen capture), draws a
/// translucent panel + border, and keeps every drawn string pure ASCII (the
/// HUD spritefonts bake ASCII 32..126 only). Unlike the toggleable
/// help/starmap panes it is modal (no dismiss button in scope) and stays up as
/// long as the mission stage is Over.
/// </summary>
public sealed class GameOverScreenRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    // High-contrast palette on the teal HUD-accent family: a very dark backdrop
    // panel edged in bright teal, a gold title banner (gold = the starmap's
    // "you are here" highlight color) so the eye lands on it instantly.
    private static readonly Color PanelBg = new(0.008f, 0.018f, 0.026f, 0.965f);
    private static readonly Color PanelBorder = new(0.24f, 0.72f, 0.66f);
    private static readonly Color TopAccent = new(0.32f, 0.96f, 0.86f);
    private static readonly Color TitleVictory = new(1.00f, 0.90f, 0.34f);
    private static readonly Color TitleDefeat = new(1.00f, 0.58f, 0.48f);
    private static readonly Color TitleDraw = new(0.85f, 0.86f, 0.88f);
    private static readonly Color WinnerColor = new(0.40f, 0.94f, 0.88f);
    private static readonly Color ReasonColor = new(0.94f, 0.97f, 0.96f);
    private static readonly Color SubColor = new(0.60f, 0.76f, 0.74f);

    public GameOverScreenRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the game-over result panel for the current viewport.</summary>
    public void Draw(Viewport viewport, GameOverPanelInfo info)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Large, centred, unmissable panel — but sized to leave the frozen
        // scene visible around it (matching the help/starmap full-ish panel
        // family while keeping the "a game just ended here" framing).
        float panelW = System.MathF.Min(w * 0.86f, 960f);
        float panelH = System.MathF.Min(h * 0.62f, 470f);
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float pad = 34f;

        Color titleColor = info.Outcome switch
        {
            GameOverOutcome.Victory => TitleVictory,
            GameOverOutcome.Defeat  => TitleDefeat,
            _                       => TitleDraw,
        };

        // Title, scaled down uniformly if it would overflow the panel width.
        string title = info.Title;
        var tsz = _big.MeasureString(title);
        float titleScale = System.MathF.Min(1f, (panelW - 2f * pad) / tsz.X);
        float titleX = panelX + (panelW - tsz.X * titleScale) / 2f;

        // Layout vertical stack: title, winner banner, reason block.
        float cursorY = panelY + 26f;
        float bigRow = _big.MeasureString("Mg").Y * titleScale;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder, 2f);
            FillRect(panelX, panelY, panelW, 4f, TopAccent);   // bright top edge

            // Title.
            DrawBig(title, new Vector2(titleX, cursorY), titleColor, titleScale);
            cursorY += bigRow + 22f;

            // Winner banner (empty on a draw — nothing to crown).
            if (info.WinnerLine.Length > 0)
            {
                var wsz = _small.MeasureString(info.WinnerLine);
                DrawSmall(info.WinnerLine,
                    new Vector2(panelX + (panelW - wsz.X) / 2f, cursorY), WinnerColor, 1.35f);
                cursorY += _small.MeasureString("Mg").Y * 1.35f + 22f;
            }

            // A thin divider above the reason block.
            FillRect(panelX + pad, cursorY - 12f, panelW - 2f * pad, 1f,
                new Color(PanelBorder.R, PanelBorder.G, PanelBorder.B, 0.55f));

            // Reason text, word-wrapped to the panel width, centred per line.
            string reasonLabel = "REASON";
            var lsz = _small.MeasureString(reasonLabel);
            DrawSmall(reasonLabel,
                new Vector2(panelX + pad, cursorY + 4f), SubColor, 1f);
            cursorY += lsz.Y + 10f;

            float reasonWidth = panelW - 2f * pad;
            var lines = WrapText(info.Reason, _small, reasonWidth);
            foreach (var line in lines)
            {
                var lineSz = _small.MeasureString(line);
                DrawSmall(line, new Vector2(panelX + (panelW - lineSz.X) / 2f, cursorY),
                    ReasonColor, 1f);
                cursorY += lineSz.Y + 6f;
            }

            // P481 — Back button (native m_pbuttonBack / OnButtonBack,
            // gameoverscreen.cpp:609-625): the only control on the game-over
            // screen. The port is keyboard-driven (Wayland, no reliable mouse),
            // so Backspace triggers it; the button is drawn to match native's
            // on-screen affordance. ASCII labels only.
            float btnW = 120f, btnH = 36f;
            float btnX = panelX + (panelW - btnW) / 2f;
            float btnY = panelY + panelH - 66f;
            FillRect(btnX, btnY, btnW, btnH, new Color(PanelBorder.R, PanelBorder.G, PanelBorder.B, 0.22f));
            OutlineRect(btnX, btnY, btnW, btnH, PanelBorder, 1.5f);
            string backLabel = "BACK";
            var backSz = _small.MeasureString(backLabel);
            DrawSmall(backLabel, new Vector2(btnX + (btnW - backSz.X) / 2f, btnY + (btnH - backSz.Y) / 2f),
                SubColor, 1f);
            string backHint = "(BACKSPACE)";
            var hintSz = _small.MeasureString(backHint);
            DrawSmall(backHint, new Vector2(panelX + (panelW - hintSz.X) / 2f, btnY + btnH + 8f),
                SubColor, 0.8f);

            // Footer: the mission has concluded (matches the "stopped" reality).
            // ASCII hyphens only — the HUD spritefonts bake ASCII 32..126.
            string foot = "-- MISSION CONCLUDED --";
            var fsz = _small.MeasureString(foot);
            DrawSmall(foot, new Vector2(panelX + (panelW - fsz.X) / 2f, panelY + panelH - 28f),
                SubColor, 1f);
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>Word-wrap <paramref name="text"/> to at most
    /// <paramref name="maxWidth"/> pixels at scale 1, splitting on spaces so a
    /// long reason string never overflows the panel (helps: this port's reason
    /// strings are short sentences, but the wrap is a defensive guarantee that
    /// no future reason clips).</summary>
    private static List<string> WrapText(string text, SpriteFont font, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

        var words = text.Split(' ');
        string line = "";
        foreach (var word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureString(candidate).X <= maxWidth || line.Length == 0)
            {
                line = candidate;
            }
            else
            {
                result.Add(line);
                line = word;
            }
        }
        if (line.Length > 0) result.Add(line);
        return result;
    }

    private void DrawSmall(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void DrawBig(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_big, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)System.MathF.Round(x),
            (int)System.MathF.Round(y), (int)System.MathF.Round(w),
            (int)System.MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color, float thick)
    {
        FillRect(x, y, w, thick, color);
        FillRect(x, y + h - thick, w, thick, color);
        FillRect(x, y, thick, h, color);
        FillRect(x + w - thick, y, thick, h, color);
    }
}
