using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P761 — pure model of the TEAM PANE's sort-header radio bar (native
/// <c>TeamPane::OnButtonBarImpl</c>, <c>WinTrek/teampane.cpp:1395-1456</c>): a
/// radio-button header where exactly ONE column is checked, each column maps to
/// a sort key, and choosing one refreshes the list (<c>OnTeamCombo</c>).
/// Columns 0..7 are name, rank, kills, sector, ship, wing, credits, objective —
/// the SAME eight native maps (status/deaths/turrets are commented out natively
/// and are NOT added). With no header clicked the list uses native's default
/// <c>m_sortPlayers = sortLeader</c> (teampane.cpp:547): commander first, then
/// humans, then drones (LeaderCompare, teampane.cpp:1471-1481).
///
/// <para><b>Comparator semantics.</b> Native's <c>TList::InsertSorted</c> treats
/// the comparator as "return true if the FIRST value should be AFTER the second"
/// (<c>zlib/tlist.h:246-266</c>), inserted stably. Each native comparator
/// (teampane.cpp:1482-1576) therefore maps to a composite stable ORDER-BY key
/// here and <see cref="Ordered"/> re-sorts with a stable sort, so ties keep
/// their previous (insertion) order exactly like native's stable insertion
/// sort. Expressed keys:</para>
/// <list type="bullet">
/// <item>name — humans before drones, then case-insensitive name ascending
/// (PlayerNameCompare, teampane.cpp:1490-1502);</item>
/// <item>rank — rank value DESCENDING (PlayerRankCompare:
/// <c>p1-&gt;Rank() &lt; p2-&gt;Rank()</c> → p1 after, teampane.cpp:1536-1542);</item>
/// <item>kills / credits — ascending (PlayerKillsCompare / PlayerMoneyCompare
/// return <c>p1 &gt; p2</c>, teampane.cpp:1520-1526, 1560-1566);</item>
/// <item>wing — wing id ascending (PlayerWingCompare, teampane.cpp:1544-1550);</item>
/// <item>sector — SECTOR NAMES, case-insensitive (<c>_stricmp(SectorName(..))</c>,
/// PlayerSectorCompare, teampane.cpp:1512-1518; a missing sector reads
/// "unknown", SectorName :1577-1583) — the caller supplies the resolved name;</item>
/// <item>ship — SHIP NAMES, case-insensitive (<c>_stricmp(ShipName(..))</c>,
/// PlayerShipCompare, teampane.cpp:1504-1510; "<none>" for a missing hull type,
/// ShipName :1586-1621) — the caller supplies the resolved hull name;</item>
/// <item>objective — the current-command text, case-insensitive
/// (<c>_stricmp(CurrentCommandText(..))</c>, PlayerObjectiveCompare,
/// teampane.cpp:1528-1534; the text is verb + " " + target name, or ""/verb for
/// an illegal/unset command, CurrentCommandText :1660-1676) — the caller
/// supplies it (<c>ClientWorld.AcceptedCommandText</c>, fed by the server's
/// ORDER_CHANGE relay, FedSrv.CPP:13695-13732).</item>
/// </list>
///
/// <para>P761b: the two former proxies (by sector id / hull id, and the unsorted
/// objective column) are gone — all three columns now compare the same strings
/// native compares.</para>
/// </summary>
public static class TeamPaneSort
{
    /// <summary>The eight header columns (0..7) plus the default no-header
    /// <see cref="Key.Leader"/> order.</summary>
    public enum Key
    {
        /// <summary>Default (no header checked): sortLeader — commander first,
        /// then humans, then drones (teampane.cpp:547, 1471-1481).</summary>
        Leader = -1,
        Name = 0,
        Rank = 1,
        Kills = 2,
        Sector = 3,
        Ship = 4,
        Wing = 5,
        Credits = 6,
        Objective = 7,
    }

    /// <summary>Number of clickable header columns (native's radio bar holds
    /// exactly these eight; teampane.cpp:1396-1456).</summary>
    public const int ColumnCount = 8;

