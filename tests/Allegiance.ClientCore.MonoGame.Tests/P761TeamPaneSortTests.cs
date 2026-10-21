using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — the team pane's radio sort-header bar (native
/// <c>TeamPane::OnButtonBarImpl</c>, WinTrek/teampane.cpp:1395-1456): one
/// checked column, each column a sort key, the list (and the selection cursor)
/// re-ordered through <see cref="TeamPaneSort"/>'s stable comparators — the
/// native comparators' exact tie-breaks (see TeamPaneSort's remarks; the TList
/// "compare returns true if the first value should be after the second"
/// semantics, zlib/tlist.h:246-266).
/// </summary>
public class P761TeamPaneSortTests
{
    private static TeamPaneSort.Row Row(string name, bool human = true, bool leader = false,
        short rank = 0, int kills = 0, int money = 0, short sector = 0, short hull = 0, short wing = 0) =>
        new(name, human, leader, rank, kills, money, sector, hull, wing);

    // --- comparators (native teampane.cpp:1482-1576) -----------------------

    [Fact]
    public void NameSort_HumansBeforeDrones_ThenCaseInsensitiveAscending()
    {
        // PlayerNameCompare (teampane.cpp:1490-1502): humans first, then
        // case-insensitive name ascending (both-human returns stricmp > 0 = "after").
        var rows = new[]
        {
            Row("zeta", human: true),
            Row("drone", human: false),
            Row("Alpha", human: true),
        };
        var sorted = TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Name);
        Assert.Equal(["Alpha", "zeta", "drone"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void RankSort_HighestRankValueFirst()
    {
        // PlayerRankCompare (teampane.cpp:1536-1542) returns p1.Rank() < p2.Rank()
        // → the LOWER rank value sorts AFTER → descending by rank value.
        var rows = new[] { Row("low", rank: 2), Row("high", rank: 9), Row("mid", rank: 5) };
        var sorted = TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Rank);
        Assert.Equal(["high", "mid", "low"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void KillsAndCreditsSort_Ascending_LikeNativesGreaterComparators()
    {
        // PlayerKillsCompare / PlayerMoneyCompare return p1 > p2 = "p1 after"
        // (teampane.cpp:1520-1526, 1560-1566) → ascending. Native really does
        // put the smaller numbers first; pinned verbatim.
        var kills = TeamPaneSort.Ordered(new[] { Row("k9", kills: 9), Row("k1", kills: 1), Row("k5", kills: 5) },
            TeamPaneSort.Key.Kills);
        Assert.Equal(["k1", "k5", "k9"], kills.Select(r => r.Name));
        var credits = TeamPaneSort.Ordered(new[] { Row("c9", money: 900), Row("c1", money: 100) },
            TeamPaneSort.Key.Credits);
        Assert.Equal(["c1", "c9"], credits.Select(r => r.Name));
    }

    [Fact]
    public void WingSort_Ascending()
    {
        // PlayerWingCompare (teampane.cpp:1544-1550): pship1->GetWingID() > p2 =
        // "after" → ascending wing id.
        var wings = TeamPaneSort.Ordered(new[] { Row("w2", wing: 2), Row("w1", wing: 1) }, TeamPaneSort.Key.Wing);
        Assert.Equal(["w1", "w2"], wings.Select(r => r.Name));
    }

    [Fact]
    public void SectorSort_BySectorName_CaseInsensitive_NotById()
    {
        // PlayerSectorCompare (teampane.cpp:1512-1518):
        // _stricmp(SectorName(p1->LastSeenSector()), SectorName(p2->...)) > 0 —
        // the sector NAME, case-insensitively. The ids here run OPPOSITE to the
        // names, so an id sort would produce the reverse order.
        var rows = new[]
        {
            new TeamPaneSort.Row("b", true, false, 0, 0, 0, SectorId: 1, HullId: 0, WingId: 0,
                ShipName: "", SectorName: "beta"),
            new TeamPaneSort.Row("a", true, false, 0, 0, 0, SectorId: 2, HullId: 0, WingId: 0,
                ShipName: "", SectorName: "Alpha"),
            new TeamPaneSort.Row("c", true, false, 0, 0, 0, SectorId: 3, HullId: 0, WingId: 0,
                ShipName: "", SectorName: "gamma"),
        };
        var sorted = TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Sector);
        Assert.Equal(["a", "b", "c"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void ShipSort_ByShipName_CaseInsensitive_NotByHullId()
    {
        // PlayerShipCompare (teampane.cpp:1504-1510):
        // _stricmp(ShipName(p1), ShipName(p2)) > 0 — the hull's NAME. Hull ids
        // run opposite to the names here, so a hull-id sort would reverse this.
        var rows = new[]
        {
            new TeamPaneSort.Row("b", true, false, 0, 0, 0, 0, HullId: 1, WingId: 0,
                ShipName: "scout"),
            new TeamPaneSort.Row("a", true, false, 0, 0, 0, 0, HullId: 2, WingId: 0,
                ShipName: "Assault"),
            new TeamPaneSort.Row("c", true, false, 0, 0, 0, 0, HullId: 3, WingId: 0,
                ShipName: "titan"),
        };
        var sorted = TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Ship);
        Assert.Equal(["a", "b", "c"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void ObjectiveSort_ByAcceptedCommandText_CaseInsensitive()
    {
        // PlayerObjectiveCompare (teampane.cpp:1528-1534):
        // _stricmp(CurrentCommandText(pship1), CurrentCommandText(pship2)) > 0.
        // The texts are the server-relayed accepted commands' verb + target name
        // (CurrentCommandText :1660-1676); "" (no command) sorts first.
        var rows = new[]
        {
            new TeamPaneSort.Row("c", true, false, 0, 0, 0, 0, 0, 0,
                ShipName: "", SectorName: "", Objective: "mine Rock3"),
            new TeamPaneSort.Row("a", true, false, 0, 0, 0, 0, 0, 0,
                ShipName: "", SectorName: "", Objective: ""),
            new TeamPaneSort.Row("b", true, false, 0, 0, 0, 0, 0, 0,
                ShipName: "", SectorName: "", Objective: "Attack Enemy"),
        };
        var sorted = TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Objective);
        Assert.Equal(["a", "b", "c"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void DefaultLeaderSort_CommanderFirst_ThenHumans_ThenDrones()
    {
        // sortLeader (constructor default, teampane.cpp:547; LeaderCompare
        // :1471-1481): leader first, then humans, then drones; ties stable.
        var rows = new[]
        {
            Row("drone", human: false),
            Row("pilot", human: true),
            Row("comm", human: true, leader: true),
            Row("drone2", human: false),
        };
        Assert.Equal(["comm", "pilot", "drone", "drone2"],
            TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Leader).Select(r => r.Name));
    }

    // --- radio bar + cycle + header ----------------------------------------

    [Fact]
    public void Cycle_RunsTheEightColumns_ThenBackToCommanderDefault()
    {
        // Native's radio bar: exactly one column checked (OnButtonBarImpl clears
        // every other button, teampane.cpp:1398-1406). The keyboard idiom cycles
        // Leader → 0..7 → Leader.
        var key = TeamPaneSort.Key.Leader;
        var seen = new List<TeamPaneSort.Key>();
        for (int i = 0; i < 8; i++) { key = TeamPaneSort.Next(key); seen.Add(key); }
        Assert.Equal(
            new[]
            {
                TeamPaneSort.Key.Name, TeamPaneSort.Key.Rank, TeamPaneSort.Key.Kills,
                TeamPaneSort.Key.Sector, TeamPaneSort.Key.Ship, TeamPaneSort.Key.Wing,
                TeamPaneSort.Key.Credits, TeamPaneSort.Key.Objective,
            },
            seen);
        // ... and back to the commander default (the un-clicked radio state).
        Assert.Equal(TeamPaneSort.Key.Leader, TeamPaneSort.Next(key));
    }

    [Fact]
    public void HeaderText_MarksExactlyOneColumn()
    {
        string header = TeamPaneSort.HeaderText(TeamPaneSort.Key.Kills);
        Assert.Contains("[KILLS]", header);
        Assert.DoesNotContain("[NAME]", header);
        Assert.Contains("SORT: KILLS", header);
        Assert.Contains("OBJECTIVE", header);    // native's column set (no deaths/turrets — commented out natively)
        Assert.DoesNotContain("DEATHS", header);
        Assert.DoesNotContain("TURRETS", header);
    }

    // --- through the controller + layout ------------------------------------

    [Fact]
    public void SortKeyEdge_AdvancesTheRadioAndReordersRosterWithSelectionClamped()
    {
        var w = new ClientWorld();
        w.Apply(new Allegiance.Core.Wire.SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
                Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1),
        });
        foreach (short id in new short[] { 10, 11 })
            w.Apply(new ExportShip { ShipId = id, SideId = 0, CharacterName = $"p{id}" });
        // P774 — the roster is derived from PLAYERINFO(10) (the native seed),
        // not from the retired score-roster snapshot (209).
        w.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "p10" });
        w.Apply(new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "p11" });
        w.MarkApplied();   // P774 — end of the received batch: the derived roster rebuilds here
        var c = new TeamPaneControls();
        var map = new ActionMap(InputActionCatalog.Definitions);

        // Ctrl+S (TeamPaneSort): the radio advances and the roster reorders.
        var r = c.Step(new KeyboardState(Keys.S, Keys.LeftControl), map, w, active: true, ballotActive: false);
        Assert.True(r.SortAdvanced);
        Assert.Equal(TeamPaneSort.Key.Name, c.SortKey);

        // The roster (selection order) now follows the NAME comparator.
        var rows = c.BuildRoster(w);
        Assert.Equal(2, rows.Count);

        // A held key does not re-fire (edge), and an inactive pane never cycles.
        var held = c.Step(new KeyboardState(Keys.S, Keys.LeftControl), map, w, active: true, ballotActive: false);
        Assert.False(held.SortAdvanced);
        Assert.Equal(TeamPaneSort.Key.Name, c.SortKey);
    }

    [Fact]
    public void Layout_DrawsTheHeaderBar_AndFollowsTheSortKey()
    {
        var entries = new List<ScoreRosterEntry>
        {
            new("b", 0, 5, 0, 0, 1),
            new("a", 0, 9, 0, 0, 3),
        };
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>();
        var lines = TeamPaneLayout.Build(entries, Array.Empty<RankInfoEntry>(), ships,
            null, null, null, false, null, sortKey: TeamPaneSort.Key.Name);
        Assert.Contains(lines, l => l.Text.Contains("[NAME]"));

        var killSort = TeamPaneLayout.Build(entries, Array.Empty<RankInfoEntry>(), ships,
            null, null, null, false, null, sortKey: TeamPaneSort.Key.Kills);
        // KILLS ascending (native's comparator semantics): 5 kills before 9.
        var pilots = killSort.Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot).Select(l => l.Text).ToList();
        Assert.True(pilots[0].Contains(" b "), $"first pilot row should be 'b' (5 kills): {pilots[0]}");
    }

    // --- P761b: the three name-based columns, end to end --------------------

    private static ServerShipUpdate Pose(short id) =>
        ServerShipUpdate.FromValues(id, 0, default, 0, new Vector3(0, 0, 0),
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);

    /// <summary>Seat two teammates (ships 10/11) plus one enemy target (99) in a
    /// world with named sectors.</summary>
    private static ClientWorld TeamWorld()
    {
        var w = new ClientWorld();
        w.Apply(new ExportCluster
        {
            Cluster = new DataClusterIGC(1, [0, 0, 1], 0xffffffff, 0, 0, 1, 0, 0, "Alpha", "", "", 0, 0, 0, true, false),
        });
        w.Apply(new ExportCluster
        {
            Cluster = new DataClusterIGC(1, [0, 0, 1], 0xffffffff, 0, 0, 2, 0, 0, "beta", "", "", 0, 0, 0, true, false),
        });
        w.Apply(new SetCluster { SectorId = 1, Ship = Pose(10) });
        w.Apply(new SetCluster { SectorId = 1, Ship = Pose(11) });
        w.Apply(new SetCluster { SectorId = 1, Ship = Pose(99) });   // the enemy to name in the order text
        foreach (var (id, side, hull, sector) in new (short, short, short, short)[]
                 { (10, 0, 22, 1), (11, 0, 11, 2), (99, 1, 21, 1) })
        {
            w.Apply(new ExportShip { ShipId = id, SideId = side, CharacterName = $"p{id}", HullId = hull });
            w.Apply(new ShipStatusMessage { ShipId = id, HullId = hull, SectorId = sector, StationId = -1, ParentId = -1 });
        }
        // P774 — PLAYERINFO(10) seeds the roster (the retired 209 is gone).
        w.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "p10" });
        w.Apply(new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "p11" });
        w.MarkApplied();   // P774 — end of the received batch: the derived roster rebuilds here
        return w;
    }

