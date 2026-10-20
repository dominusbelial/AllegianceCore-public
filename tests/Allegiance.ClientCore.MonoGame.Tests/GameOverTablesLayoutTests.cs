using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P752 — pins the game-over screen's two result tables (native
/// gameoverscreen.cpp:840-1009): the sort click semantics (radio button +
/// reverse), every comparator's order, the deaths/ejections eject-pods switch,
/// the h:mm time format and the 1-based position column.
/// </summary>
public class GameOverTablesLayoutTests
{
    private static readonly GameOverPlayerEntry[] Players =
    [
        new("ann", 0, 4, 100f, Kills: 1, Deaths: 2, Ejections: 7, BaseKills: 0, BaseCaptures: 0, Assists: 0, TimePlayed: 60f),
        new("Bob", 1, 9, 900f, Kills: 5, Deaths: 1, Ejections: 3, BaseKills: 2, BaseCaptures: 1, Assists: 4, TimePlayed: 7265f),
        new("carol", 0, 9, 900f, Kills: 5, Deaths: 0, Ejections: 9, BaseKills: 2, BaseCaptures: 1, Assists: 4, TimePlayed: 60f),
    ];

    private static IReadOnlyList<GameOverTablesLayout.PlayerRow> Build(
        GameOverTablesLayout.PlayerSort sort, bool ejectPods = false,
        short? civ0 = 1, short? civ1 = 3) =>
        GameOverTablesLayout.BuildPlayers(Players, _ => "Cadet",
            side => side == 0 ? civ0!.Value : civ1!.Value, new HashSet<string>(), ejectPods, sort);

    // ── sort click semantics (OnButtonBarStats, gameoverscreen.cpp:922-956) ──