    /// <summary>The header-bar labels, in native column order.</summary>
    public static readonly string[] ColumnLabels =
    {
        "NAME", "RANK", "KILLS", "SECTOR", "SHIP", "WING", "CREDITS", "OBJECTIVE",
    };

    /// <summary>One sortable roster row — the fields the native comparators
    /// read, extracted from the port's roster snapshot + live ship entities by
    /// the caller (<c>TeamPaneLayout.Build</c> /
    /// <c>TeamPaneControls.BuildRoster</c>).</summary>
    /// <remarks>P761b — <paramref name="ShipName"/>, <paramref name="SectorName"/>
    /// and <paramref name="Objective"/> are the native comparators' string keys
    /// (ship name, sector name, CurrentCommandText). They are resolved by the
    /// caller (which owns the core/sector/order tables) so this model stays
    /// MonoGame- and ClientCore-free.</remarks>
    public readonly record struct Row(
        string Name, bool IsHuman, bool IsLeader, short Rank, int Kills, int Money,
        short SectorId, short HullId, short WingId,
        string ShipName = "", string SectorName = "", string Objective = "");

    /// <summary>The stable ordered sequence for one side's rows under
    /// <paramref name="key"/>. <see cref="Enumerable.OrderBy"/> is stable, so
    /// ties keep the caller's insertion order exactly like native's stable
    /// insertion sort (zlib/tlist.h:249-252).</summary>
    public static IReadOnlyList<Row> Ordered(IEnumerable<Row> rows, Key key) =>
        key switch
        {
            Key.Leader => rows.OrderBy(r => LeaderKey(r)).ToArray(),
            Key.Name => rows.OrderBy(r => (r.IsHuman ? 0 : 1, r.Name.ToLowerInvariant())).ToArray(),
            Key.Rank => rows.OrderBy(r => -r.Rank).ToArray(),
            Key.Kills => rows.OrderBy(r => r.Kills).ToArray(),
            // PlayerSectorCompare / PlayerShipCompare: _stricmp of the NAMES
            // (teampane.cpp:1504-1518).
            Key.Sector => rows.OrderBy(r => r.SectorName, StringComparer.OrdinalIgnoreCase).ToArray(),
            Key.Ship => rows.OrderBy(r => r.ShipName, StringComparer.OrdinalIgnoreCase).ToArray(),
            Key.Wing => rows.OrderBy(r => r.WingId).ToArray(),
            Key.Credits => rows.OrderBy(r => r.Money).ToArray(),
            // PlayerObjectiveCompare: _stricmp of CurrentCommandText
            // (teampane.cpp:1528-1534).
            Key.Objective => rows.OrderBy(r => r.Objective, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => rows.ToArray(),
        };

    // sortLeader (teampane.cpp:1471-1481): leader first; then humans before
    // drones; ties keep insertion order (stable). Key tuple encodes both.
    private static (int LeaderFlag, int HumanFlag) LeaderKey(Row r) =>
        (r.IsLeader ? 0 : 1, r.IsHuman ? 0 : 1);

    /// <summary>Radio-button header behaviour (OnButtonBarImpl,
    /// teampane.cpp:1395-1456): exactly one column checked. The port's keyboard
    /// idiom cycles the radio through the eight columns and back to the
    /// commander default; native's click on column <c>i</c> corresponds to
    /// landing on <see cref="Key"/> value <c>i</c>.</summary>
    public static Key Next(Key current) => current switch
    {
        Key.Leader => Key.Name,
        Key.Objective => Key.Leader,
        _ => (Key)((int)current + 1),
    };

    /// <summary>The header-bar text: all eight column labels, the checked one
    /// wrapped in brackets, plus the active sort's name (the checked column IS
    /// the sort, native's radio behaviour).</summary>
    public static string HeaderText(Key active)
    {
        var cells = ColumnLabels.Select((label, i) =>
            (Key)i == active ? $"[{label}]" : label);
        string sortName = active switch
        {
            Key.Leader => "COMMANDER",
            Key.Objective => "OBJECTIVE",
            _ => ColumnLabels[(int)active],
        };
        return string.Join("  ", cells) + $"   SORT: {sortName}";
    }
}
