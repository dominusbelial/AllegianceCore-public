using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — pure (MonoGame-free) layout of the TEAM PANE: the full-roster screen
/// showing every side's pilots with their rank name, ship status, kills and
/// deaths, plus a footer with the local commander's money and any active
/// ballot. Data is the derived roster (P774: PLAYERINFO + KILL_SHIP)
/// (<c>ClientWorld.ScoreRoster</c>), the <c>RANK_INFO(13)</c> rank table
/// (<c>ClientWorld.RankTable</c>), and the live ship entity table
/// (<c>ClientWorld.Ships</c>, flattened to <see cref="ShipBrief"/> by the
/// caller so this layout stays MonoGame- and ClientCore-free and headless-
/// testable).
///
/// <para>Native team roster state (teamscreen.cpp:452-505; teampane.cpp)
/// includes commander, AFK and wing membership. These fields come from
/// SET_TEAM_LEADER, PLAYER_READY and SET_WINGID; enemy wing membership is
/// omitted because native relays wing assignments only to teammates.</para>
///
/// <para><b>Ship status</b> is derived from the live ship entity: dead/gone →
/// "<c>DEAD</c>"; docked (<c>StationId != -1</c>, the same docked gate the
/// autoproof uses, Game1.AutoProof.cs:42-43) → "<c>DOCKED</c>"; otherwise the
/// sector "<c>SEC &lt;id&gt;</c>".</para>
///
/// <para><b>ASCII safety</b> applies to every string (see
/// <see cref="ScoreboardLayout.AsciiSafe"/>).</para>
/// </summary>
public static class TeamPaneLayout
{
    // Native WinTrek.cpp:10061-10081 / teampane.cpp: compact team roster.
    public static IReadOnlyList<Line> Compact(IReadOnlyList<Line> expanded,
        IReadOnlyList<ScoreRosterEntry> entries, short? side)
    {
        return new[] { new Line(LineKind.Title, "MINI TEAM") }
            .Concat(entries.Where(e => e.SideId == side).OrderBy(e => e.CharacterName, StringComparer.Ordinal)
                .Select(e => new Line(LineKind.Pilot, $"{ScoreboardLayout.AsciiSafe(e.CharacterName),-18} K:{e.Kills} D:{e.Deaths}",
                    expanded.Any(l => l.Highlight && l.Text.TrimStart().StartsWith(ScoreboardLayout.AsciiSafe(e.CharacterName) + " ", StringComparison.Ordinal)))))
            .Concat(expanded.Where(l => l.Kind is LineKind.Money or LineKind.Ballot)).ToArray();
    }
    /// <summary>The flattened per-pilot ship state the caller extracts from
    /// <c>ClientWorld.Ships</c> (kept MonoGame/ClientCore-free so this layout is
    /// headless-testable). P761 adds <see cref="IsHuman"/> (native's
    /// IsHuman(), the name-sort humans-before-drones rule, teampane.cpp:1490)
    /// and <see cref="Money"/> (GetMoney(), the credits column,
    /// teampane.cpp:1520). P761b adds the three STRING sort keys the native
    /// comparators actually compare: <see cref="ShipName"/> (ShipName(),
    /// teampane.cpp:1504-1510/1586-1621), <see cref="SectorName"/>
    /// (SectorName(), :1512-1518/1577-1583) and <see cref="Objective"/>
    /// (CurrentCommandText(), :1528-1534/1660-1676).</summary>
    /// <para>P775 review — <see cref="StatusKnown"/> is false for a ship the
    /// client knows only from PLAYERINFO (every enemy and drone in every sector
    /// since P775): its <see cref="SectorId"/>/<see cref="StationId"/> are the C#
    /// defaults 0, not telemetry (0 is a real cluster id, and StationId 0 would
    /// read DOCKED). Native's PlayerInfo status is reset in AddPlayerToSide
    /// (<c>clintlib.cpp:4139</c>, <c>clintlib.h:411-421</c>) until a ship status
    /// lands, so the team pane draws <c>SectorName(LastSeenSector())</c> =
    /// "unknown" for it (<c>teampane.cpp:1577-1583, 1901-1905</c>) and only a
    /// <c>c_ssDocked</c> last-seen state draws as docked (<c>:1828</c>).</para>
    public readonly record struct ShipBrief(short HullId, short SectorId, short StationId, bool Dead, bool Gone,
        bool IsLeader = false, bool? Ready = null, short? WingId = null, short ShipId = -1,
        bool IsHuman = true, int Money = 0,
        string ShipName = "", string SectorName = "", string Objective = "",
        bool StatusKnown = true);

    /// <summary>SectorName() for an unknown / NULL cluster
    /// (<c>teampane.cpp:1577-1583</c>).</summary>
    public const string UnknownSector = "unknown";

