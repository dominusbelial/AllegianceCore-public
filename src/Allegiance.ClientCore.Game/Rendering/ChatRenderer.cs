using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Renders the in-flight chat overlay: a small scrolling log of the last few
/// <c>ChatMessage</c> lines (bottom-left, same glass-panel aesthetic as
/// <see cref="HudRenderer"/>), plus the active composing line beneath it while
/// chat input is open. Purely presentation — the caller formats the lines via
/// <see cref="ChatLogFormat"/> (which owns the testable string logic) and hands
/// already-built strings in here.
/// </summary>
public sealed class ChatRenderer
{
    private static readonly Color PanelBg = new(0.010f, 0.030f, 0.033f, 0.55f);
    private static readonly Color BorderDim = new(0.14f, 0.34f, 0.32f);
    private static readonly Color TextMain = new(0.88f, 0.96f, 0.95f);
    private static readonly Color InputPrompt = new(0.07f, 0.95f, 0.84f);
    private static readonly Color OlderHint = new(0.5f, 0.6f, 0.58f);

    /// <summary>Maximum chat lines shown in the log panel at once.</summary>
    public const int MaxVisible = 8;

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _font;

    public ChatRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont font)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _font = font;
    }

    /// <summary>
    /// Draw the chat log + (optionally) the active input line. Self-contained —
    /// begins/ends its own <see cref="SpriteBatch"/> session like
    /// <see cref="HudRenderer.Draw"/>. <paramref name="lines"/> are the already-
    /// formatted display strings, oldest first; <paramref name="offset"/> is the
    /// P626 chat-scroll offset (lines pulled from the newest edge — 0 = newest
    /// visible); only the <see cref="MaxVisible"/> lines at that offset are drawn.
    /// When scrolled back, a dim "OLDER" hint rides the top border so the player
    /// knows they are not at the live edge. When <paramref name="composing"/> is
    /// true the <paramref name="inputLine"/> is drawn as the active line, prefixed
    /// by <paramref name="targetLabel"/> (e.g. "[TEAM] ", P456), with a blinking
    /// cursor block (blink driven by <paramref name="nowSeconds"/>).
    /// </summary>
    public void Draw(IReadOnlyList<string> lines, int offset, string inputLine, bool composing, double nowSeconds, Viewport viewport, string targetLabel)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            const float margin = 20f;
            const float rowHeight = 22f;
            float panelLeft = margin;

            int start = ChatScroll.StartIndex(offset, lines.Count, MaxVisible);
            int take = Math.Min(MaxVisible, Math.Max(0, lines.Count - start));
            bool scrolled = ChatScroll.Clamp(offset, lines.Count, MaxVisible) > 0;
            var slice = new List<string>();
            for (int i = 0; i < take; i++)
                slice.Add(lines[start + i]);
            // Newest at the bottom, growing upward: panel sits at the bottom-left
            // with the newest line right above the input/composing area.
            float logTop = h - (composing ? 96f : 74f) - take * rowHeight;

            if (take > 0)
            {
                float panelH = take * rowHeight + 14f;
                FillRect(panelLeft - 10f, logTop - 8f, w * 0.52f, panelH, PanelBg);
                FillRect(panelLeft - 10f, logTop - 8f, 2f, panelH, BorderDim);

                if (scrolled)
                {
                    // P626 — a dim "OLDER" tag at the top border marks a scrolled-
                    // back view (PageDn / NextCommMsg returns to the live edge).
                    _sb.DrawString(_font, "(OLDER - PgDn to newest)", new Vector2(panelLeft, logTop - 24f), OlderHint);
                }

                for (int i = 0; i < take; i++)
                {
                    var line = slice[i];
                    var sz = _font.MeasureString(line);
                    // Clip long lines to the panel width instead of spilling over.
                    if (sz.X > w * 0.5f)
                        line = TruncateToWidth(line, w * 0.5f);
                    _sb.DrawString(_font, line, new Vector2(panelLeft, logTop + i * rowHeight), TextMain);
                }
            }

            if (composing)
            {
                // ASCII-only cursor block (the HUD spritefonts bake 32..126 only).
                // P456: the target label (e.g. "[TEAM] ") precedes the caret so the
                // player sees who will receive the message.
                string prompt = targetLabel + "> " + inputLine + (CursorBlock(nowSeconds) ? "_" : "");
                float iy = h - 62f;
                FillRect(panelLeft - 10f, iy - 6f, w * 0.52f, 30f, PanelBg);
                FillRect(panelLeft - 10f, iy - 6f, 2f, 30f, InputPrompt);
                _sb.DrawString(_font, prompt, new Vector2(panelLeft, iy), TextMain);
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private bool CursorBlock(double now) => ((long)(now * 2.0)) % 2 == 0;

    private string TruncateToWidth(string s, float maxWidth)
    {
        string t = s;
        while (t.Length > 0 && _font.MeasureString(t).X > maxWidth)
            t = t.Substring(0, t.Length - 1);
        return t + "...";
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);
}
