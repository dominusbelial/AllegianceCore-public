using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P752 — pure, headless-testable layout of the game-over screen's two result
/// tables, the port of native <c>GameOverScreen</c>'s sortable lists
/// (<c>WinTrek/gameoverscreen.cpp:840-1009</c>):
/// <list type="bullet">
/// <item><b>Side totals table</b> (native <c>SidePainter</c>, :231-301) —
/// position, side name in the side's colour, baseKills, baseCaptures, flags,
/// artifacts, kills, deaths — one row per <c>SideEndgameInfo</c> from the
/// <c>GAME_OVER(62)</c> snapshot.</item>
/// <item><b>Player results table</b> (native <c>PlayerPainter</c>, :84-190) —
/// position, name in the side's colour (bold for the commander), rank name,
/// baseKills, baseCaptures, score, assists, kills, deaths-or-ejections
/// (switched by the mission's <c>bEjectPods</c>), and time played as
/// <c>h:mm</c>.</item>
/// <item><b>Column sorting and reversal</b> (native <c>OnButtonBarStats</c> /
/// <c>OnButtonBarStatsSide</c>, :840-874,922-956) — clicking a column header
/// sorts by it; clicking the same header again reverses; clicking the position
/// column re-applies the current sort. Native's list comparators return true
/// when the first value should sort AFTER the second (<c>zlib/tlist.h</c>,
/// <c>InsertSorted</c>), so a fresh column click is ASCENDING (low values on
/// top — the exact "WLP sort with low scores on top" startup comment) and the
/// second click flips it to descending (<c>ReverseCompare</c>).</item>
/// </list>
/// No MonoGame types live here — the renderer only paints the rows.
/// </summary>
public static class GameOverTablesLayout
{
    // ── Columns ─────────────────────────────────────────────────────────────

    /// <summary>The player table's sortable columns, in native button-bar
    /// order (position 0 is the position column; 1..9 are the data columns,
    /// gameoverscreen.cpp:922-956).</summary>
    public enum PlayerColumn { Position, Name, Rank, BaseKills, BaseCaptures, Score, Assists, Kills, Deaths, TimePlayed }

    /// <summary>The side table's sortable columns, in native button-bar order
    /// (gameoverscreen.cpp:840-874).</summary>
    public enum SideColumn { Position, Name, BaseKills, BaseCaptures, Flags, Artifacts, Kills, Deaths }

    /// <summary>One sort state: the column plus whether it is reversed
    /// (descending). <c>Reverse</c> is native's <c>m_bPlayerSortReverse</c> /
    /// <c>m_bSideSortReverse</c>: a fresh column click sets it false
    /// (ascending, low values on top) and the second click sets it true
    /// (descending, high values on top).</summary>
    public readonly record struct PlayerSort(PlayerColumn Column, bool Reverse);
    public readonly record struct SideSort(SideColumn Column, bool Reverse);

    // ── Rows ────────────────────────────────────────────────────────────────

    /// <summary>One player-table row, pre-resolved for display: rank name and
    /// time-played text are already formatted; <see cref="Commander"/> marks
    /// the side's leader (native draws the name bold, gameoverscreen.cpp:126).</summary>
    public sealed record PlayerRow(
        int Position, string Name, short SideId, bool Commander, string RankName,
        short BaseKills, short BaseCaptures, int Score, short Assists, short Kills,
        short DeathsOrEjections, string TimePlayed);

    /// <summary>One side-table row. <see cref="SideId"/> is the side's id (the
    /// <c>rgSides</c> array index in the GAME_OVER snapshot — the wire record
    /// itself does not repeat it), used to resolve the side's colour.</summary>
    public sealed record SideRow(
        int Position, short SideId, string Name, short CivId,
        short BaseKills, short BaseCaptures, short Flags, short Artifacts, short Kills, short Deaths);

    /// <summary>Native's time-played display: whole hours then <c>:mm</c>
    /// with a leading zero, seconds dropped (gameoverscreen.cpp:168-176).</summary>
    public static string FormatTimePlayed(float seconds)
    {
        if (float.IsNaN(seconds) || seconds < 0f) seconds = 0f;
        int t = (int)seconds;
        int hours = t / (60 * 60);
        int minutes = t / 60 - hours * 60;
        return hours + (minutes > 9 ? ":" : ":0") + minutes;
    }

    // ── Sort click semantics ────────────────────────────────────────────────

    /// <summary>Native <c>OnButtonBarStats</c>'s radio-button behaviour
    /// (gameoverscreen.cpp:922-956): clicking a data column sorts by it
    /// ascending (low values on top); clicking the SAME column again reverses
    /// it (descending); clicking the position ("#") column is folded into a
    /// click on the CURRENT sort column, so it <b>toggles</b> that column's
    /// direction (native resolves iButton to the checked column and then runs
    /// the normal radio-button/checked-state logic).</summary>
    public static PlayerSort ClickPlayerColumn(PlayerSort current, PlayerColumn clicked)
        => clicked == current.Column || clicked == PlayerColumn.Position
            ? current with { Reverse = !current.Reverse }
            : new PlayerSort(clicked, Reverse: false);

    /// <summary>Native <c>OnButtonBarStatsSide</c>'s radio-button behaviour
    /// (gameoverscreen.cpp:840-874), same fresh-ascending/reverse rule as the
    /// player bar — the "#" column toggles the current column.</summary>
    public static SideSort ClickSideColumn(SideSort current, SideColumn clicked)
        => clicked == current.Column || clicked == SideColumn.Position
            ? current with { Reverse = !current.Reverse }
            : new SideSort(clicked, Reverse: false);

