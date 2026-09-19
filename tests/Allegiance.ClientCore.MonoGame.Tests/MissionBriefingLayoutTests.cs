using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P271 — pure-logic tests for <see cref="MissionBriefingLayout.WrapWords"/>,
/// the word-wrap that lays the briefing body into lines inside the popup panel.
/// No graphics device — word widths come from a monospaced stub
/// (<c>width == char count</c>, space width 1) so wrapping is fully
/// deterministic.
/// </summary>
public class MissionBriefingLayoutTests
{
    private static readonly Func<string, float> Mono = s => s.Length;
    private const float SpaceW = 1f;

    [Fact]
    public void WrapWords_PacksWordsIntoLinesWithinMaxWidth()
    {
        var lines = MissionBriefingLayout.WrapWords(
            "a bb ccc dd ee", Mono, SpaceW, maxWidth: 6f);

        // "a bb ccc" = 1+1+2+1+3 = 8 > 6, so it breaks. Greedy packing:
        //   line1: "a bb"   (1+1+2 = 4)
        //   line2: "ccc dd" (3+1+2 = 6)
        //   line3: "ee"     (2)
        Assert.Equal(new[] { "a bb", "ccc dd", "ee" }, lines);
    }

    [Fact]
    public void WrapWords_DoesNotDropASingleLongWord()
    {
        // A word wider than maxWidth still gets a line of its own (never dropped,
        // never crashes) — the renderer then measures the panel to fit it.
        var lines = MissionBriefingLayout.WrapWords(
            "tiny ANTIDISESTABLISHMENTARIANISM tiny", Mono, SpaceW, maxWidth: 8f);

        Assert.Equal(new[] { "tiny", "ANTIDISESTABLISHMENTARIANISM", "tiny" }, lines);
    }

    [Fact]
    public void WrapWords_CollapsesMultipleSpaces()
    {
        var lines = MissionBriefingLayout.WrapWords(
            "one    two   three", Mono, SpaceW, maxWidth: 100f);

        Assert.Equal(new[] { "one two three" }, lines);
    }

    [Fact]
    public void WrapWords_ShortText_IsASingleLine()
    {
        var lines = MissionBriefingLayout.WrapWords("hi there", Mono, SpaceW, maxWidth: 100f);
        Assert.Equal(new[] { "hi there" }, lines);
    }

    [Fact]
    public void WrapWords_EmptyOrBlank_ReturnsNoLines()
    {
        Assert.Empty(MissionBriefingLayout.WrapWords("", Mono, SpaceW, 100f));
        Assert.Empty(MissionBriefingLayout.WrapWords("   ", Mono, SpaceW, 100f));
        Assert.Empty(MissionBriefingLayout.WrapWords("hello", Mono, SpaceW, 0f));
    }
}
