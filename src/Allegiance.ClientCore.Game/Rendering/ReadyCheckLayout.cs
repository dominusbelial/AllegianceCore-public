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
        int? launchCountdown = null)
    {
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "READY CHECK"));
        rows.Add(Row.Of(Kind.Hint, "Y TOGGLE READY"));
        if (canForceTeamReady) rows.Add(Row.Of(Kind.Hint, "O TOGGLE TEAM READY"));

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

        return rows;
    }

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