    // ── Table builds ────────────────────────────────────────────────────────

    /// <summary>
    /// Build the side-totals table rows. Native sorts the side list with the
    /// selected comparator (<c>RefreshSides</c>, gameoverscreen.cpp:877-919);
    /// every comparator is ascending (low values first) and the reverse flag
    /// flips it to descending. Positions are the 1-based indices in the sorted
    /// order.
    /// </summary>
    public static IReadOnlyList<SideRow> BuildSides(
        IReadOnlyList<SideEndgameInfo> sides, int numSides, SideSort sort)
    {
        // The GAME_OVER rgSides array is indexed by SideID (Messages.h:469-477),
        // so the array position IS the side id.
        var live = sides.Take(Math.Max(0, numSides))
            .Select((s, i) => (SideId: (short)i, Side: s));
        IEnumerable<(short SideId, SideEndgameInfo Side)> ordered = sort.Column switch
        {
            SideColumn.Name => sort.Reverse
                ? live.OrderByDescending(t => t.Side.SideName, StringComparer.OrdinalIgnoreCase)
                : live.OrderBy(t => t.Side.SideName, StringComparer.OrdinalIgnoreCase),
            SideColumn.BaseKills => Key(t => t.Side.CBaseKills),
            SideColumn.BaseCaptures => Key(t => t.Side.CBaseCaptures),
            SideColumn.Flags => Key(t => t.Side.CFlags),
            SideColumn.Artifacts => Key(t => t.Side.CArtifacts),
            SideColumn.Kills => Key(t => t.Side.CKills),
            SideColumn.Deaths => Key(t => t.Side.CDeaths),
            _ => live,
        };
        return ordered.Select((t, i) => new SideRow(i + 1, t.SideId, t.Side.SideName, t.Side.CivId,
            t.Side.CBaseKills, t.Side.CBaseCaptures, t.Side.CFlags, t.Side.CArtifacts,
            t.Side.CKills, t.Side.CDeaths)).ToArray();

        IEnumerable<(short SideId, SideEndgameInfo Side)> Key(Func<(short SideId, SideEndgameInfo Side), short> key) => sort.Reverse
            ? live.OrderByDescending(key)
            : live.OrderBy(key);
    }

    /// <summary>
    /// Build the player-results table rows. <paramref name="rankName"/> and
    /// <paramref name="civOf"/> resolve display data the wire record does not
    /// carry (the rank table and each side's civilization for the rank
    /// tie-break); <paramref name="commanders"/> names the players who were
    /// their side's leader (native bolds commanders,
    /// gameoverscreen.cpp:126-133). <paramref name="ejectPods"/> switches the
    /// last counter column between deaths and ejections exactly like native
    /// (<c>bDeaths = !trekClient.GetGameoverEjectPods()</c>,
    /// gameoverscreen.cpp:300-301 and the painter's :168-184 branch).
    /// </summary>
    public static IReadOnlyList<PlayerRow> BuildPlayers(
        IReadOnlyList<GameOverPlayerEntry> players,
        Func<short, string> rankName,
        Func<short, short> civOf,
        IReadOnlySet<string> commanders,
        bool ejectPods,
        PlayerSort sort)
    {
        static short DeathsOrEjections(GameOverPlayerEntry p, bool ej) => ej ? p.Ejections : p.Deaths;

        IEnumerable<GameOverPlayerEntry> ordered = sort.Column switch
        {
            PlayerColumn.Name => sort.Reverse
                ? players.OrderByDescending(p => p.CharacterName, StringComparer.OrdinalIgnoreCase)
                : players.OrderBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase),
            // Native SortRank: rank ascending, ties broken by the endgame
            // side's civilization ascending (gameoverscreen.cpp:752-762); the
            // reverse flag flips both to descending.
            PlayerColumn.Rank => sort.Reverse
                ? players.OrderByDescending(p => p.Rank).ThenByDescending(p => civOf(p.SideId))
                : players.OrderBy(p => p.Rank).ThenBy(p => civOf(p.SideId)),
            PlayerColumn.BaseKills => Key(p => p.BaseKills),
            PlayerColumn.BaseCaptures => Key(p => p.BaseCaptures),
            PlayerColumn.Score => KeyF(p => p.Score),
            PlayerColumn.Assists => Key(p => p.Assists),
            PlayerColumn.Kills => Key(p => p.Kills),
            PlayerColumn.Deaths => Key(p => DeathsOrEjections(p, ejectPods)),
            PlayerColumn.TimePlayed => KeyF(p => p.TimePlayed),
            _ => players,
        };
        return ordered.Select((p, i) => new PlayerRow(
            Position: i + 1,
            Name: p.CharacterName,
            SideId: p.SideId,
            Commander: commanders.Contains(p.CharacterName),
            RankName: rankName(p.Rank),
            BaseKills: p.BaseKills,
            BaseCaptures: p.BaseCaptures,
            Score: (int)p.Score,
            Assists: p.Assists,
            Kills: p.Kills,
            DeathsOrEjections: DeathsOrEjections(p, ejectPods),
            TimePlayed: FormatTimePlayed(p.TimePlayed))).ToArray();

        IEnumerable<GameOverPlayerEntry> Key(Func<GameOverPlayerEntry, short> key) => sort.Reverse
            ? players.OrderByDescending(key)
            : players.OrderBy(key);
        IEnumerable<GameOverPlayerEntry> KeyF(Func<GameOverPlayerEntry, float> key) => sort.Reverse
            ? players.OrderByDescending(key)
            : players.OrderBy(key);
    }
}
