using System.Collections.Generic;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P281 — pure (MonoGame-free) layout of the post-connect SIDE-SELECT screen.
/// The server now defers a joining peer's spawn to a side-select step: after
/// LOGONACK it pushes a SIDE_ROSTER(211) snapshot of the joinable sides and
/// holds the peer (no ship, no SET_CLUSTER) until it sends FM_C_POSITIONREQ(39)
/// naming its side. This layout turns that roster into the ASCII-safe display
/// rows <see cref="SideSelectOverlayRenderer"/> draws, following the same
/// discipline as <see cref="ShopLayout"/>/<see cref="ScoreboardLayout"/>: pure,
/// headless-testable, every string printable ASCII 32..126 (the HUD spritefonts
/// bake ASCII only — P214/p217 lesson).
/// </summary>
public static class SideSelectLayout
{
    /// <summary>Presentation bucket for a row; the renderer styles by this.</summary>
    public enum Kind
    {
        Title,
        Hint,
        Blank,
        Info,
        Item,
        Muted,
    }

    /// <summary>One display row.</summary>
    public readonly record struct Row(Kind Kind, string Text, bool Highlight = false)
    {
        public static Row Of(Kind kind, string text) => new(kind, text);
        public static Row Blank() => new(Kind.Blank, "");
    }

    /// <summary>P864d — one player in the mission's team lobby ("Not on a team")
    /// as the team screen lists it: display name, whether it is the local player,
    /// and whether it is still waiting for a side it requested (native's
    /// player-list status "Requesting", <c>WinTrek/teamscreen.cpp:495-497</c>).
    /// Gathered from the normal player-info traffic by
    /// <see cref="ClientWorld.TeamLobbyMembers"/>; never from the roster scalar.</summary>
    public readonly record struct LobbyMember(string Name, bool IsSelf, bool Requesting);

