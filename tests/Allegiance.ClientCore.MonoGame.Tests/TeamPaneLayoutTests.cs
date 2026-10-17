using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P639 — pure-logic tests for the Team Pane layout: side grouping, pilot-row
/// ordering, ship-status derivation, self-highlighting and the money/ballot
/// footer. No graphics device — same discipline as <see cref="ScoreboardLayoutTests"/>.
/// </summary>
public class TeamPaneLayoutTests
{
    [Fact]
    public void Commander_and_afk_markers_follow_roster_state_independently_of_location()
    {
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>
        {
            ["Amy"] = new(301, 1, -1, false, false, IsLeader: true, Ready: false, WingId: 3),
            ["Bob"] = new(301, 1, 9, false, false, Ready: true),
        };
        var lines = TeamPaneLayout.Build([E("Amy", 0), E("Bob", 0)], Ranks, ships, null, null, null, false, "Amy");
        var commander = lines.Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Highlight);
        Assert.Contains("[COMMANDER] [AFK]", commander.Text);
        Assert.Contains("SEC 1", commander.Text);
        Assert.Contains("WING:escort", commander.Text);
        var pilot = lines.Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot && !l.Highlight);
        Assert.Contains("DOCKED", pilot.Text);
        Assert.DoesNotContain("[COMMANDER]", pilot.Text);
        Assert.DoesNotContain("[AFK]", pilot.Text);
    }

    private static ScoreRosterEntry E(string name, short side, short kills = 0, short deaths = 0, short rank = 0) =>
        new(name, side, kills, deaths, rank, 0f, 0f);

    private static RankInfoEntry R(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    private static readonly RankInfoEntry[] Ranks = { R(0, "Newbie"), R(15, "Veteran 1") };

    private static TeamPaneLayout.ShipBrief Fly(string sector) =>
        new(HullId: 301, SectorId: short.Parse(sector), StationId: -1, Dead: false, Gone: false);
    private static TeamPaneLayout.ShipBrief Dock(short station = 901) =>
        new(HullId: 910, SectorId: 1, StationId: station, Dead: false, Gone: false);
    private static readonly TeamPaneLayout.ShipBrief Dead =
        new(HullId: 701, SectorId: 0, StationId: -1, Dead: true, Gone: false);

    /// <summary>⭐ The pane groups pilots by side and, within a side, sorts by
    /// name — the readable roster default for the team screen.</summary>
    [Fact]
    public void Build_groups_by_side_and_sorts_pilots_by_name()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("Zed", 1), E("Amy", 0), E("Bob", 0), E("Cal", 1) },
            Ranks,
            new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, null, null, false, null);

        var sideHeaders = lines.Where(l => l.Kind == TeamPaneLayout.LineKind.SideHeader)
            .Select(l => l.Text).ToArray();
        Assert.Equal(new[] { "SIDE 0  (players: 2)", "SIDE 1  (players: 2)" }, sideHeaders);

        var pilots = lines.Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot)
            .Select(l => l.Text).ToArray();
        // Amy & Bob (side 0), then Cal & Zed (side 1), each alpha-sorted.
        Assert.True(pilots[0].Contains("Amy") && pilots[1].Contains("Bob"));
        Assert.True(pilots[2].Contains("Cal") && pilots[3].Contains("Zed"));
    }

    /// <summary>⭐ Ship status is derived from the live entity: flying ships show
    /// their sector, docked ships show DOCKED (StationId != -1, the Game1.AutoProof
    /// docked gate), dead ships show DEAD.</summary>
    [Fact]
    public void Build_derives_ship_status_from_entity()
    {
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>
        {
            ["Fighter"] = Fly("3"),
            ["Docker"] = Dock(),
            ["KIA"] = Dead,
            // "NoShip" deliberately absent: a roster pilot with no ship entity.
        };
        var lines = TeamPaneLayout.Build(
            new[] { E("Fighter", 0), E("Docker", 0), E("KIA", 0), E("NoShip", 0) },
            Ranks, ships, null, null, null, false, null);

        var byName = lines.Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot)
            .ToDictionary(l => l.Text.Substring(4).Trim().Split("  ")[0].Trim(), l => l.Text);

        Assert.Contains("SEC 3", byName["Fighter"]);
        Assert.Contains("DOCKED", byName["Docker"]);
        Assert.Contains("DEAD", byName["KIA"]);
        Assert.Contains("?", byName["NoShip"]);
    }

    /// <summary>⭐ Rank names appear on each pilot row (RANK_INFO(13) table).</summary>
    [Fact]
    public void Build_puts_rank_name_on_each_row()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("Vet", 0, rank: 15), E("Green", 0, rank: 0) },
            Ranks,
            new Dictionary<string, TeamPaneLayout.ShipBrief>(), null, null, null, false, null);
        var byName = lines.Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot)
            .ToDictionary(l => l.Text.Substring(4).Trim().Split("  ")[0].Trim(), l => l.Text);
        Assert.Contains("Veteran 1", byName["Vet"]);
        Assert.Contains("Newbie", byName["Green"]);
    }

    /// <summary>⭐ The local player's own row is flagged for highlight.</summary>
    [Fact]
    public void Build_highlights_the_self_row()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("Amy", 0), E("Bob", 0) },
            Ranks,
            new Dictionary<string, TeamPaneLayout.ShipBrief>(), null, null, null, false, "Bob");
        Assert.False(lines.First(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("Amy")).Highlight);
        Assert.True(lines.First(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("Bob")).Highlight);
    }

    /// <summary>⭐ Money and the active ballot appear in the footer only when data
    /// exists; unknown wings remain explicit on pilot rows.</summary>
    [Fact]
    public void Build_footer_shows_money_ballot_and_unknown_wing()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("Amy", 0) }, Ranks,
            new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, myMoney: 3210, activeBallot: "Move miners to G7", isLeader: true, "Amy");

        Assert.Contains(lines, l => l.Kind == TeamPaneLayout.LineKind.Money && l.Text == "MY MONEY: 3210");
        Assert.Contains(lines, l => l.Kind == TeamPaneLayout.LineKind.Ballot && l.Text.Contains("Move miners to G7"));
        Assert.DoesNotContain(lines, l => l.Text.Contains("unavailable"));
        Assert.Contains(lines, l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("WING:?"));
    }

    [Theory]
    [InlineData((short)0, "command")]
    [InlineData((short)3, "escort")]
    [InlineData((short)9, "echo")]
    public void Build_shows_authoritative_wing_name(short wing, string name)
    {
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>
            { ["Amy"] = new(301, 1, -1, false, false, WingId: wing) };
        var lines = TeamPaneLayout.Build([E("Amy", 0)], Ranks, ships, null, null, null, false, "Amy");
        var pilot = Assert.Single(lines, l => l.Kind == TeamPaneLayout.LineKind.Pilot);
        Assert.Contains("WING:" + name, pilot.Text);
        Assert.True(pilot.Highlight);
    }

    /// <summary>Ballot is omitted when not a team leader.</summary>
    [Fact]
    public void Build_hides_ballot_for_non_leaders()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("Amy", 0) }, Ranks,
            new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, null, activeBallot: "some", isLeader: false, "Amy");
        Assert.DoesNotContain(lines, l => l.Kind == TeamPaneLayout.LineKind.Ballot);
    }

    /// <summary>⭐ Guard: every drawn line must be pure ASCII 32..126 (the HUD
    /// spritefonts bake ASCII only; a non-ASCII char crashes MeasureString at
    /// draw time, as a hardcoded em-dash in the wing note did). This test pins
    /// that the layout never emits an unresolvable character.</summary>
    [Fact]
    public void Build_output_is_always_pure_ascii()
    {
        var lines = TeamPaneLayout.Build(
            new[] { E("H\u00e9llo\u2191", 0, kills: 2, deaths: 1, rank: 9), E("Bob", 1) },
            new[] { R(9, "Inter. 4") },
            new Dictionary<string, TeamPaneLayout.ShipBrief> { ["H\u00e9llo\u2191"] = Fly("3") },
            null, myMoney: 100, activeBallot: "Vote \u2192 move", isLeader: true, "Bob");
        foreach (var ln in lines)
        {
            Assert.All(ln.Text, c => Assert.True(c >= 32 && c <= 126,
                $"non-ASCII char U+{(int)c:X4} in line: {ln.Text}"));
        }
    }
}
