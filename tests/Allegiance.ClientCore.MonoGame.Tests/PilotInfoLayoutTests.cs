using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P275 — pure-logic tests for the Pilot Info layout (local-player extraction,
/// formatting and ASCII-safety). No graphics device — same discipline as
/// <see cref="ScoreboardLayoutTests"/> / <see cref="HelpOverlayTests"/>.
/// </summary>
public class PilotInfoLayoutTests
{
    private static ScoreRosterEntry E(string name, short side, short kills, short deaths,
        short rank = 0, float score = 0f, float combat = 0f) =>
        new(name, side, kills, deaths, rank, score, combat);

    /// <summary>⭐ The overlay extracts the local player's OWN row (matched by
    /// name) and reports all five live stats in order: kills, deaths, combat
    /// rating, rank, score.</summary>
    [Fact]
    public void Build_returns_local_players_own_stats_in_order()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("Ace", 0, 4, 2, 9, 900f, 310.4375f),
            E("Bee", 1, 1, 5, 3, 402f, 205.015625f),
        }, "Ace");

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("Ace", p.Name);
        Assert.Equal("S0", p.SideLabel);

        Assert.Equal(new[] { "KILLS", "DEATHS", "COMBAT RATING", "RANK", "SCORE" },
            p.Stats.Select(s => s.Label).ToArray());
        Assert.Equal("4", p.Stats[0].Value);      // kills
        Assert.Equal("2", p.Stats[1].Value);      // deaths
        Assert.Equal("310.4", p.Stats[2].Value);  // combat rating, F1
        Assert.Equal("9", p.Stats[3].Value);      // rank
        Assert.Equal("900", p.Stats[4].Value);    // score, F0
    }

    /// <summary>⭐ The overlay does NOT show another player's row — it matches
    /// strictly the local player's name, even when others have richer scores.</summary>
    [Fact]
    public void Build_does_not_confuse_another_player_for_self()
    {
        var info = PilotInfoLayout.Build(new[]
        {
            E("RichKiller", 0, 999, 888, 40, 5555f, 777.875f),
            E("Me", 0, 2, 3, 1, 210f, 312.5f),
        }, "Me");

        Assert.NotNull(info);
        var p = info.Value;   // nullable struct: unwrap after the NotNull assert
        Assert.Equal("2", p.Stats[0].Value);          // my kills, not RichKiller's
        Assert.Equal("3", p.Stats[1].Value);          // my deaths
        Assert.Equal("312.5", p.Stats[2].Value);      // my combat rating
        Assert.Equal("1", p.Stats[3].Value);          // my rank
        Assert.Equal("210", p.Stats[4].Value);        // my score
    }

    /// <summary>Returns null when the local player isn't in the roster yet (pre-
    /// join / no ScoreRosterUpdate received) — the renderer draws nothing rather
    /// than a placeholder.</summary>
    [Fact]
    public void Build_returns_null_when_self_not_yet_in_roster()
    {
        var info = PilotInfoLayout.Build(new[] { E("SomeoneElse", 0, 1, 1) }, "Me");
        Assert.Null(info);
    }

    /// <summary>Returns null when there's no known local player name (not yet
    /// assigned a ship / MyShipId unset).</summary>
    [Fact]
    public void Build_returns_null_when_no_self_name()
    {
        Assert.Null(PilotInfoLayout.Build(new[] { E("Me", 0, 1, 1) }, null));
        Assert.Null(PilotInfoLayout.Build([], "Me"));
    }

    /// <summary>A non-ASCII name is scrubbed to printable ASCII so the spritefonts
    /// never throw on a mangled/hostile name (same P214/p217 lesson).</summary>
    [Fact]
    public void Build_scrubs_non_ascii_from_name()
    {
        var info = PilotInfoLayout.Build(new[] { E("H\u00e9llo\u2191", 0, 1, 1) }, "H\u00e9llo\u2191");
        Assert.NotNull(info);
        Assert.DoesNotContain(info.Value.Name, c => c < 32 || c > 126);
        Assert.DoesNotContain('\u00e9', info.Value.Name);
    }
}
