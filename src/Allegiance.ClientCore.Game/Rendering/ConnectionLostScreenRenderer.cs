using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P320 — draws the session-loss screen: the unambiguous, unmissable on-screen
/// answer to "why did everything just stop" when the TCP session to the server
/// drops (server crash / network hiccup) — the case that previously produced
/// total silence while the client kept rendering a frozen world (review H-4).
///
/// Reuses the established full-screen overlay pattern (the
/// <see cref="GameOverScreenRenderer"/> / MissionBriefing / SideSelect family):
/// it begins/ends its own <see cref="SpriteBatch"/> session (so it renders over
/// the 3D scene or into an offscreen capture), draws a translucent panel +
/// border, and keeps every drawn string pure ASCII (the HUD spritefonts bake
/// ASCII 32..126 only). It is modal (no dismiss button): the player must quit
/// (Esc). Reconnection is out of scope (P321 / review H-5).
/// </summary>
public sealed class ConnectionLostScreenRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    // Alarm palette on the teal HUD-accent family: a near-black backdrop panel
    // edged in bright alert red-orange (deliberately different from the
    // green/gold victory tones of the game-over screen) so "connection lost"
    // reads instantly as trouble, not as a mission conclusion.
    private static readonly Color PanelBg = new(0.06f, 0.012f, 0.010f, 0.965f);
    private static readonly Color PanelBorder = new(0.98f, 0.34f, 0.20f);
    private static readonly Color TopAccent = new(1.00f, 0.55f, 0.24f);
    private static readonly Color TitleColor = new(1.00f, 0.38f, 0.30f);
    private static readonly Color BodyColor = new(0.94f, 0.97f, 0.96f);
    private static readonly Color SubColor = new(0.72f, 0.78f, 0.76f);

    public ConnectionLostScreenRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the connection-lost panel for the current viewport.</summary>
    public void Draw(Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // A large centred panel — like the game-over screen it leaves the (now
        // frozen) scene visible around it, but the border/title make the state
        // unmistakable.
        float panelW = System.MathF.Min(w * 0.88f, 1000f);
        float panelH = System.MathF.Min(h * 0.62f, 440f);
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float pad = 34f;

        string title = "CONNECTION LOST";
        var tsz = _big.MeasureString(title);
        float titleScale = System.MathF.Min(1f, (panelW - 2f * pad) / tsz.X);
        float titleX = panelX + (panelW - tsz.X * titleScale) / 2f;

        float cursorY = panelY + 26f;
        float bigRow = _big.MeasureString("Mg").Y * titleScale;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder, 2f);
            FillRect(panelX, panelY, panelW, 4f, TopAccent);   // bright top edge

            // Title.
            DrawBig(title, new Vector2(titleX, cursorY), TitleColor, titleScale);
            cursorY += bigRow + 26f;

            // Body lines, word-wrapped and centred.
            string[] body =
            {
                "The connection to the game server was lost.",
                "The world has stopped updating and your",
                "controls are disabled.",
                "",
                "If this was not expected, the server may have",
                "crashed or your network connection dropped.",
            };
            foreach (var line in body)
            {
                var wsz = _small.MeasureString(line);
                DrawSmall(line, new Vector2(panelX + (panelW - wsz.X) / 2f, cursorY),
                    BodyColor, 1.25f);
                cursorY += _small.MeasureString("Mg").Y * 1.25f + 8f;
            }

            // Divider above the footer.
            FillRect(panelX + pad, cursorY + 4f, panelW - 2f * pad, 1f,
                new Color(PanelBorder.R, PanelBorder.G, PanelBorder.B, 0.55f));

            // Footer: how to leave.
            string foot = "-- PRESS ESC TO QUIT --";
            var fsz = _small.MeasureString(foot);
            DrawSmall(foot, new Vector2(panelX + (panelW - fsz.X) / 2f, panelY + panelH - 28f),
                SubColor, 1f);
        }
        finally
        {
            _sb.End();
        }
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