    /// <summary>P775 review — flatten one live <see cref="ShipEntity"/> into the
    /// pane's <see cref="ShipBrief"/> (the construction Game1 used inline). A
    /// ship whose status never landed (<see cref="ShipEntity.StatusSectorKnown"/>
    /// false — e.g. created by PLAYERINFO alone) reports sector -1 / station -1
    /// and the "unknown" sector name instead of its C# defaults.</summary>
    public static ShipBrief BriefOf(ClientWorld world, ShipEntity ent, Func<short, string> hullNameForSort)
    {
        bool known = ent.StatusSectorKnown;
        return new ShipBrief(
            ent.HullId, known ? ent.SectorId : (short)-1, known ? ent.StationId : (short)-1, ent.IsDead, ent.IsGone,
            world.TeamLeaderShipIdFor(ent.SideId) == ent.ShipId,
            world.Roster.ReadyByShipId.TryGetValue(ent.ShipId, out var ready) ? ready : null,
            ent.SideId == world.MySideId ? ent.WingId : null, ShipId: ent.ShipId,
            // P761 — the name/credits sort comparators need native's
            // IsHuman (PilotTypePlayer) and GetMoney (PLAYERINFO money).
            IsHuman: ent.Player is { PilotType: PlayerInfo.PilotTypePlayer },
            Money: ent.Player?.Money ?? 0,
            // P761b — the three STRING sort keys the native comparators
            // compare (teampane.cpp:1504-1534): the hull NAME, the sector NAME
            // and the accepted-command text.
            ShipName: hullNameForSort(ent.HullId),
            SectorName: SectorNameOf(world, ent),
            Objective: world.AcceptedCommandText(ent.ShipId),
            StatusKnown: known);
    }

    /// <summary>SectorName(LastSeenSector()) (<c>teampane.cpp:1577-1583</c>):
    /// the cluster name, or "unknown" when the cluster is NULL — which is also
    /// the case before any status landed (P775 review: never cluster 0 by
    /// default).</summary>
    public static string SectorNameOf(ClientWorld world, ShipEntity ent)
        => ent.StatusSectorKnown && world.Clusters.TryGetValue(ent.SectorId, out var cluster)
            ? cluster.Name : UnknownSector;

    public enum LineKind { Blank, Title, SideHeader, Pilot, Money, Ballot, Note, Hint }

    /// <summary>One roster line, ready to draw. <see cref="Highlight"/> marks the
    /// local player's own row.</summary>
    public readonly record struct Line(LineKind Kind, string Text, bool Highlight = false);

