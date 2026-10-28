using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P282 — pure (MonoGame-free) layout of the READY-CHECK screen shown between
/// P281's side-select and flight. Once seated (MyShipId set) the server holds
/// the mission at NotStarted until every occupied side is aggregate-ready
/// (P180 TryAutoStart); this screen replaces the old unconditional auto-ready
/// hack (Game1 sent <c>SendPlayerReady(true)</c> the instant it had a ship)
/// with a real player toggled ready flag, a live same-side roster of ready
/// states (from the real PLAYER_READY(45) relays the server sends to the whole
/// side, incl. the sender — <see cref="ClientWorld.Roster"/>), the per-side
/// aggregate <c>TEAM_READY(46)</c> broadcast, and a leader force-ready trigger
/// (<c>FORCE_TEAM_READY(47)</c>) surfaced to each team commander (teamscreen.cpp:1554-1559).
///
/// Follows the same discipline as <see cref="SideSelectLayout"/>: pure,
/// headless-testable, every string printable ASCII 32..126 (the HUD
/// spritefonts bake ASCII only — P214/P217 lesson).
/// </summary>
public static class ReadyCheckLayout
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
        /// <summary>P853b — a refused input (the native message box that
        /// <c>newgamescreen.cpp:1117-1119</c> / <c>temascreen.cpp:1044-1075</c>
        /// raise). Drawn in a warning colour so a rejected edit is impossible to
        /// miss.</summary>
        Error,
    }

    /// <summary>One display row.</summary>
    public readonly record struct Row(Kind Kind, string Text, bool Highlight = false)
    {
        public static Row Of(Kind kind, string text) => new(kind, text);
        public static Row Blank() => new(Kind.Blank, "");
    }

    /// <summary>One same-side player on the roster: display name, current ready
    /// flag (from the real PLAYER_READY relay), and whether it is the local
    /// player (styled distinctly).</summary>
    public readonly record struct PlayerRow(string Name, bool Ready, bool IsSelf);

    /// <summary>P864d — one player in the mission's team lobby ("Not on a team",
    /// native's lobby team row: <c>WinTrek/teamscreen.cpp:378-386</c> for the
    /// count, <c>:470-510</c> for the per-player row). <see cref="RequestingSideName"/>
    /// is the side this player asked to join and is still waiting for (native's
    /// player-row status "Requesting", <c>:495-497</c>), or null when idle.</summary>
    public readonly record struct LobbyRow(string Name, bool IsSelf, string? RequestingSideName);

    /// <summary>Build the READY-CHECK rows. <paramref name="sideDisplayName"/>
    /// is the side's name (from the SIDE_ROSTER entry matching MySideId, or a
    /// "SIDE &lt;id&gt;" fallback); <paramref name="ownReady"/> is the local
    /// player's current toggle; <paramref name="players"/> is the same-side
    /// roster; <paramref name="sideAggregateReady"/> is the last
    /// <c>TEAM_READY(46)</c> for the own side (null until one arrives);
    /// <paramref name="forcedReady"/> is the own side's force flag (from the
    /// <c>FORCE_TEAM_READY(47)</c> echo). The force-ready hint is appended only
    /// when <paramref name="canForceTeamReady"/> is true (mission ownership is independent).
    /// <param name="launchCountdown">P522 — seconds remaining in the
    /// mission-start countdown (STAGE_STARTING), or <see langword="null"/>
    /// when it hasn't begun. While non-null the local ship is held (not
    /// flying) awaiting <c>STAGE_STARTED</c>, and this line surfaces the
    /// countdown clearly — native holds the player through the countdown and
    /// only enters the game at <c>STAGE_STARTED</c> (appmsg.cpp:2330-2346 →
    /// OnEnterMission → screen(ScreenIDCombat)).</summary>
    public static IReadOnlyList<Row> Build(
        string sideDisplayName,
        bool ownReady,
        IReadOnlyList<PlayerRow> players,
        bool? sideAggregateReady,
        bool forcedReady,
        bool canForceTeamReady,
        int? launchCountdown = null,
        IReadOnlyList<LobbyRow>? lobby = null,
        bool canAcceptLobbyRequest = false,
        bool amMissionOwner = false)
    {
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "READY CHECK"));
        rows.Add(Row.Of(Kind.Hint, "Y TOGGLE READY"));
        if (canForceTeamReady) rows.Add(Row.Of(Kind.Hint, "O TOGGLE TEAM READY"));
        // P864d — the team screen's other owner/leader controls: native's
        // Randomize button (mission owner, teamscreen.cpp:1554-1573 button row +
        // OnButtonRandomize → FM_C_RANDOMIZE_TEAMS) and the team leader's Accept
        // button for a pending join request (teamscreen.cpp:2998-3013 →
        // FM_C_POSITIONACK accepted).
        if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
        if (canAcceptLobbyRequest) rows.Add(Row.Of(Kind.Hint, "A  ACCEPT LOBBY REQUEST"));

        // P522 — the countdown hold: while the mission is STAGE_STARTING the
        // ship is held (not flying), so surface the launch countdown loudly.
        // This is the "held" view native shows during the briefing (its
        // GameStateContainer countdown timer, WinTrek.cpp:8129-8137).
        if (launchCountdown is { } ld)
            rows.Add(new Row(Kind.Title, $"LAUNCHING IN {ld}", Highlight: true));

        rows.Add(Row.Blank());

        string agg = sideAggregateReady == null ? "--"
            : (sideAggregateReady.Value ? "YES" : "NO");
        string aggLine = $"TEAM {AsciiSafe(sideDisplayName)}  READY: {agg}";
        if (forcedReady) aggLine += "  (FORCED)";
        rows.Add(Row.Of(Kind.Info, aggLine));

        rows.Add(Row.Of(Kind.Muted, "YOU: " + (ownReady ? "READY" : "NOT READY")));
        rows.Add(Row.Blank());

        if (players.Count == 0)
        {
            rows.Add(Row.Of(Kind.Muted, "WAITING FOR SAME-SIDE ROSTER..."));
        }
        else
        {
            foreach (var p in players)
            {
                string tag = p.Ready ? "[READY]" : "[WAIT ]";
                string suffix = p.IsSelf ? "  (you)" : "";
                rows.Add(new Row(Kind.Item,
                    $"{tag} {AsciiSafe(p.Name),-16}{suffix}",
                    Highlight: p.IsSelf));
            }
        }

        // P864d — native's TEAM LOBBY team row on the team screen: the lobby side
        // sits at the bottom of the team list (teamscreen.cpp:1330-1335) and its
        // player list is drawn by name with each player's status
        // (:378-386 count, :470-510 rows). Shown to EVERY viewer (the lobby's
        // members are mission members), which is exactly how a team leader sees
        // who is waiting to be accepted (:2998-3013).
        if (lobby is { Count: > 0 })
        {
            rows.Add(Row.Blank());
            rows.Add(Row.Of(Kind.Info, $"NOT ON A TEAM  (players: {lobby.Count})"));
            foreach (var m in lobby)
            {
                string status = m.RequestingSideName is { Length: > 0 } wanted
                    ? "Requesting " + AsciiSafe(wanted)
                    : "Not Playing";
                string suffix = m.IsSelf ? "  (you)" : "";
                rows.Add(new Row(Kind.Muted,
                    $"    {AsciiSafe(m.Name),-16}  {status}{suffix}"));
            }
        }

        return rows;
    }

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