    /// <summary>Build the display rows for the given roster snapshot and the
    /// highlighted <paramref name="selectedIndex"/> (clamped into range).
    /// The randomize-teams hint is appended only when
    /// <paramref name="amMissionOwner"/> is true (the server owner-gates the
    /// message anyway; surfacing it only to the owner keeps the UI honest).
    /// A null/empty roster (snapshot not arrived yet) yields a waiting line.
    /// <para>P481 — an optional <paramref name="isSideActive"/> predicate lets
    /// the eliminated-side picker show native's inactive-state: an inactive
    /// side is rendered <c>(DESTROYED)</c> (native TeamScreen paints an
    /// empty-inactive team "destroyed", teamscreen.cpp:318) and excluded from
    /// selection. <paramref name="selectedIndex"/> is an index into the
    /// <b>selectable</b> (active) side list, matching how
    /// <see cref="SelectableSideIds"/> feeds the picker.
    /// <para>P864d — <paramref name="lobbyMembers"/> is native's lobby team row
    /// plus its player list: the team screen's team list is the real sides
    /// <b>concatenated with the lobby side</b> (<c>WinTrek/teamscreen.cpp:
    /// 1330-1335</c>), and selecting it lists that side's members
    /// (<c>:2560-2568</c>). The lobby row shows the member count
    /// (<c>:378-386</c>) and each member is drawn by name with a status
    /// (<c>:470-510</c>); <paramref name="amInTeamLobby"/> selects native's
    /// prompt text for a player who is on no team ("you need to pick a team",
    /// <c>:1662-1672</c>). The lobby is never selectable — a player always picks
    /// a REAL side (<c>teamscreen.cpp:2962-2982</c> sends POSITIONREQ for it).</para></summary>
    public static IReadOnlyList<Row> Build(
        SideRoster? roster, int selectedIndex, bool amMissionOwner,
        Func<short, bool>? isSideActive = null, Func<short, string?>? civNameFor = null,
        IReadOnlyList<LobbyMember>? lobbyMembers = null, bool amInTeamLobby = false,
        string ownName = "", bool ownRequestPending = false,
        Func<short, int>? livePlayerCount = null)
    {
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "SELECT SIDE"));
        rows.Add(Row.Of(Kind.Hint, "UP/DOWN SELECT   ENTER CONFIRM"));
        rows.Add(Row.Blank());

        // P864d — native's prompt line (teamscreen.cpp:1655-1673): a player who
        // requested a side is told so; a player ON a team is told which; a player
        // on no team is told to pick one.
        if (ownRequestPending)
            rows.Add(Row.Of(Kind.Hint, $"{AsciiSafe(ownName)} -- YOU ARE REQUESTING TO JOIN A TEAM"));
        else if (amInTeamLobby)
            rows.Add(Row.Of(Kind.Hint, $"{AsciiSafe(ownName)} -- YOU NEED TO PICK A TEAM"));

        int count = roster?.Sides.Length ?? 0;
        if (count == 0)
        {
            rows.Add(Row.Of(Kind.Muted, "WAITING FOR SIDE LIST FROM SERVER..."));
            rows.Add(Row.Blank());
            if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
            return rows;
        }

        var selectable = SelectableSideIds(roster, isSideActive);
        int sel = selectedIndex < 0 ? 0 : selectedIndex % Math.Max(1, selectable.Length);
        short? selectedSide = selectable.Length > 0 ? selectable[sel] : null;

        for (int i = 0; i < count; i++)
        {
            var s = roster!.Sides[i];
            bool active = isSideActive?.Invoke(s.SideId) ?? true;
            if (!active)
            {
                rows.Add(new Row(Kind.Muted,
                    $"    {AsciiSafe(s.Name),-20}  (DESTROYED)"));
                continue;
            }
            bool isSel = s.SideId == selectedSide;
            char marker = isSel ? '>' : ' ';
            // P864d — native draws each team's RUNNING human count
            // (<c>MissionInfo::SideNumPlayers</c>, teamscreen.cpp:390-395), not a
            // join-time snapshot; fall back to the SIDE_ROSTER count when this
            // client has no player table yet.
            int playercount = livePlayerCount?.Invoke(s.SideId) ?? s.PlayerCount;
            // P802 — the side's current civilization (read-only here; the team
            // leader changes it in the team lobby). Native draws the same civ
            // identity on the team screen (teamscreen.cpp:586-591,2479-2492).
            string civ = civNameFor?.Invoke(s.SideId) is { Length: > 0 } name ? "  [" + AsciiSafe(name) + "]" : "";
            rows.Add(new Row(Kind.Item,
                $"{marker} {AsciiSafe(s.Name),-20}  (players: {playercount}){civ}",
                Highlight: isSel));
        }

        // P864b/P864d — the team-lobby pool row, native's "Not on a team" entry
        // (MissionInfo::SideName, clintlib/clintlib.h:571; the count is the lobby
        // side's member-list count, teamscreen.cpp:378-386) plus — P864d — its
        // MEMBERS BY NAME, exactly as the team screen lists the selected team's
        // players (teamscreen.cpp:470-510). The names come from the normal
        // player-info traffic (ClientWorld.TeamLobbyMembers), not from the roster
        // scalar. Not selectable — a player picks a REAL side, never the lobby,
        // from this picker (teamscreen.cpp:2962-2982).
        int lobbyCount = Math.Max(roster!.LobbyPlayerCount, lobbyMembers?.Count ?? 0);
        if (lobbyCount > 0)
        {
            string lobbyName = "Not on a team";
            rows.Add(new Row(Kind.Muted,
                $"    {AsciiSafe(lobbyName),-20}  (players: {lobbyCount})"));
            if (lobbyMembers is not null)
            {
                foreach (var member in lobbyMembers)
                {
                    // Native's player row: name, then the status column
                    // ("Requesting" while the player waits for a side,
                    // teamscreen.cpp:493-507).
                    string status = member.Requesting ? "Requesting" : "Ready";
                    string you = member.IsSelf ? "  (you)" : "";
                    rows.Add(new Row(Kind.Muted,
                        $"      {AsciiSafe(member.Name),-18}  {status,-12}{you}"));
                }
            }
        }

        rows.Add(Row.Blank());
        if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
        return rows;
    }

    /// <summary>P481 — the ordered side ids a player may pick, excluding any
    /// side reported inactive (eliminated) by the
    /// <paramref name="isSideActive"/> predicate. When the predicate is null
    /// (or a side is unknown), the side is considered active (native
    /// <c>SideActive(sideID)</c> defaults active, matching the client world's
    /// <c>IsSideActive</c>). Kept in roster order so
    /// the picker's up/down cycling is stable and deterministic.</summary>
    public static short[] SelectableSideIds(SideRoster? roster, Func<short, bool>? isSideActive = null)
    {
        if (roster is null) return Array.Empty<short>();
        return roster.Sides
            .Where(s => isSideActive?.Invoke(s.SideId) ?? true)
            .Select(s => s.SideId)
            .ToArray();
    }

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
