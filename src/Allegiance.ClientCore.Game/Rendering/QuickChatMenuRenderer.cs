using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.QuickChat;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P217 — the on-screen quick-chat menu overlay. Shows the current
/// <see cref="QuickChatMenu"/> level's choices — each with its real single-key
/// shortcut and caption, matching the original's <c>quickchat.mdl</c> data
/// exactly (e.g. "1) Affirmative", "A) ATTACK..."), in the same glass-panel
/// aesthetic as <see cref="HudRenderer"/> / <see cref="ChatRenderer"/>.
/// Presentation only: the caller drives the menu state machine in <c>Game1</c>;
/// this draws whatever menu node is current.
/// </summary>
public sealed class QuickChatMenuRenderer
{
    private static readonly Color PanelBg = new(0.010f, 0.035f, 0.036f, 0.72f);
    private static readonly Color BorderDim = new(0.14f, 0.34f, 0.32f);
    private static readonly Color Accent = new(0.09f, 0.92f, 0.83f);
    private static readonly Color TextMain = new(0.88f, 0.96f, 0.95f);
    private static readonly Color TextDim = new(0.48f, 0.64f, 0.62f);
    private static readonly Color LeafCol = new(0.96f, 0.96f, 0.90f);

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _font;
    private readonly SpriteFont _big;

    public QuickChatMenuRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont font, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _font = font;
        _big = big;
    }

    /// <summary>
    /// Draw the current menu level centered on screen. <paramref name="path"/> is
    /// the ancestor trail (deepest first? see below) used to title the panel and
    /// hint at the Escape-back level. Self-contained SpriteBatch session.
    /// </summary>
    /// <param name="menu">The menu level to display.</param>
    /// <param name="crumbTrail">Ancestor trail, deepest last — the last element
    /// is <paramref name="menu"/> itself; a longer trail means Escape backs out
    /// a level rather than closing.</param>
    public void Draw(QuickChatMenu menu, IReadOnlyList<QuickChatMenu> crumbTrail, Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Title: the breadcrumb trail ("mainMenu > attackMenu").
        string title = string.Join(" > ",
            crumbTrail.Select(m => m.Name.Length == 0 ? "(menu)" : m.Name));

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            const float margin = 30f;
            const float rowHeight = 26f;
            float panelW = Math.Min(560f, w - 2f * margin);
            float titleH = 34f;
            float panelH = titleH + menu.Choices.Count * rowHeight + 26f;
            float x = (w - panelW) / 2f;
            float y = (h - panelH) / 2f;

            FillRect(x, y, panelW, panelH, PanelBg);
            FillRect(x, y, panelW, 2f, Accent);
            FillRect(x, y, 2f, panelH, Accent);

            // Title.
            var tsz = _font.MeasureString(title);
            _sb.DrawString(_font, title, new Vector2(x + 14f, y + 8f), Accent);

            // Header row.
            _sb.DrawString(_font, "KEY   VOICE COMMAND", new Vector2(x + 14f, y + titleH), TextDim);

            for (int i = 0; i < menu.Choices.Count; i++)
            {
                var ch = menu.Choices[i];
                float ry = y + titleH + 8f + i * rowHeight;
                bool isSub = ch.Submenu is not null;
                var keyStr = ch.Key.ToString();
                // Shortcut in the accent column; caption after it; submenus get
                // a trailing "..." marker and an accent tint to hint descent.
                string caption = isSub ? ch.Label.TrimEnd() + "..." : ch.Label;
                var color = isSub ? Accent : LeafCol;
                _sb.DrawString(_font, keyStr, new Vector2(x + 14f, ry), isSub ? Accent : TextDim);
                var cap = Rendering.ChatLogFormat.AsciiSafe(caption);
                _sb.DrawString(_font, cap, new Vector2(x + 58f, ry), color);
            }

            var hint = crumbTrail.Count > 1 ? "ESC = back one level" : "ESC = close";
            var hsz = _font.MeasureString(hint);
            _sb.DrawString(_font, hint, new Vector2(x + panelW - hsz.X - 14f, y + panelH - 22f), TextDim);
        }
        finally
        {
            _sb.End();
        }
    }

    private void FillRect(float px, float py, float pw, float ph, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(px), (int)MathF.Round(py),
            (int)MathF.Round(pw), (int)MathF.Round(ph)), color);
}
