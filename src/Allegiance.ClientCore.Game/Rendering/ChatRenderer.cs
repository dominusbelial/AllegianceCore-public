using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Renders the in-flight chat pane. HUD-10 moves the history pane to the native
/// <c>ChatListPane</c> geometry (355 px wide, <c>8 + ChatFontSize·NumChatLines</c>
/// tall, hidden scrollbar, <c>CommPane</c> at <c>SideTop(-160,-126)</c>;
/// dialog.mdl:180, chatpane.cpp:374-380, dialog.mdl:5394) and draws the native
/// <c>ComposingPane</c> art variants for the compose states the port reaches
/// (dialog.mdl:89-170). The native chat text is the sender's side colour
/// (<c>trekigc.cpp:4736-4741</c>); the model carries those colours per line.
/// </summary>
public sealed class ChatRenderer
{
    private static readonly Color PanelBg = new(0.010f, 0.030f, 0.033f, 0.55f);
    private static readonly Color BorderDim = new(0.14f, 0.34f, 0.32f);
    private static readonly Color TextMain = new(0.88f, 0.96f, 0.95f);
    private static readonly Color OlderHint = new(0.5f, 0.6f, 0.58f);

    /// <summary>Maximum chat lines shown in the log panel at once. HUD-10 sets
    /// this to the native default <c>ChatLines</c> count (7, WinTrek.cpp:3317).</summary>
    public const int MaxVisible = ChatPaneLayout.DefaultLines;

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _font;
    private readonly HudNativeTextures? _art;
    private readonly NativeFonts? _native;