    [Fact]
    public void RelayedOrderChange_AppliesToATeammatesShip_AndBecomesItsObjectiveText()
    {
        // Native applies ORDER_CHANGE to ANY ship in the client core
        // (clintlib/appmsg.cpp:1831-1855), fed by the server's side-wide relay
        // (FedSrv.CPP:13695-13732); CurrentCommandText is the tree then sorts
        // (teampane.cpp:1528-1534, 1660-1676).
        var w = TeamWorld();
        Assert.Equal("", w.AcceptedCommandText(11));   // unset slot → empty text

        w.Apply(new OrderChange
        {
            Command = OrderChange.CmdAccepted, ShipID = 11,
            CommandId = OrderChange.CidAttack,
            ObjectType = IgcObjectType.Ship, ObjectID = 99,
        });
        // Verb + " " + the target's name (Igc/common.cpp:41-52 + GetModelName).
        Assert.Equal("attack p99", w.AcceptedCommandText(11));
        Assert.Equal("", w.AcceptedCommandText(10));   // other ships untouched

        // Clearing the target keeps the bare verb (native's no-pmodel branch).
        w.Apply(new OrderChange
        {
            Command = OrderChange.CmdAccepted, ShipID = 11,
            CommandId = OrderChange.CidGoto, ObjectType = (IgcObjectType)(-1), ObjectID = -1,
        });
        Assert.Equal("goto", w.AcceptedCommandText(11));
    }