    /// <summary>P761 — the pane's radio sort-header bar (native
    /// TeamPane::OnButtonBarImpl, teampane.cpp:1395-1456). The checked column IS
    /// the sort key; <see cref="TeamPaneSort.Key.Leader"/> (the default) is
    /// native's no-header-clicked sortLeader order. Rows within each side are
    /// ordered by <see cref="TeamPaneSort.Ordered"/>'s stable sort — native's
    /// comparators with their tie-breaks (see TeamPaneSort's remarks).
    /// <paramref name="selectedName"/> additionally marks the team
    /// pane's selected row (the selection cursor the pane's controls act on;
    /// native's list selection, teampane.cpp:1104), and
    /// <paramref name="joinRequests"/> appends the own side's pending join
    /// requests for the leader (native's ConcatinatedList(members, requests),
    /// teampane.cpp:987-995) so the accept control has a subject.</para>
    /// </summary>
    public static IReadOnlyList<Line> Build(
        IReadOnlyList<ScoreRosterEntry> entries,
        IReadOnlyList<RankInfoEntry> rankTable,
        IReadOnlyDictionary<string, ShipBrief> ships,
        Func<short, bool>? isSideActive,
        int? myMoney,
        string? activeBallot,
        bool isLeader,
        string? selfName,
        string? controlsHint = null,
        string? selectedName = null,
        IReadOnlyList<string>? joinRequests = null,
        TeamPaneSort.Key sortKey = TeamPaneSort.Key.Leader,
        Func<short, string?>? sideSquadName = null)
    {
        var lines = new List<Line>();
        lines.Add(new Line(LineKind.Title, "TEAM PANE"));
        // P761 — the radio sort-header bar: one checked column, chosen with the
        // TeamPaneSort action (native's clickable header, teampane.cpp:1395-1456).
        lines.Add(new Line(LineKind.Hint, TeamPaneSort.HeaderText(sortKey)));

        foreach (var g in entries.GroupBy(e => e.SideId).OrderBy(g => g.Key))
        {
            bool destroyed = isSideActive is not null && !isSideActive(g.Key);
            // P820b — the side's squad (relayed like native SET_TEAM_INFO squadID,
            // fsmission.cpp:1584-1590) is shown on the side header when it has one.
            string? squad = sideSquadName?.Invoke(g.Key);
            lines.Add(new Line(LineKind.SideHeader,
                destroyed ? $"SIDE {g.Key}  (DESTROYED)"
                          : (squad is { Length: > 0 } ? $"SIDE {g.Key}  [{ScoreboardLayout.AsciiSafe(squad)}]  (players: {g.Count()})"
                                                      : $"SIDE {g.Key}  (players: {g.Count()})")));

            // P761 — the side's rows in the pane's sort order (native's sorted
            // member list, teampane.cpp:997-1047): the sort comparators read the
            // roster counters plus the live ship state.
            var sideRows = g.Select(e =>
            {
                ships.TryGetValue(e.CharacterName, out var sb);
                return new TeamPaneSort.Row(
                    e.CharacterName,
                    sb.IsHuman,
                    sb.IsLeader,
                    e.Rank,
                    e.Kills,
                    sb.Money,
                    sb.SectorId,
                    sb.HullId,
                    sb.WingId ?? -1,
                    sb.ShipName,
                    sb.SectorName,
                    sb.Objective);
            });
            foreach (var e in TeamPaneSort.Ordered(sideRows, sortKey).Select(r => r.Name)
                         .Select(name => g.First(x => x.CharacterName == name)))
            {
                bool self = selfName != null && string.Equals(e.CharacterName, selfName, StringComparison.Ordinal);
                bool selected = selectedName != null && string.Equals(e.CharacterName, selectedName, StringComparison.Ordinal);
                string rankName = RankNameLookup.Lookup(rankTable, e.Rank);
                string status = StatusFor(ships, e.CharacterName);
                bool hasShip = ships.TryGetValue(e.CharacterName, out var sb);
                // P761c — draw the SAME resolved strings the sort compares
                // (sector NAME, hull NAME, CurrentCommandText), not sector/hull
                // ids: native draws SectorName() (teampane.cpp:1901-1907) and the
                // hull/objective from the model, and P761b's "SEC 1053 HLD13"
                // showed ids while sorting by names. A hull missing from the
                // core reads "<none>" (ShipName(), teampane.cpp:1586-1621);
                // without a core the id text is the honest fallback.
                string hull = hasShip ? DisplayHullName(sb) : "";
                // teamscreen.cpp:452-477,497-505: leader and AFK are roster
                // state, independent of a pilot's current ship location.
                string role = sb.IsLeader ? " [COMMANDER]" : "";
                if (sb.Ready == false) role += " [AFK]";
                string objective = hasShip ? sb.Objective : "";
                string cursor = selected ? ">   " : "    ";
                // Compact columns: the role/wing tail must not push the
                // objective text past the renderer's fit (P761c re-shoot).
                string text = $"{cursor}{ScoreboardLayout.AsciiSafe(e.CharacterName),-18}  " +
                              $"{ScoreboardLayout.AsciiSafe(rankName),-12}  " +
                              $"{ScoreboardLayout.AsciiSafe(status),-14}  " +
                              $"{ScoreboardLayout.AsciiSafe(hull),-10}  " +
                              $"K:{e.Kills}  D:{e.Deaths}  OBJ:{ScoreboardLayout.AsciiSafe(objective)}{role} WING:{(sb.WingId is { } wing2 ? WingNames.For(wing2) : "?")}";
                lines.Add(new Line(LineKind.Pilot, text, self || selected));
            }
        }

        // P752 — the own side's pending join requests (leader view): native
        // appends the request list to the members (teampane.cpp:987-995) and the
        // accept control acts on the selected one.
        if (isLeader && joinRequests is { Count: > 0 })
        {
            lines.Add(new Line(LineKind.Note, "  JOIN REQUESTS:"));
            foreach (var name in joinRequests)
                lines.Add(new Line(LineKind.Pilot,
                    $"{(selectedName != null && string.Equals(name, selectedName, StringComparison.Ordinal) ? ">   " : "    ")}{ScoreboardLayout.AsciiSafe(name),-18}  [WAITING]",
                    selectedName != null && string.Equals(name, selectedName, StringComparison.Ordinal)));
        }

        lines.Add(new Line(LineKind.Blank, ""));
        if (myMoney is { } money)
            lines.Add(new Line(LineKind.Money, $"MY MONEY: {money}"));

        if (isLeader && !string.IsNullOrWhiteSpace(activeBallot))
            lines.Add(new Line(LineKind.Ballot, $"ACTIVE BALLOT: {ScoreboardLayout.AsciiSafe(activeBallot)}"));

        // P761b — the controls hint is long; wrap it so the pane stays a panel
        // instead of a screen-wide strip (the renderer additionally truncates as
        // a hard guarantee — a line can never overflow).
        lines.AddRange(Wrapped(LineKind.Hint,
            controlsHint ?? "Ctrl + T TOGGLE  |  B LEADERBOARD  |  P CHARACTER SHEET"));

        return lines;
    }