    public ChatRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont font,
        HudNativeTextures? art = null, NativeFonts? nativeFonts = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _font = font;
        _art = art;
        _native = nativeFonts;
    }

    // FONT-1 — classic chat: history lines use the native largeFont (and would
    // use largeBoldFont for leader lines, chatpane.cpp:34 — the port's chat
    // model carries no per-line "from leader" flag, so all lines use largeFont,
    // a recorded gap), compose text uses the native smallFont. When no registry
    // is supplied (isolated renderer tests) the historic mono font is used.
    private SpriteFont HistoryFont => _native is { } n ? n.Get(NativeFont.LargeFont, 1f).Font : _font;
    private SpriteFont ComposeFont => _native is { } n ? n.Get(NativeFont.SmallFont, 1f).Font : _font;

    /// <summary>
    /// Draw the chat log + (optionally) the active input line. Self-contained —
    /// begins/ends its own <see cref="SpriteBatch"/> session.
    /// <paramref name="lines"/> are the already-formatted display strings for the
    /// ChatPaneModel's current window, oldest first. <paramref name="offset"/>
    /// says whether the pane is scrolled back: a dim "OLDER" hint then rides the
    /// top border. When <paramref name="composeState"/> is a composing state the
    /// native panel art is drawn at the native anchor and slide progress.
    /// </summary>
    public void Draw(IReadOnlyList<string> lines, int offset, string inputLine, bool composing,
        double nowSeconds, Viewport viewport, string targetLabel, string? filterLabel = null,
        NativeComposeState composeState = NativeComposeState.NotComposing, float composeProgress = 0f,
        IReadOnlyList<HudColor?>? lineColors = null, HudColor? sideColor = null,
        int paneLines = ChatPaneLayout.DefaultLines)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            int take = Math.Min(MaxVisible, Math.Max(0, lines.Count));
            bool scrolled = offset > 0;
            int paneLeft = ChatPaneLayout.Left(viewport.Width);
            int paneTop = ChatPaneLayout.Top(ChatPaneLayout.ConfiguredLines(paneLines));
            int paneHeight = ChatPaneLayout.Height(ChatPaneLayout.ConfiguredLines(paneLines));
            // HUD-11 — native largeFont at ChatFontSize 2 is 14 px high
            // (chatpane.cpp:374-380); the port spritefont is taller, so scale
            // each line into its native item slot. FONT-1: the native largeFont
            // bake is exactly 14 px tall, so ChatPaneLayout.TextScale returns 1
            // and the shrink drops away (rows no longer overlap).
            SpriteFont historyFont = HistoryFont;
            float textScale = ChatPaneLayout.TextScale(historyFont.LineSpacing);

            if (take > 0)
            {
                // CommPane is a dark native list over the 3-D scene; the port
                // keeps the same subtle panel fill rather than drawing a black
                // box (the native concommdisplay art has no alpha channel).
                FillRect(paneLeft, paneTop, ChatPaneLayout.Width, paneHeight, PanelBg);
                FillRect(paneLeft, paneTop, 2f, paneHeight, BorderDim);

                if (scrolled)
                    _sb.DrawString(historyFont, "(OLDER - PgDn to newest)",
                        new Vector2(paneLeft + 3f, paneTop - historyFont.LineSpacing * textScale), OlderHint, 0f,
                        Vector2.Zero, textScale, SpriteEffects.None, 0f);

                if (filterLabel is not null)
                {
                    string label = $"[{filterLabel}]";
                    float lx = paneLeft + ChatPaneLayout.Width - historyFont.MeasureString(label).X * textScale - 8f;
                    _sb.DrawString(historyFont, label, new Vector2(Math.Max(paneLeft, lx), paneTop - historyFont.LineSpacing * textScale),
                        OlderHint, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
                }

                // Native item text starts 3 px inside the list item (chatpane.cpp:95)
                // and advances by the native line height. The port font is taller,
                // so each row is clipped to its native 14 px slot and the pane's
                // bottom edge; the pane size/line count stay native.
                for (int i = 0; i < take; i++)
                {
                    string line = lines[i];
                    if (historyFont.MeasureString(line).X * textScale > ChatPaneLayout.Width - 6f)
                        line = TruncateToWidth(historyFont, line, (ChatPaneLayout.Width - 6f) / textScale);
                    int y = paneTop + 3 + i * ChatPaneLayout.NativeFontHeight;
                    if (y + ChatPaneLayout.NativeFontHeight > paneTop + paneHeight) break;
                    Color color = lineColors is not null && i < lineColors.Count && lineColors[i] is { } lc
                        ? From(lc) : TextMain;
                    _sb.DrawString(historyFont, line, new Vector2(paneLeft + 3f, y), color, 0f,
                        Vector2.Zero, textScale, SpriteEffects.None, 0f);
                }
            }

            if (composing && composeState != NativeComposeState.NotComposing)
                DrawCompose(inputLine, targetLabel, composeState, composeProgress, viewport, sideColor);
        }
        finally
        {
            _sb.End();
        }
    }

    private void DrawCompose(string inputLine, string targetLabel, NativeComposeState state,
        float progress, Viewport viewport, HudColor? sideColor)
    {
        var variant = ComposePaneLayout.Variant(state);
        float left = ComposePaneLayout.Left(viewport.Width, progress);
        float top = ComposePaneLayout.Top(viewport.Height, variant.PanelHeight, progress);
        var panel = new Rectangle((int)MathF.Round(left), (int)MathF.Round(top),
            variant.PanelWidth, variant.PanelHeight);

        var art = _art?.ForState(state);
        if (art is not null)
            _sb.Draw(art, panel, Color.White);
        else
        {
            FillRect(panel.X, panel.Y, panel.Width, panel.Height, PanelBg);
            FillRect(panel.X, panel.Y, 2f, panel.Height, BorderDim);
        }

        Color colour = sideColor is { } sc ? From(sc) : TextMain;
        SpriteFont composeFont = ComposeFont;
        // A 1 px dark edge keeps the native yellow/white glyphs legible over the
        // bright command bar art (the port font is not the native smallFont).
        if (variant.HasRecipient)
        {
            string recipient = targetLabel.Trim();
            DrawBoxedCentered(composeFont, recipient, panel.X + variant.RecipientLeft, panel.Y + variant.RecipientTop,
                variant.RecipientWidth, variant.RecipientHeight, colour);
        }

        if (variant.HasVerbIcon && _art?.VerbIcons.Count > 0)
        {
            short cid = CommandIdForCompose(inputLine);
            if (_art.VerbIcons.TryGetValue(cid, out var icon))
                _sb.Draw(icon, new Rectangle(panel.X + variant.VerbLeft, panel.Y + variant.VerbTop, 16, 16), Color.White);
        }

        if (variant.HasText)
        {
            // UpdateComposedChat renders m_strTypedText + "|" (console.cpp:1290-1292).
            string text = state == NativeComposeState.Command
                ? inputLine
                : inputLine + "|";
            string fit = text;
            if (composeFont.MeasureString(fit).X > variant.TextWidth)
                fit = TruncateToWidth(composeFont, fit, variant.TextWidth);
            _sb.DrawString(composeFont, fit,
                new Vector2(panel.X + variant.TextLeft, panel.Y + variant.TextTop), colour);
        }
    }

    /// <summary>Map the typed command text's leading verb to a CommandID for the
    /// native verb icon (console.cpp:2334-2355, c_cdAllCommands order).</summary>
    public static short CommandIdForCompose(string input)
    {
        string s = input.TrimStart();
        if (s.Length == 0) return 0;
        return char.ToLowerInvariant(s[0]) switch
        {
            'a' => 1,   // attack
            'c' => 2,   // capture
            'd' => 3,   // defend
            'p' => 4,   // pickup
            'g' => 5,   // goto
            'r' => 6,   // repair
            'j' => 7,   // join
            'm' => 8,   // mine
            'b' => 9,   // build
            _ => 0,
        };
    }

    private void DrawBoxedCentered(SpriteFont font, string text, int x, int y, int width, int height, Color color)
    {
        if (width <= 0 || text.Length == 0) return;
        float measured = font.MeasureString(text).X;
        if (measured > width)
            text = TruncateToWidth(font, text, width);
        _sb.DrawString(font, text,
            new Vector2(x + (width - font.MeasureString(text).X) / 2f, y), color);
    }

    /// <summary>Classic chat presentation of the shared HUD frame.</summary>
    public void Draw(HudModel model, Viewport viewport)
    {
        if (!model.Visible || model.Context.Chat is not { } chat) return;
        Draw(chat.Lines, chat.Offset, chat.Input, chat.Composing, chat.NowSeconds, viewport,
            chat.Recipient, chat.FilterLabel, chat.ComposeState, chat.ComposeProgress,
            chat.LineColors, chat.SideColor, chat.PaneLines);
    }

    private string TruncateToWidth(SpriteFont font, string s, float maxWidth)
    {
        string t = s;
        while (t.Length > 0 && font.MeasureString(t).X > maxWidth)
            t = t.Substring(0, t.Length - 1);
        return t + "...";
    }

    private static Color From(HudColor c) => new(c.R, c.G, c.B, c.A);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);
}
