using System;
using System.Collections.Generic;
using System.Text;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P271 — pure (MonoGame-free) layout for the mission-briefing popup: greedy
/// word-wrap of the briefing body into lines that fit a maximum width, plus the
/// spacing constants the <see cref="MissionBriefingRenderer"/> lays out the
/// panel with. Kept free of any MonoGame type (mirroring
/// <see cref="HelpOverlayLayout"/> / <see cref="ScoreboardLayout"/>) so the
/// wrapping logic is headless-unit-testable without a graphics device: the
/// renderer supplies per-word measured widths (necessarily font-dependent) and
/// this class decides only which words join which line, which is the meaningful
/// testable logic.
/// </summary>
public static class MissionBriefingLayout
{
    /// <summary>Horizontal padding inside the panel (px).</summary>
    public const float Padding = 26f;

    /// <summary>Vertical gap between content blocks (header/title/body/countdown/
    /// hint) inside the panel (px).</summary>
    public const float BlockGap = 10f;

    /// <summary>Vertical line pitch for wrapped body lines (px).</summary>
    public const float LineHeight = 20f;

    /// <summary>Cap on the panel width as a fraction of the viewport width, so a
    /// pathological long briefing never clips past the screen edges.</summary>
    public const float MaxPanelWidthFraction = 0.92f;

    /// <summary>
    /// Greedily wrap <paramref name="text"/> into lines each fitting
    /// <paramref name="maxWidth"/>, splitting on single spaces. The first word
    /// of any line always starts it even if it alone overflows the width (a
    /// single unbreakably-long word is placed on its own line rather than
    /// dropped or crashed); otherwise words are packed while
    /// <c>spaceWidth + wordWidth</c> fits. <paramref name="measureWord"/>
    /// returns a word's width at the renderer's font scale, and
    /// <paramref name="spaceWidth"/> the width of a single space — both supplied
    /// by the caller because they are font-dependent. Multi-space gaps collapse
    /// to one (the wire briefing is a single location name; this just defends
    /// against hostile formatting).
    /// </summary>
    public static IReadOnlyList<string> WrapWords(
        string text,
        Func<string, float> measureWord,
        float spaceWidth,
        float maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0f)
            return Array.Empty<string>();

        // Native briefing strings carry explicit line breaks (missionbrief.mdl
        // "\n\nOther objectives include:\n", "\n *  Control …"): each
        // paragraph wraps on its own and blank lines are kept.
        if (text.Contains('\n'))
        {
            var all = new List<string>();
            foreach (var paragraph in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(paragraph)) { all.Add(""); continue; }
                all.AddRange(WrapWords(paragraph, measureWord, spaceWidth, maxWidth));
            }
            return all;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur = new StringBuilder();
        float curW = 0f;

        foreach (var word in words)
        {
            float ww = measureWord(word);
            if (cur.Length == 0)
            {
                cur.Append(word);
                curW = ww;                      // always start a line, even overflowing
            }
            else if (curW + spaceWidth + ww <= maxWidth)
            {
                cur.Append(' ').Append(word);
                curW += spaceWidth + ww;
            }
            else
            {
                lines.Add(cur.ToString());
                cur.Clear().Append(word);
                curW = ww;
            }
        }

        if (cur.Length > 0)
            lines.Add(cur.ToString());

        return lines;
    }
}
