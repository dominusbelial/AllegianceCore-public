using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P282 ready-check layout — every emitted row is
/// printable ASCII 32..126 (the HUD spritefonts bake ASCII only) and encodes
/// the local ready toggle, the live same-side roster of ready states, the
/// per-side TEAM_READY aggregate, and the commander team-ready hint.
/// </summary>
public class ReadyCheckLayoutTests
{
    private static readonly IReadOnlyList<ReadyCheckLayout.PlayerRow> TwoPlayers = new[]
    {
        new ReadyCheckLayout.PlayerRow("you", true, true),
        new ReadyCheckLayout.PlayerRow("Matilda", false, false),
    };

    private static void AssertPureAscii(IReadOnlyList<ReadyCheckLayout.Row> rows)
    {
        foreach (var r in rows)
            foreach (var ch in r.Text)
                Assert.InRange(ch, '\u0020', '\u007e');
    }

    [Fact]
    public void Build_yields_title_hint_aggregate_and_roster_rows()
    {
        var rows = ReadyCheckLayout.Build("Bios", ownReady: true, TwoPlayers, sideAggregateReady: false, forcedReady: false, canForceTeamReady: false);
        AssertPureAscii(rows);

        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("READY CHECK", joined);
        Assert.Contains("Y TOGGLE READY", joined);
        Assert.Contains("TEAM Bios  READY: NO", joined);
        Assert.Contains("you", joined);
        Assert.Contains("Matilda", joined);
        Assert.Contains("[READY]", joined);   // self row tagged ready
        Assert.Contains("[WAIT ]", joined);   // unready teammate tagged wait

        // The self row is styled as highlighted, the teammate not.
        var selfRow = rows.Single(r => r.Text.Contains("(you)"));
        Assert.True(selfRow.Highlight);
    }

    [Fact]
    public void Aggregates_yes_and_forced_flag_are_reflected()
    {
        var rows = ReadyCheckLayout.Build("IronCo", ownReady: true, TwoPlayers, sideAggregateReady: true, forcedReady: true, canForceTeamReady: false);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("TEAM IronCo  READY: YES", joined);
        Assert.Contains("(FORCED)", joined);
    }

    [Fact]
    public void Aggregate_null_shows_dash_before_first_team_ready_arrives()
    {
        var rows = ReadyCheckLayout.Build("Bios", ownReady: false, TwoPlayers, sideAggregateReady: null, forcedReady: false, canForceTeamReady: false);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("READY: --", joined);
        Assert.Contains("NOT READY", joined);
    }

    [Fact]
    public void Force_ready_hint_shown_only_when_commander_can_toggle()
    {
        var owner = ReadyCheckLayout.Build("Bios", false, TwoPlayers, false, false, canForceTeamReady: true);
        Assert.Contains("O TOGGLE TEAM READY", string.Join("\n", owner.Select(r => r.Text)));

        var nonOwner = ReadyCheckLayout.Build("Bios", false, TwoPlayers, false, false, canForceTeamReady: false);
        Assert.DoesNotContain("O TOGGLE TEAM READY", string.Join("\n", nonOwner.Select(r => r.Text)));
    }

    [Fact]
    public void Empty_roster_shows_wait_message()
    {
        var rows = ReadyCheckLayout.Build("Bios", false, new List<ReadyCheckLayout.PlayerRow>(), null, false, false);
        AssertPureAscii(rows);
        Assert.Contains("WAITING FOR SAME-SIDE ROSTER", string.Join("\n", rows.Select(r => r.Text)));
    }

    // ── P522 — the countdown hold (STAGE_STARTING) ──────────────────────────

    /// <summary>⭐ While the mission is STAGE_STARTING the ship is held (not
    /// flying); the ready-check screen surfaces the launch countdown loudly.
    /// Passing a non-null countdown emits a prominent LAUNCHING IN row.</summary>
    [Fact]
    public void LaunchCountdown_Emits_LAUNCHING_IN_Row()
    {
        var rows = ReadyCheckLayout.Build("Bios", true, TwoPlayers, true, false, false, launchCountdown: 7);
        AssertPureAscii(rows);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("LAUNCHING IN 7", joined);

        // And it is rendered as a highlighted emphasis row.
        var cdRow = rows.Single(r => r.Text.StartsWith("LAUNCHING IN"));
        Assert.True(cdRow.Highlight);
    }

    /// <summary>Before the countdown begins (null countdown) no LAUNCHING IN
    /// row is shown — the roster stays the whole screen.</summary>
    [Fact]
    public void LaunchCountdown_Null_Omits_LAUNCHING_IN_Row()
    {
        var rows = ReadyCheckLayout.Build("Bios", true, TwoPlayers, true, false, false, launchCountdown: null);
        Assert.DoesNotContain("LAUNCHING IN", string.Join("\n", rows.Select(r => r.Text)));
    }
}
