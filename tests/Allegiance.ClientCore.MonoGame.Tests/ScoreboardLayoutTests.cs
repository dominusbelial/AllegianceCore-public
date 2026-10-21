using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P639 LEADERBOARD layout (the upgraded scoreboard):
/// ordering (side asc, score desc), in-side position numbering, rank-name
/// resolution from the RANK_INFO(13) table, ASCII-safety and column alignment.
/// No graphics device — same discipline as <see cref="HelpOverlayTests"/> /
/// <see cref="StarmapLayoutTests"/>.
/// </summary>
public class ScoreboardLayoutTests
{
    private static ScoreRosterEntry E(string name, short side, float score,
        short kills = 0, short deaths = 0, short rank = 0, float combat = 0f) =>
        new(name, side, kills, deaths, rank, score, combat);

    private static RankInfoEntry R(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    /// <summary>⭐ The leaderboard orders by side (ascending) then overall
    /// rating/score (descending) — native's default lbSortScore basis
    /// (leaderboardscreen.cpp:54-67,669-671) — so a team's best-rated pilot sits
    /// on top, numbered #1 within their side.</summary>
    [Fact]
    public void Build_orders_by_side_then_score_desc_and_numbers_positions()
    {
        var rows = ScoreboardLayout.Build(new[]
        {
            E("Low", 0, 100f),
            E("Top", 0, 9900f),
            E("Mid", 1, 950f),
            E("Hi", 1, 9700f),
        }, Array.Empty<RankInfoEntry>());

        Assert.Equal(new[] { "Top", "Low", "Hi", "Mid" }, rows.Select(r => r.Name).ToArray());
        // In-side positions: Top=1, Low=2 (side 0); Hi=1, Mid=2 (side 1).
        Assert.Equal(new[] { "1", "2", "1", "2" }, rows.Select(r => r.Position).ToArray());
    }

    /// <summary>⭐ Rank names are resolved from the server's RANK_INFO(13) table
    /// via RankNameLookup (native BaseClient::LookupRankName, clintlib.cpp:2670),
    /// turning a raw Rank id into the displayed rank.</summary>
    [Fact]
    public void Build_resolves_rank_names_from_table()
    {
        var table = new[] { R(0, "Newbie"), R(9, "Inter. 4"), R(22, "Expert 1") };
        var rows = ScoreboardLayout.Build(new[]
        {
            E("NewPilot", 0, 5f, rank: 0),
            E("Vet", 0, 8800f, rank: 22),
        }, table);

        Assert.Equal("Newbie", rows.First(r => r.Name == "NewPilot").RankName);
        Assert.Equal("Expert 1", rows.First(r => r.Name == "Vet").RankName);
    }

    /// <summary>⭐ Every cell is pure ASCII 32..126 so the spritefonts (which bake
    /// ASCII only) never throw on a hostile/mangled name.</summary>
    [Fact]
    public void Build_scrubs_non_ascii_from_names()
    {
        var rows = ScoreboardLayout.Build(new[]
        {
            E("H\u00e9llo\u2191", 0, 5f),
        }, Array.Empty<RankInfoEntry>());

        var name = rows[0].Name;
        Assert.DoesNotContain(name, c => c < 32 || c > 126);
        // The latin-1 0xE9 arrived as a real é; it must be scrubbed to a space.
        Assert.DoesNotContain('\u00e9', name);
    }

    /// <summary>The side renders as its numeric id; an inactive (eliminated)
    /// side is marked "(DESTROYED)" (native teamscreen.cpp:318).</summary>
    [Fact]
    public void Build_renders_side_label_and_destroyed_marker()
    {
        var rows = ScoreboardLayout.Build(new[] { E("Ace", 3, 5f) },
            Array.Empty<RankInfoEntry>(), side => side != 3);
        Assert.Equal("S3(DESTROYED)", rows[0].SideLabel);

        var active = ScoreboardLayout.Build(new[] { E("Ace", 3, 5f) },
            Array.Empty<RankInfoEntry>());
        Assert.Equal("S3", active[0].SideLabel);
    }

    /// <summary>Column widths never collapse below their header label and expand
    /// to fit the longest cell — the renderer's alignment guarantee.</summary>
    [Fact]
    public void ColumnWidths_accommodate_longest_cells_and_headers()
    {
        var rows = new[]
        {
            new ScoreboardLayout.Row("#", "Ace", "S3", "Newbie", "5", "1.0", "1", "0", "0", "0"),
            new ScoreboardLayout.Row("12", "A Very Long Pilot Name", "S12", "Veteran 7", "12345", "99.5", "123", "7", "2", "3"),
        };
        var w = ScoreboardLayout.ColumnWidths(rows, s => s.Length);   // monospaced stub: width = char count

        Assert.Equal(Math.Max("12".Length, "#".Length), (int)w.Position);
        Assert.Equal(Math.Max("A Very Long Pilot Name".Length, "NAME".Length), (int)w.Name);
        Assert.Equal(Math.Max("S12".Length, "SIDE".Length), (int)w.Side);
        Assert.Equal(Math.Max("Veteran 7".Length, "RANK".Length), (int)w.Rank);
        Assert.Equal(Math.Max("12345".Length, "SCORE".Length), (int)w.Score);
        Assert.Equal(Math.Max("99.5".Length, "COMBAT".Length), (int)w.Combat);
    }
}
