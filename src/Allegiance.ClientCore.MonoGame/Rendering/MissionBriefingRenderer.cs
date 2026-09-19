using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P271 — draws the dismissible mission-briefing popup: a centred translucent
/// panel over the pre-launch scene showing the real <c>SET_BRIEFING_TEXT(9)</c>
/// body the server sends on join (in this port the starting cluster name),
/// a synthesized title, the live "Mission starts in Ns" countdown, and a dismiss
/// hint. Follows this client's established overlay conventions (mirroring
/// <see cref="HelpOverlayRenderer"/> / <see cref="ScoreboardOverlayRenderer"/>):
/// it begins/ends its own <see cref="SpriteBatch"/> session, draws a translucent
/// panel with a coloured border, and keeps every drawn string pure ASCII (the
/// HUD spritefonts bake ASCII 32..126 only).
///
/// <para>The popup is drawn during the pre-launch briefing window and is
/// dismissible (Enter/Esc) or auto-dismissed by <see cref="Game1"/> once the
/// countdown expires and the mission goes live — mirroring native
/// <c>MissionBriefPopup</c>, which shows only while
/// <c>GetStage() == STAGE_STARTING</c> and closes automatically when the stage
/// advances (<c>WinTrek/teamscreen.cpp:1668-1704</c>).</para>
/// </summary>
public sealed class MissionBriefingRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color PanelBg = new(0.016f, 0.030f, 0.045f, 0.935f);
    private static readonly Color PanelBorder = new(0.16f, 0.45f, 0.44f);
    private static readonly Color HeaderColor = new(0.40f, 0.88f, 0.84f);
    private static readonly Color TitleColor = new(0.92f, 0.96f, 0.97f);
    private static readonly Color BodyColor = new(0.84f, 0.92f, 0.92f);
    private static readonly Color CountdownColor = new(1.00f, 0.78f, 0.30f);
    private static readonly Color PreparingColor = new(0.52f, 0.72f, 0.70f);
    private static readonly Color HintColor = new(0.62f, 0.74f, 0.74f);

    private const string Header = "MISSION BRIEFING";
    private const string Hint = "(PRESS ENTER OR ESC TO DISMISS)";

    public MissionBriefingRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>
    /// Draw the briefing popup for the current viewport. <paramref name="title"/>
    /// is the synthesized title (in this port the real starting-cluster name from
    /// the wire, used as both the title and the body — the server sends only that
    /// one location string with <c>FGenerateCivBriefing=true</c>). <paramref name="body"/>
    /// is the <c>SET_BRIEFING_TEXT(9)</c> <c>Text</c> body verbatim (real wire data).
    /// <paramref name="remainingSeconds"/> is the integer seconds left in the
    /// briefing countdown, or <c>null</c> if the countdown has not begun yet
    /// (the popup then shows a neutral preparing line instead of a ticking number).
    /// </summary>
    public void Draw(Viewport viewport, string title, string body, int? remainingSeconds)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Sanitise every string to the ASCII range the spritefonts bake.
        string safeTitle = ScoreboardLayout.AsciiSafe(title);
        string safeBody = ScoreboardLayout.AsciiSafe(body);

        string countdown = remainingSeconds is { } rem
            ? $"MISSION STARTS IN {rem}s..."
            : "MISSION PREPARING...";

        // Natural content width = the widest single line, then clamp so nothing
        // clips the viewport, and wrap the body to that width.
        float headerW = _small.MeasureString(Header).X;
        float titleW = _big.MeasureString(safeTitle).X;
        float countdownW = _small.MeasureString(countdown).X;
        float hintW = _small.MeasureString(Hint).X;
        float bodyOneLine = _small.MeasureString(safeBody).X;

        float maxLine = MathF.Max(MathF.Max(headerW, titleW),
            MathF.Max(MathF.Max(countdownW, hintW), bodyOneLine));
        float viewportLimit = w * MissionBriefingLayout.MaxPanelWidthFraction
                            - 2f * MissionBriefingLayout.Padding;
        float contentW = MathF.Min(maxLine, viewportLimit);
        if (contentW <= 0f) contentW = 1f;

        float spaceW = _small.MeasureString(" ").X;
        IReadOnlyList<string> bodyLines =
            MissionBriefingLayout.WrapWords(safeBody, s => _small.MeasureString(s).X, spaceW, contentW);

        float actualContentW = contentW;
        foreach (var ln in bodyLines)
            actualContentW = MathF.Max(actualContentW, _small.MeasureString(ln).X);

        float panelW = actualContentW + 2f * MissionBriefingLayout.Padding;
        float titleH = _big.MeasureString("M").Y;
        float smallH = _small.MeasureString("M").Y;
        float panelH = MissionBriefingLayout.Padding
                     + smallH                                  // header
                     + MissionBriefingLayout.BlockGap
                     + titleH                                  // title
                     + MissionBriefingLayout.BlockGap
                     + bodyLines.Count * MissionBriefingLayout.LineHeight  // body
                     + MissionBriefingLayout.BlockGap
                     + smallH                                  // countdown
                     + MissionBriefingLayout.BlockGap
                     + smallH                                  // hint
                     + MissionBriefingLayout.Padding;

        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        float cx = w / 2f;

        float headerY = panelY + MissionBriefingLayout.Padding;
        float titleY = headerY + smallH + MissionBriefingLayout.BlockGap;
        float bodyY = titleY + titleH + MissionBriefingLayout.BlockGap;
        float countdownY = bodyY + bodyLines.Count * MissionBriefingLayout.LineHeight
                         + MissionBriefingLayout.BlockGap;
        float hintY = countdownY + smallH + MissionBriefingLayout.BlockGap;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            CenterSmall(Header, cx, headerY, HeaderColor);
            CenterBig(safeTitle, cx, titleY, TitleColor);
            foreach (var (line, i) in bodyLines.Select((l, i) => (l, i)))
                CenterSmall(line, cx, bodyY + i * MissionBriefingLayout.LineHeight, BodyColor);
            CenterSmall(countdown, cx, countdownY,
                remainingSeconds is { } ? CountdownColor : PreparingColor);
            CenterSmall(Hint, cx, hintY, HintColor);
        }
        finally
        {
            _sb.End();
        }
    }

    private void CenterSmall(string text, float cx, float y, Color color)
    {
        var sz = _small.MeasureString(text);
        _sb.DrawString(_small, text, new Vector2(cx - sz.X / 2f, y), color, 0f,
            Vector2.Zero, 1f, SpriteEffects.None, 0f);
    }

    private void CenterBig(string text, float cx, float y, Color color)
    {
        var sz = _big.MeasureString(text);
        _sb.DrawString(_big, text, new Vector2(cx - sz.X / 2f, y), color, 0f,
            Vector2.Zero, 1f, SpriteEffects.None, 0f);
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x),
            (int)MathF.Round(y), (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