    /// <summary>P761b — wrap one long line at word boundaries into lines of at
    /// most <see cref="HintWrapColumns"/> characters (a hard cap; the renderer
    /// truncates anything still too wide for its font).</summary>
    public const int HintWrapColumns = 96;

    public static IEnumerable<Line> Wrapped(LineKind kind, string text)
    {
        foreach (var part in Wrap(text, HintWrapColumns)) yield return new Line(kind, part);
    }

    /// <summary>Split <paramref name="text"/> into word-wrapped chunks of at most
    /// <paramref name="max"/> characters, breaking on spaces and prefixing the
    /// continuation lines with two spaces (a hanging indent).</summary>
    public static IReadOnlyList<string> Wrap(string text, int max = HintWrapColumns)
    {
        if (max <= 0 || text.Length <= max) return [text];
        var parts = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (candidate.Length > max && line.Length > 0)
            {
                parts.Add(line.ToString());
                line.Clear().Append("  ").Append(word);
            }
            else line.Clear().Append(candidate);
        }
        if (line.Length > 0) parts.Add(line.ToString());
        return parts;
    }

    /// <summary>P761b — one roster row's sort keys, resolved from the live world
    /// exactly as the native comparators resolve them. Shared by this layout's
    /// caller and by <c>TeamPaneControls</c> so the drawn order and the
    /// selection order can never diverge.</summary>
    /// <param name="hullName">Resolves a hull id to the hull's name (native
    /// <c>GetHullType(id)-&gt;GetName()</c>; the caller owns the parsed core, so
    /// it supplies the lookup). A resolver that cannot find the hull returns
    /// "<none>" (native ShipName(), teampane.cpp:1586-1621); a NULL resolver
    /// (no shop catalog) falls back to the hull id text so the sort still works.</param>
    public static TeamPaneSort.Row SortRow(ClientWorld world, ScoreRosterEntry e,
        Func<short, string>? hullName)
    {
        var ent = world.Ships.Values.FirstOrDefault(s =>
            s.SideId == e.SideId && s.CharacterName == e.CharacterName);
        // SectorName(): a NULL cluster reads "unknown" (:1580-1583); so does a
        // ship whose status never landed (P775 review).
        string sectorName = ent is null ? UnknownSector : SectorNameOf(world, ent);
        return new TeamPaneSort.Row(
            e.CharacterName,
            ent is null || ent.Player is { PilotType: Allegiance.Core.Wire.PlayerInfo.PilotTypePlayer },
            ent is not null && world.TeamLeaderShipIdFor(e.SideId) == ent.ShipId,
            e.Rank,
            e.Kills,
            ent?.Player?.Money ?? 0,
            ent is { StatusSectorKnown: true } ? ent.SectorId : (short)-1,
            ent?.HullId ?? -1,
            ent?.WingId ?? -1,
            ent is null ? "" : ResolveHullName(hullName, ent.HullId),
            sectorName,
            ent is null ? "" : world.AcceptedCommandText(ent.ShipId));
    }

    /// <summary>P761c — the one hull-name rule for both the sort and the drawn
    /// cell: a named hull is its name; a hull the core does not know reads
    /// "<none>" (native ShipName(), teampane.cpp:1586-1621); with no core the
    /// hull id text keeps the sort meaningful.</summary>
    public static string ResolveHullName(Func<short, string>? hullName, short hullId)
    {
        if (hullName is null) return $"HLD{hullId}";
        string? resolved = hullName(hullId);
        return string.IsNullOrEmpty(resolved) ? "<none>" : resolved;
    }

    /// <summary>The hull cell for an already-resolved brief: the name the sort
    /// uses, or the id text when the caller had no catalog.</summary>
    private static string DisplayHullName(ShipBrief sb)
        => sb.ShipName.Length > 0 ? sb.ShipName : $"HLD{sb.HullId}";

    private static string StatusFor(IReadOnlyDictionary<string, ShipBrief> ships, string name)
    {
        if (!ships.TryGetValue(name, out var sb)) return "?";
        if (sb.Dead || sb.Gone) return "DEAD";
        // P775 review — no status ever landed (a PLAYERINFO-only ship): native
        // draws SectorName(NA) = "unknown" (teampane.cpp:1577-1583, 1901-1905),
        // never DOCKED (that needs a c_ssDocked last-seen state, :1828).
        if (!sb.StatusKnown) return UnknownSector;
        if (sb.StationId != -1) return "DOCKED";
        // P761c — the same sector NAME the SECTOR sort compares (SectorName(),
        // teampane.cpp:1512-1518, 1577-1583), never the raw sector id.
        return sb.SectorName.Length > 0 ? sb.SectorName : "?";
    }
}
