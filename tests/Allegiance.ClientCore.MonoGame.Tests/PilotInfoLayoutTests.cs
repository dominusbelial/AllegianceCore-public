using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P639 — pure-logic tests for the Character Sheet layout (the upgraded Pilot
/// Info screen): local-player extraction, rank-name resolution from the
/// RANK_INFO(13) table, the full stat breakdown, formatting and ASCII-safety.
/// No graphics device — same discipline as <see cref="ScoreboardLayoutTests"/>.
/// </summary>
public class PilotInfoLayoutTests
{
    private static ScoreRosterEntry E(string name, short side, short kills, short deaths,
        short rank = 0, float score = 0f, float combat = 0f) =>
        new(name, side, kills, deaths, rank, score, combat);

    private static RankInfoEntry R(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    private static readonly RankInfoEntry[] Ranks = { R(0, "Newbie"), R(9, "Inter. 4") };

    /// <summary>⭐ The sheet extracts the local player's OWN row (matched by name)
    /// and reports the full stat breakdown in order: rank name, kills, deaths,
    /// ejections, rescues, base kills, base captures, artifacts, flags, combat
    /// rating, score.</summary>
    [Fact]
    public void Build_returns_local_players_full_stats_in_order()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("Ace", 0, 4, 2, 9, 900f, 310.4375f),
            E("Bee", 1, 1, 5, 3, 402f, 205.015625f),
        }, "Ace", Ranks);

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("Ace", p.Name);
        Assert.Equal("S0", p.SideLabel);
        Assert.Equal("Inter. 4", p.RankName);   // resolved from the rank table

        Assert.Equal(new[]
        {
            "RANK NAME", "KILLS", "DEATHS", "EJECTIONS", "RESCUES", "BASE KILLS",
            "BASE CAPTURES", "ARTIFACTS", "FLAGS", "COMBAT RATING", "SCORE",
        }, p.Stats.Select(s => s.Label).ToArray());
        Assert.Equal("4", p.Stats[1].Value);      // kills
        Assert.Equal("2", p.Stats[2].Value);      // deaths
        Assert.Equal("310.4", p.Stats[9].Value);  // combat rating, F1
        Assert.Equal("900", p.Stats[10].Value);   // score, F0
    }

    /// <summary>⭐ The sheet does NOT show another player's row — it matches
    /// strictly the local player's name, even when others have richer scores.</summary>
    [Fact]
    public void Build_does_not_confuse_another_player_for_self()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("RichKiller", 0, 999, 888, 40, 5555f, 777.875f),
            E("Me", 0, 2, 3, 1, 210f, 312.5f),
        }, "Me", Ranks);

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("2", p.Stats[1].Value);     // my kills, not RichKiller's
        Assert.Equal("3", p.Stats[2].Value);     // my deaths
        Assert.Equal("312.5", p.Stats[9].Value); // my combat rating
        Assert.Equal("210", p.Stats[10].Value);  // my score
    }

    /// <summary>Returns null when the local player isn't in the roster yet (pre-
    /// join / no ScoreRosterUpdate received) — the renderer draws nothing rather
    /// than a placeholder.</summary>
    [Fact]
    public void Build_returns_null_when_self_not_yet_in_roster()
    {
        var info = PilotInfoLayout.Build(new[] { E("SomeoneElse", 0, 1, 1) }, "Me", Ranks);
        Assert.Null(info);
    }

    /// <summary>Returns null when there's no known local player name (not yet
    /// assigned a ship / MyShipId unset).</summary>
    [Fact]
    public void Build_returns_null_when_no_self_name()
    {
        Assert.Null(PilotInfoLayout.Build(new[] { E("Me", 0, 1, 1) }, null, Ranks));
        Assert.Null(PilotInfoLayout.Build([], "Me", Ranks));
    }

    /// <summary>P761c — a stat value is right-aligned to the panel's INNER edge
    /// (the content width the panel was measured from); it must never run over
    /// the border the way the "0.0" combat rating did.</summary>
    [Fact]
    public void StatValue_RightAlignsInsideTheInnerPanelWidth()
    {
        const float textLeft = 10f, innerRight = 110f;
        float x = PilotInfoOverlayRenderer.ValueLeft(textLeft, innerRight, valueWidth: 20f);
        Assert.Equal(90f, x);
        Assert.True(x + 20f <= innerRight);
        // A value too wide for the content column is clamped to its left edge
        // (the renderer fits the string first).
        Assert.Equal(textLeft, PilotInfoOverlayRenderer.ValueLeft(textLeft, innerRight, valueWidth: 200f));
    }

    /// <summary>P761c — native's team pane / character sheet are opaque screens;
    /// the port draws them over the HUD, so their panels are near-opaque (the
    /// old 0.92 sheet let the gauges print through).</summary>
    [Fact]
    public void TeamPaneAndCharacterSheetPanels_AreNearOpaque()
    {
        Assert.True(TeamPaneRenderer.PanelAlpha >= 0.99f, $"team pane alpha {TeamPaneRenderer.PanelAlpha}");
        Assert.True(PilotInfoOverlayRenderer.PanelAlpha >= 0.99f, $"sheet alpha {PilotInfoOverlayRenderer.PanelAlpha}");
    }

    /// <summary>A non-ASCII name is scrubbed to printable ASCII so the spritefonts
    /// never throw on a mangled/hostile name (same P214/p217 lesson).</summary>
    [Fact]
    public void Build_scrubs_non_ascii_from_name()
    {
        var info = PilotInfoLayout.Build(new[] { E("H\u00e9llo\u2191", 0, 1, 1) }, "H\u00e9llo\u2191", Ranks);
        Assert.NotNull(info);
        Assert.DoesNotContain(info.Value.Name, c => c < 32 || c > 126);
        Assert.DoesNotContain('\u00e9', info.Value.Name);
    }
}
