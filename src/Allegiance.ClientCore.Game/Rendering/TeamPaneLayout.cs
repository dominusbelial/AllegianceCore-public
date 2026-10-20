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
/// ballot. Data is the periodic <c>ScoreRosterUpdate(209)</c> snapshot
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
    /// headless-testable).</summary>
    public readonly record struct ShipBrief(short HullId, short SectorId, short StationId, bool Dead, bool Gone,
        bool IsLeader = false, bool? Ready = null, short? WingId = null, short ShipId = -1);

    public enum LineKind { Blank, Title, SideHeader, Pilot, Money, Ballot, Note, Hint }

    /// <summary>One roster line, ready to draw. <see cref="Highlight"/> marks the
    /// local player's own row.</summary>
    public readonly record struct Line(LineKind Kind, string Text, bool Highlight = false);

    /// <summary>
    /// Build the ordered, ASCII-safe team-pane lines. Sides are grouped in id
    /// order; pilots within a side are sorted by name. The local player (matched
    /// by <paramref name="selfName"/>) is highlighted. <paramref name="ships"/>
    /// maps a character name to its <see cref="ShipBrief"/> for the status
    /// column (absent names fall back to "?" — a pilot with no ship entity yet).
    /// <para>P752 — <paramref name="selectedName"/> additionally marks the team
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
        IReadOnlyList<string>? joinRequests = null)
    {
        var lines = new List<Line>();
        lines.Add(new Line(LineKind.Title, "TEAM PANE"));

        foreach (var g in entries.GroupBy(e => e.SideId).OrderBy(g => g.Key))
        {
            bool destroyed = isSideActive is not null && !isSideActive(g.Key);
            lines.Add(new Line(LineKind.SideHeader,
                destroyed ? $"SIDE {g.Key}  (DESTROYED)" : $"SIDE {g.Key}  (players: {g.Count()})"));

            foreach (var e in g.OrderBy(e => e.CharacterName, StringComparer.Ordinal))
            {
                bool self = selfName != null && string.Equals(e.CharacterName, selfName, StringComparison.Ordinal);
                bool selected = selectedName != null && string.Equals(e.CharacterName, selectedName, StringComparison.Ordinal);
                string rankName = RankNameLookup.Lookup(rankTable, e.Rank);
                string status = StatusFor(ships, e.CharacterName);
                bool hasShip = ships.TryGetValue(e.CharacterName, out var sb);
                string hull = hasShip && sb.HullId > 0 ? $" HLD{sb.HullId}" : "";
                string id = hasShip && sb.ShipId >= 0 ? $" ID:{sb.ShipId}" : "";
                // teamscreen.cpp:452-477,497-505: leader and AFK are roster
                // state, independent of a pilot's current ship location.
                string role = sb.IsLeader ? " [COMMANDER]" : "";
                if (sb.Ready == false) role += " [AFK]";
                role += $" WING:{(sb.WingId is { } wing ? WingNames.For(wing) : "?")}";
                string cursor = selected ? ">   " : "    ";
                string text = $"{cursor}{ScoreboardLayout.AsciiSafe(e.CharacterName),-18}  " +
                              $"{ScoreboardLayout.AsciiSafe(rankName),-14}  " +
                              $"{status,-10}{hull}  K:{e.Kills}  D:{e.Deaths}{role}{id}";
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

        lines.Add(new Line(LineKind.Hint, controlsHint ?? "Ctrl + T TOGGLE  |  B LEADERBOARD  |  P CHARACTER SHEET"));

        return lines;
    }

    private static string StatusFor(IReadOnlyDictionary<string, ShipBrief> ships, string name)
    {
        if (!ships.TryGetValue(name, out var sb)) return "?";
        if (sb.Dead || sb.Gone) return "DEAD";
        if (sb.StationId != -1) return "DOCKED";
        if (sb.SectorId > 0) return $"SEC {sb.SectorId}";
        return "?";
    }
}
