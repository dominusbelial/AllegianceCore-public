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
    /// formatted display strings for the ChatPaneModel's current window, oldest
    /// first (the caller already applied the scroll offset); up to
    /// <see cref="MaxVisible"/> are drawn. <paramref name="offset"/> only says
    /// whether the pane is scrolled back: a dim "OLDER" hint then rides the top
    /// border so the player knows they are not at the live edge. When <paramref name="composing"/> is
    /// true the <paramref name="inputLine"/> is drawn as the active line, prefixed
    /// by <paramref name="targetLabel"/> (e.g. "[TEAM] ", P456), with a blinking
    /// cursor block (blink driven by <paramref name="nowSeconds"/>).
    /// P757: <paramref name="filterLabel"/> names the pane's active channel filter
    /// (native ChatPassesFilter machinery) in the pane header; null hides it.
    /// </summary>
    public void Draw(IReadOnlyList<string> lines, int offset, string inputLine, bool composing, double nowSeconds, Viewport viewport, string targetLabel, string? filterLabel = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            const float margin = 20f;
            const float rowHeight = 22f;
            float panelLeft = margin;
            // Stay left of the central gauge cluster (THR's 128px gauge is
            // centred at w/2-126.5*spread): the old 52%-of-screen panel ran
            // under the THR gauge and the WEAPONS/BOOST status line.
            float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
            float panelWidth = Math.Max(220f, w / 2f - 126.5f * spread - 76f);
            // Bottom of the history pane sits above the lower-left status band
            // (WEAPONS/BOOST is drawn at h-158).
            float logBottom = h - 182f;

            // The caller (BuildChatLines) already applied the ChatPaneModel's
            // offset and hands over the visible window; slicing again here made
            // a paused pane always fall back to the newest page (P757b). The
            // offset only tells us whether an OLDER marker is shown.
            int take = Math.Min(MaxVisible, Math.Max(0, lines.Count));
            bool scrolled = offset > 0;
            // Newest at the bottom, growing upward.
            float logTop = logBottom - take * rowHeight;

            if (take > 0)
            {
                float panelH = take * rowHeight + 14f;
                FillRect(panelLeft - 10f, logTop - 8f, panelWidth + 10f, panelH, PanelBg);
                FillRect(panelLeft - 10f, logTop - 8f, 2f, panelH, BorderDim);

                if (scrolled)
                {
                    // P626 — a dim "OLDER" tag at the top border marks a scrolled-
                    // back view (PageDn / NextCommMsg returns to the live edge).
                    _sb.DrawString(_font, "(OLDER - PgDn to newest)", new Vector2(panelLeft, logTop - 24f), OlderHint);
                }

                // P757 — the active channel-filter mode rides the top border on
                // the right, mirroring the native pane's filtered contents
                // (chatpane.cpp:299-358). Right-aligned inside the pane, clear of
                // the THR gauge (the old w*0.52-150 origin sat on it).
                if (filterLabel is not null)
                {
                    string label = $"[{filterLabel}]";
                    float lx = panelLeft + panelWidth - _font.MeasureString(label).X - 8f;
                    _sb.DrawString(_font, label, new Vector2(Math.Max(panelLeft, lx), logTop - 24f), OlderHint);
                }

                for (int i = 0; i < take; i++)
                {
                    var line = lines[i];
                    var sz = _font.MeasureString(line);
                    // Clip long lines to the panel width instead of spilling over.
                    if (sz.X > panelWidth - 4f)
                        line = TruncateToWidth(line, panelWidth - 4f);
                    _sb.DrawString(_font, line, new Vector2(panelLeft, logTop + i * rowHeight), TextMain);
                }
            }

            if (composing)
            {
                // ASCII-only cursor (the HUD spritefonts bake 32..126 only).
                // P456: the target label (e.g. "[TEAM] ") precedes the cursor so
                // the player sees who will receive the message. P757: the cursor
                // is native's always-shown "|" (UpdateComposedChat,
                // console.cpp:1290-1292: m_strTypedText + "|").
                string prompt = targetLabel + "> " + inputLine + "|";
                if (_font.MeasureString(prompt).X > panelWidth - 4f)
                    prompt = TruncateToWidth(prompt, panelWidth - 4f);
                float iy = h - 62f;
                FillRect(panelLeft - 10f, iy - 6f, panelWidth + 10f, 30f, PanelBg);
                FillRect(panelLeft - 10f, iy - 6f, 2f, 30f, InputPrompt);
                _sb.DrawString(_font, prompt, new Vector2(panelLeft, iy), TextMain);
            }
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>Classic chat presentation of the shared HUD frame.</summary>
    public void Draw(HudModel model, Viewport viewport)
    {
        if (!model.Visible || model.Context.Chat is not { } chat) return;
        Draw(chat.Lines, chat.Offset, chat.Input, chat.Composing, chat.NowSeconds, viewport, chat.Recipient, chat.FilterLabel);
    }

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
