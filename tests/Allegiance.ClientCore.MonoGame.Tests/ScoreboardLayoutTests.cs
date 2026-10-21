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
/// ordering (P774b: side asc, kills desc, deaths asc, name ordinal), in-side
/// position numbering, the in-mission column set (RANK/name/K/D only),
/// rank-name resolution from the RANK_INFO(13) table, ASCII-safety and column
/// alignment. No graphics device — same discipline as
/// <see cref="HelpOverlayTests"/> / <see cref="StarmapLayoutTests"/>.
/// </summary>
public class ScoreboardLayoutTests
{
    private static ScoreRosterEntry E(string name, short side,
        short kills = 0, short deaths = 0, short rank = 0, short ejections = 0) =>
        new(name, side, kills, deaths, ejections, rank);

    private static RankInfoEntry R(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    /// <summary>⭐ The leaderboard orders by side (ascending), then kills
    /// (descending), deaths (ascending), name (ordinal) — the in-mission
    /// counters native actually displays there (clintlib.h:346-361). P774b:
    /// score has no native in-mission source (GAME_OVER_PLAYERS,
    /// gameoverscreen.cpp:158), so the old score-descending order is gone.
    /// Rows are numbered #1.. within their own side by the same order.</summary>
    [Fact]
    public void Build_orders_by_side_then_kills_desc_deaths_asc_name()
    {
        var rows = ScoreboardLayout.Build(new[]
        {
            E("Ann", 0, kills: 3, deaths: 2),
            E("Bob", 0, kills: 1, deaths: 0),
            E("Cid", 0, kills: 3, deaths: 1),
            E("Zed", 1, kills: 9, deaths: 9),
        }, Array.Empty<RankInfoEntry>());

        // Side 0 first: Cid (3k/1d) before Ann (3k/2d) — deaths ascending
        // breaks the kill tie — then Bob (1k). Side 1's Zed follows.
        Assert.Equal(new[] { "Cid", "Ann", "Bob", "Zed" }, rows.Select(r => r.Name).ToArray());
        // In-side positions: Cid=1, Ann=2, Bob=3 (side 0); Zed=1 (side 1).
        Assert.Equal(new[] { "1", "2", "3", "1" }, rows.Select(r => r.Position).ToArray());
    }

    /// <summary>⭐ P774b — the in-mission header row is exactly the six columns
    /// with a native in-mission source: position, name, side, rank, kills,
    /// deaths (clintlib.h:346-361). SCORE/COMBAT/BK/BC have no native
    /// in-mission source — score and base kills/captures are game-over-only
    /// (GAME_OVER_PLAYERS, gameoverscreen.cpp:146-158), combat rating is never
    /// shown (igc.h:5981 commented out) — so their header constants must not
    /// exist for the renderer to draw.</summary>
    [Fact]
    public void In_mission_header_row_is_position_name_side_rank_k_d_only()
    {
        var headers = typeof(ScoreboardLayout.Row)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.Name.StartsWith("Header", StringComparison.Ordinal) && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "#", "D", "K", "NAME", "RANK", "SIDE" }, headers);
        Assert.Equal(6, ScoreboardLayout.Row.ColumnCount);
        Assert.Equal(new[] { "#", "NAME", "SIDE", "RANK", "K", "D" }, ScoreboardLayout.Row.HeaderCells);
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
            E("NewPilot", 0, rank: 0),
            E("Vet", 0, rank: 22),
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
            E("H\u00e9llo\u2191", 0),
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
        var rows = ScoreboardLayout.Build(new[] { E("Ace", 3) },
            Array.Empty<RankInfoEntry>(), side => side != 3);
        Assert.Equal("S3(DESTROYED)", rows[0].SideLabel);

        var active = ScoreboardLayout.Build(new[] { E("Ace", 3) },
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
            new ScoreboardLayout.Row("#", "Ace", "S3", "Newbie", "1", "0"),
            new ScoreboardLayout.Row("12", "A Very Long Pilot Name", "S12", "Veteran 7", "123", "7"),
        };
        var w = ScoreboardLayout.ColumnWidths(rows, s => s.Length);   // monospaced stub: width = char count

        Assert.Equal(Math.Max("12".Length, "#".Length), (int)w.Position);
        Assert.Equal(Math.Max("A Very Long Pilot Name".Length, "NAME".Length), (int)w.Name);
        Assert.Equal(Math.Max("S12".Length, "SIDE".Length), (int)w.Side);
        Assert.Equal(Math.Max("Veteran 7".Length, "RANK".Length), (int)w.Rank);
        Assert.Equal(Math.Max("123".Length, "K".Length), (int)w.Kills);
        Assert.Equal(Math.Max("7".Length, "D".Length), (int)w.Deaths);
    }
}