    [Fact]
    public void SortRow_ResolvesSectorNameAndObjective_FromTheWorld()
    {
        var w = TeamWorld();
        w.Apply(new OrderChange
        {
            Command = OrderChange.CmdAccepted, ShipID = 10,
            CommandId = OrderChange.CidDefend, ObjectType = IgcObjectType.Ship, ObjectID = 99,
        });
        var entries = w.ScoreRoster.ToArray();
        var p10 = TeamPaneLayout.SortRow(w, entries.Single(e => e.CharacterName == "p10"),
            hullName: id => id == 22 ? "Titan" : "Scout");
        var p11 = TeamPaneLayout.SortRow(w, entries.Single(e => e.CharacterName == "p11"),
            hullName: id => id == 22 ? "Titan" : "Scout");

        Assert.Equal("Alpha", p10.SectorName);        // sector 1's name
        Assert.Equal("beta", p11.SectorName);         // sector 2's name
        Assert.Equal("Titan", p10.ShipName);
        Assert.Equal("Scout", p11.ShipName);
        Assert.Equal("defend p99", p10.Objective);
        Assert.Equal("", p11.Objective);

        // The three native comparators then order by those strings: sector name
        // (Alpha < beta), ship name (Scout < Titan from p11/p10), objective.
        var rows = new[] { p10, p11 };
        Assert.Equal(["p10", "p11"],
            TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Sector).Select(r => r.Name));
        Assert.Equal(["p11", "p10"],
            TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Ship).Select(r => r.Name));
        Assert.Equal(["p11", "p10"],   // "" sorts before "defend p99"
            TeamPaneSort.Ordered(rows, TeamPaneSort.Key.Objective).Select(r => r.Name));
    }


    /// <summary>P761c — the drawn row uses the SAME resolved strings the sorts
    /// compare (sector name, hull name, CurrentCommandText), never raw ids.
    /// Native: SectorName()/ShipName()/CurrentCommandText
    /// (teampane.cpp:1577-1583, 1586-1621, 1660-1676).</summary>
    [Fact]
    public void Layout_RendersResolvedSectorAndHullNames_AndTheObjectiveText_NotIds()
    {
        var entries = new List<ScoreRosterEntry> { new("p10", 0, 0, 0, 0, 0) };
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>
        {
            ["p10"] = new(22, 1, -1, false, false,
                ShipName: "Titan", SectorName: "Ceto", Objective: "defend p99"),
        };
        var row = TeamPaneLayout.Build(entries, Array.Empty<RankInfoEntry>(), ships,
            null, null, null, false, null)
            .Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot).Text;
        Assert.Contains(" Ceto ", row);
        Assert.Contains(" Titan ", row);
        Assert.Contains("OBJ:defend p99", row);
        Assert.DoesNotContain("SEC 1", row);
        Assert.DoesNotContain("HLD22", row);
    }

    /// <summary>P761c — a missing hull reads "&lt;none&gt;" (native ShipName(),
    /// teampane.cpp:1612); with no shop catalog (null resolver) the id text keeps
    /// the SHIP sort meaningful instead of collapsing every row to "".</summary>
    [Fact]
    public void SortRow_MissingHullReadsNone_AndNullCatalogFallsBackToTheHullIdText()
    {
        var w = TeamWorld();
        var p10 = w.ScoreRoster.Single(e => e.CharacterName == "p10");

        var missing = TeamPaneLayout.SortRow(w, p10, hullName: _ => "<none>");
        Assert.Equal("<none>", missing.ShipName);
        // An empty resolver result is a MISS, not a sortable empty key.
        Assert.Equal("<none>", TeamPaneLayout.SortRow(w, p10, hullName: _ => "").ShipName);

        var noCatalog = TeamPaneLayout.SortRow(w, p10, hullName: null);
        Assert.Equal("HLD22", noCatalog.ShipName);
        // The fallback key still sorts (p11's hull 11 before p10's 22).
        var p11 = TeamPaneLayout.SortRow(w, w.ScoreRoster.Single(e => e.CharacterName == "p11"), null);
        Assert.Equal(["p11", "p10"],
            TeamPaneSort.Ordered(new[] { noCatalog, p11 }, TeamPaneSort.Key.Ship).Select(r => r.Name));
    }

    [Fact]
    public void Layout_SortsPilotRowsByShipAndSectorName()
    {
        var entries = new List<ScoreRosterEntry>
        {
            new("p10", 0, 0, 0, 0, 0),
            new("p11", 0, 0, 0, 0, 0),
        };
        var ships = new Dictionary<string, TeamPaneLayout.ShipBrief>
        {
            // hull ids run OPPOSITE to the names → an id sort would reverse this.
            ["p10"] = new(22, 1, -1, false, false, ShipName: "Titan", SectorName: "Alpha"),
            ["p11"] = new(11, 1, -1, false, false, ShipName: "Scout", SectorName: "beta"),
        };
        var byShip = TeamPaneLayout.Build(entries, Array.Empty<RankInfoEntry>(), ships,
            null, null, null, false, null, sortKey: TeamPaneSort.Key.Ship)
            .Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot).Select(l => l.Text).ToList();
        Assert.True(byShip[0].Contains(" p11 "), $"Scout (hull 11) sorts first by NAME: {byShip[0]}");
        var bySector = TeamPaneLayout.Build(entries, Array.Empty<RankInfoEntry>(), ships,
            null, null, null, false, null, sortKey: TeamPaneSort.Key.Sector)
            .Where(l => l.Kind == TeamPaneLayout.LineKind.Pilot).Select(l => l.Text).ToList();
        Assert.True(bySector[0].Contains(" p10 "), $"Alpha sorts first by NAME: {bySector[0]}");
    }
}