    [Fact]
    public void ClickingAColumn_SortsAscending_ClickingAgainReverses()
    {
        var sort = new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Score, Reverse: true);
        // A fresh click on a NEW column: ascending (native's low-on-top first
        // click), Reverse = false.
        sort = GameOverTablesLayout.ClickPlayerColumn(sort, GameOverTablesLayout.PlayerColumn.Kills);
        Assert.Equal(new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Kills, false), sort);
        // Clicking the SAME column again: reversed (descending).
        sort = GameOverTablesLayout.ClickPlayerColumn(sort, GameOverTablesLayout.PlayerColumn.Kills);
        Assert.Equal(new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Kills, true), sort);
        // Clicking yet another column: back to ascending.
        sort = GameOverTablesLayout.ClickPlayerColumn(sort, GameOverTablesLayout.PlayerColumn.Name);
        Assert.Equal(new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Name, false), sort);
    }

    [Fact]
    public void ClickingThePositionColumn_TogglesTheCurrentSort()
    {
        // Native OnButtonBarStats/OnButtonBarStatsSide (gameoverscreen.cpp:
        // 840-874, 922-956): a "#" click becomes a click on the current sort
        // column, which TOGGLES its direction — not a no-op re-apply.
        var sort = new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Kills, true);
        Assert.Equal(new GameOverTablesLayout.PlayerSort(GameOverTablesLayout.PlayerColumn.Kills, false),
            GameOverTablesLayout.ClickPlayerColumn(sort, GameOverTablesLayout.PlayerColumn.Position));

        var side = new GameOverTablesLayout.SideSort(GameOverTablesLayout.SideColumn.Flags, true);
        Assert.Equal(new GameOverTablesLayout.SideSort(GameOverTablesLayout.SideColumn.Flags, false),
            GameOverTablesLayout.ClickSideColumn(side, GameOverTablesLayout.SideColumn.Position));

        // Toggling back restores the original direction.
        Assert.Equal(new GameOverTablesLayout.SideSort(GameOverTablesLayout.SideColumn.Flags, true),
            GameOverTablesLayout.ClickSideColumn(
                GameOverTablesLayout.ClickSideColumn(side, GameOverTablesLayout.SideColumn.Position),
                GameOverTablesLayout.SideColumn.Position));
    }

    // ── comparators ──────────────────────────────────────────────────────────

    [Fact]
    public void NameSort_IsCaseInsensitive_AscendingThenDescending()
    {
        // _stricmp: a fresh sort is ascending (low on top, zlib/tlist.h), the
        // reverse flag flips it to descending.
        var asc = Build(new(GameOverTablesLayout.PlayerColumn.Name, false));
        Assert.Equal(["ann", "Bob", "carol"], asc.Select(r => r.Name));
        var desc = Build(new(GameOverTablesLayout.PlayerColumn.Name, true));
        Assert.Equal(["carol", "Bob", "ann"], desc.Select(r => r.Name));
    }

    [Fact]
    public void RankSort_TiesBreakOnCiv_AscendingThenDescending()
    {
        // Bob (side 1, civ 3) and carol (side 0, civ 1) share rank 9; the tie
        // breaks on the endgame side's civilization, ascending
        // (gameoverscreen.cpp:752-762).
        Assert.Equal(["ann", "carol", "Bob"], Build(new(GameOverTablesLayout.PlayerColumn.Rank, false)).Select(r => r.Name));
        Assert.Equal(["Bob", "carol", "ann"], Build(new(GameOverTablesLayout.PlayerColumn.Rank, true)).Select(r => r.Name));
    }

    [Fact]
    public void ScoreKillsAssistsTimePlayed_AscendingThenDescending()
    {
        Assert.Equal(["ann", "Bob", "carol"], Build(new(GameOverTablesLayout.PlayerColumn.Score, false)).Select(r => r.Name));
        // Score ties (Bob 900, carol 900) keep arrival order (stable).
        Assert.Equal(["Bob", "carol", "ann"], Build(new(GameOverTablesLayout.PlayerColumn.Score, true)).Select(r => r.Name));
        Assert.Equal(["ann", "Bob", "carol"], Build(new(GameOverTablesLayout.PlayerColumn.Kills, false)).Select(r => r.Name));
        Assert.Equal(["Bob", "carol", "ann"], Build(new(GameOverTablesLayout.PlayerColumn.Kills, true)).Select(r => r.Name));
        Assert.Equal(["ann", "Bob", "carol"], Build(new(GameOverTablesLayout.PlayerColumn.Assists, false)).Select(r => r.Name));
        // Time played ascending: Bob 7265 s last; ann and carol tie at 60 s and
        // keep arrival order (stable, like native's insertion sort).
        Assert.Equal(["ann", "carol", "Bob"], Build(new(GameOverTablesLayout.PlayerColumn.TimePlayed, false)).Select(r => r.Name));
    }

    [Fact]
    public void DeathsColumn_FollowsTheEjectPodsSwitch()
    {
        // Deaths sort (ascending): without eject pods the death counter decides
        // (carol 0 deaths first); with eject pods the ejection counter decides
        // (Bob 3 first) — native SortDeaths, gameoverscreen.cpp:781-790.
        Assert.Equal(["carol", "Bob", "ann"], Build(new(GameOverTablesLayout.PlayerColumn.Deaths, false)).Select(r => r.Name));
        Assert.Equal(["Bob", "ann", "carol"], Build(new(GameOverTablesLayout.PlayerColumn.Deaths, false), ejectPods: true).Select(r => r.Name));
        // The displayed counter switches too (PlayerPainter :168-184).
        Assert.Equal(0, Build(new(GameOverTablesLayout.PlayerColumn.Score, false))[2].DeathsOrEjections);
        Assert.Equal(9, Build(new(GameOverTablesLayout.PlayerColumn.Score, false), ejectPods: true)[2].DeathsOrEjections);
    }

    // ── presentation ─────────────────────────────────────────────────────────

    [Fact]
    public void Positions_AreOneBasedInTheSortedOrder()
    {
        var rows = Build(new(GameOverTablesLayout.PlayerColumn.Score, false));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Position));
    }

    [Fact]
    public void TimePlayed_FormatsNativeHoursAndZeroPaddedMinutes()
    {
        Assert.Equal("0:00", GameOverTablesLayout.FormatTimePlayed(0f));
        // Seconds are dropped entirely: 59.9 s is still minute zero.
        Assert.Equal("0:00", GameOverTablesLayout.FormatTimePlayed(59.9f));
        Assert.Equal("0:59", GameOverTablesLayout.FormatTimePlayed(3599f));
        Assert.Equal("1:01", GameOverTablesLayout.FormatTimePlayed(3660f));
        Assert.Equal("2:01", GameOverTablesLayout.FormatTimePlayed(7265f));   // 2h 1m 5s -> seconds dropped
        Assert.Equal("0:00", GameOverTablesLayout.FormatTimePlayed(-5f));
    }

    [Fact]
    public void CommanderAndRankName_RideTheRows()
    {
        var commanders = new HashSet<string> { "Bob" };
        var rows = GameOverTablesLayout.BuildPlayers(Players, r => r == 9 ? "Admiral" : "Cadet",
            _ => (short)1, commanders, ejectPods: false,
            new(GameOverTablesLayout.PlayerColumn.Score, false));
        Assert.True(rows.First(r => r.Name == "Bob").Commander);
        Assert.False(rows.First(r => r.Name == "ann").Commander);
        Assert.Equal("Admiral", rows.First(r => r.Name == "Bob").RankName);
        Assert.Equal("Cadet", rows.First(r => r.Name == "ann").RankName);
    }

    // ── side totals table ────────────────────────────────────────────────────

    private static SideEndgameInfo Side(short kills, short deaths, short bk, short bc, short flags, short art, string name = "Alpha")
        => new(name, 1, 1f, 1f, 1f, 1f, kills, deaths, 0, bk, bc, flags, art);

    [Fact]
    public void SideTable_SortsByEveryCounter_AscendingThenDescending()
    {
        var sides = new List<SideEndgameInfo> { Side(3, 0, 0, 0, 0, 0, "Zeta"), Side(1, 9, 5, 0, 0, 0, "Alpha") };
        Assert.Equal(["Alpha", "Zeta"],
            GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.Kills, false)).Select(r => r.Name));
        Assert.Equal(["Zeta", "Alpha"],
            GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.Deaths, false)).Select(r => r.Name));
        Assert.Equal(["Zeta", "Alpha"],
            GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.BaseKills, false)).Select(r => r.Name));
        Assert.Equal(["Alpha", "Zeta"],
            GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.Name, false)).Select(r => r.Name));
        // Reversed name sort is descending.
        Assert.Equal(["Zeta", "Alpha"],
            GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.Name, true)).Select(r => r.Name));
    }

    [Fact]
    public void SideTable_CarriesSideIdAndPositions_NumSidesLimitsRows()
    {
        var sides = new List<SideEndgameInfo> { Side(1, 0, 0, 0, 0, 0, "A"), Side(2, 0, 0, 0, 0, 0, "B"), Side(3, 0, 0, 0, 0, 0, "C") };
        var rows = GameOverTablesLayout.BuildSides(sides, 2, new(GameOverTablesLayout.SideColumn.Kills, false));
        // Only the first NumSides entries exist on the wire snapshot.
        Assert.Equal(2, rows.Count);
        Assert.Equal(["A", "B"], rows.Select(r => r.Name));
        Assert.Equal([1, 2], rows.Select(r => r.Position));
        // Side ids are the array indices (rgSides is indexed by SideID).
        Assert.Equal([0, 1], rows.Select(r => r.SideId));
    }
}
