using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>How the local player came out of the mission — drives the title the
/// game-over panel shows (a port of native's won/lost/draw three-way, which
/// sets the screen's title string in <c>WinTrek/gameoverscreen.cpp:351-362</c>).</summary>
public enum GameOverOutcome
{
    /// <summary>Our side is <c>iSideWinner</c> — "You are victorious!".</summary>
    Victory,

    /// <summary>Some other real side won — "You have been defeated.".</summary>
    Defeat,

    /// <summary><c>iSideWinner == NA</c> (no surviving side) — a draw.</summary>
    Draw,
}

/// <summary>
/// P262 — the fully-resolved text for the on-screen game-over result panel.
/// Pure (no graphics), derived only from the real <see cref="GameOver"/> wire
/// snapshot plus the local player's own side id, so the MonoGame renderer is a
/// dumb layout and this derivation is unit-testable headlessly.
///
/// Faithfulness to native (<c>WinTrek/gameoverscreen.cpp</c>):
///  - the <b>title</b> is native's three-way — "You are victorious!" /
///    "You have been defeated." / "" — resolved from whether our side matches
///    <c>iSideWinner</c> (:351-362); the draw case uses a spelled-out title
///    since this port has no "won/drew/lost" client accumulator.
///  - the <b>reason</b> is native's <c>m_ptextReason = GetGameoverMessage()</c>
///    (:689) — the real <c>szGameoverMessage</c> string the server put on the
///    wire.
///  - the <b>winner line</b> is drawn from the wire's own <c>rgSides[
///    iSideWinner].sideName</c>, which native renders in its side-stats list
///    (the <c>SidePainter</c>, :245-255). Making it a bold banner is this
///    task's "err toward obvious/unmissable" requirement.
/// </summary>
public readonly record struct GameOverPanelInfo(
    GameOverOutcome Outcome,
    string Title,
    string WinnerLine,
    string Reason)
{
    /// <summary>Resolve the panel text from a real wire snapshot.
    /// <paramref name="mySideId"/> is the local player's own side
    /// (<see cref="Allegiance.ClientCore.State.ClientWorld.MySideId"/>), null
    /// before the client owns a ship. Every string is scrubbed to the HUD
    /// fonts' ASCII repertoire.</summary>
    public static GameOverPanelInfo From(GameOver go, short? mySideId)
    {
        bool isDraw = go.SideWinner == GameOver.NA;

        GameOverOutcome outcome = isDraw
            ? GameOverOutcome.Draw
            : (mySideId is { } mine && go.SideWinner == mine)
                ? GameOverOutcome.Victory
                : GameOverOutcome.Defeat;

        string title = outcome switch
        {
            GameOverOutcome.Victory => "YOU ARE VICTORIOUS!",
            GameOverOutcome.Defeat  => "YOU HAVE BEEN DEFEATED.",
            _                       => "THE GAME IS A DRAW",
        };

        // Winner side name straight from the wire's own rgSides array. Fall
        // back to "side{id}" when the record is absent — either the empty
        // string, or native's "<bug>" placeholder that GameOver.ForMission
        // fills every non-real side slot with (GameOver.cs:237) — so a
        // dangling "WINNER:" never prints.
        string winnerLine = "";
        if (!isDraw && go.SideWinner >= 0 && go.SideWinner < go.Sides.Length)
        {
            string name = go.Sides[go.SideWinner].SideName;
            if (string.IsNullOrEmpty(name) || name == "<bug>") name = $"side{go.SideWinner}";
            winnerLine = $"WINNER:  {name}";
        }

        return new GameOverPanelInfo(
            outcome,
            AsciiSafe(title),
            AsciiSafe(winnerLine),
            AsciiSafe(go.GameoverMessage));
    }

    private static string AsciiSafe(string s) =>
        Rendering.ChatLogFormat.AsciiSafe(s ?? "");
}
