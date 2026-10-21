using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P639 — pure-logic tests for the Character Sheet layout (the upgraded Pilot
/// Info screen): local-player extraction, rank-name resolution from the
/// RANK_INFO(13) table, the in-mission stat set (P774b: RANK, KILLS, DEATHS,
/// EJECTIONS only), formatting and ASCII-safety. No graphics device — same
/// discipline as <see cref="ScoreboardLayoutTests"/>.
/// </summary>
public class PilotInfoLayoutTests
{
    private static ScoreRosterEntry E(string name, short side, short kills, short deaths,
        short rank = 0, short ejections = 0) =>
        new(name, side, kills, deaths, ejections, rank);

    private static RankInfoEntry R(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    private static readonly RankInfoEntry[] Ranks = { R(0, "Newbie"), R(9, "Inter. 4") };

    /// <summary>⭐ P774b — the sheet shows exactly the roster fields with a
    /// native in-mission source, in order: RANK (resolved from the rank table,
    /// drawn as the banner by the renderer), KILLS, DEATHS, EJECTIONS
    /// (clintlib.h:346-361). RESCUES, BASE KILLS/CAPTURES, ARTIFACTS, FLAGS,
    /// COMBAT RATING and SCORE have no native in-mission display (never shown,
    /// or game-over-only from GAME_OVER_PLAYERS — gameoverscreen.cpp:146-158)
    /// and must not appear.</summary>
    [Fact]
    public void Build_returns_local_players_full_stats_in_order()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("Ace", 0, 4, 2, 9, ejections: 1),
            E("Bee", 1, 1, 5, 3),
        }, "Ace", Ranks);

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("Ace", p.Name);
        Assert.Equal("S0", p.SideLabel);
        Assert.Equal("Inter. 4", p.RankName);   // resolved from the rank table

        Assert.Equal(new[] { "RANK", "KILLS", "DEATHS", "EJECTIONS" },
            p.Stats.Select(s => s.Label).ToArray());
        Assert.Equal("Inter. 4", p.Stats[0].Value);
        Assert.Equal("4", p.Stats[1].Value);      // kills
        Assert.Equal("2", p.Stats[2].Value);      // deaths
        Assert.Equal("1", p.Stats[3].Value);      // ejections
    }

    /// <summary>⭐ The sheet does NOT show another player's row — it matches
    /// strictly the local player's name, even when others have richer counters.</summary>
    [Fact]
    public void Build_does_not_confuse_another_player_for_self()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("RichKiller", 0, 999, 888, 40, ejections: 7),
            E("Me", 0, 2, 3, 1),
        }, "Me", Ranks);

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("2", p.Stats[1].Value);     // my kills, not RichKiller's
        Assert.Equal("3", p.Stats[2].Value);     // my deaths
    }

    /// <summary>Returns null when the local player isn't in the roster yet (pre-
    /// join / no roster row received) — the renderer draws nothing rather
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
