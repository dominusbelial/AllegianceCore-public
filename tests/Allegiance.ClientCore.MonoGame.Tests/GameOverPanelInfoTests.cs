using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P262 — pure-derivation tests for <see cref="GameOverPanelInfo"/>, the text
/// of the real game-over result screen. No graphics device (same discipline as
/// <see cref="HelpOverlayTests"/>). They pin the contract that earns a test:
/// the panel's title is native's won/lost/draw three-way resolved against the
/// wire's real <c>iSideWinner</c> and the local player's side, the winner line
/// is the wire's real side name, and the real reason string survives intact.
/// </summary>
public class GameOverPanelInfoTests
{
    private static GameOver Make(
        short winner, string reason, params (short Id, string Name)[] sides)
    {
        var arr = new SideEndgameInfo[GameOver.MaxSides];
        for (int i = 0; i < GameOver.MaxSides; i++)
            arr[i] = new SideEndgameInfo("<bug>", -1, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);
        foreach (var (id, name) in sides)
            arr[id] = new SideEndgameInfo(name, 1, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);
        return new GameOver
        {
            Sides = arr,
            GameoverMessage = reason,
            SideWinner = winner,
            NumSides = (byte)sides.Length,
            EjectPods = false,
        };
    }

    [Fact]
    public void Victory_WhenWinnerMatchesLocalSide()
    {
        var go = Make(winner: 1, reason: "Alpha won by outlasting all other sides.",
            (0, "Alpha"), (1, "Bravo"));
        var info = GameOverPanelInfo.From(go, mySideId: 1);

        Assert.Equal(GameOverOutcome.Victory, info.Outcome);
        Assert.Equal("YOU ARE VICTORIOUS!", info.Title);
        Assert.Equal("WINNER:  Bravo", info.WinnerLine);
        Assert.Equal("Alpha won by outlasting all other sides.", info.Reason);
    }

    [Fact]
    public void Defeat_WhenAnotherRealSideWins()
    {
        var go = Make(winner: 0, reason: "Alpha won by achieving the conquest/territory victory objective.",
            (0, "Alpha"), (1, "Bravo"));
        var info = GameOverPanelInfo.From(go, mySideId: 1);

        Assert.Equal(GameOverOutcome.Defeat, info.Outcome);
        Assert.Equal("YOU HAVE BEEN DEFEATED.", info.Title);
        Assert.Equal("WINNER:  Alpha", info.WinnerLine);
    }

    [Fact]
    public void Draw_WhenSideWinnerIsNA()
    {
        var go = Make(winner: GameOver.NA, reason: "The game was declared a draw",
            (0, "Alpha"), (1, "Bravo"));
        var info = GameOverPanelInfo.From(go, mySideId: 0);

        Assert.Equal(GameOverOutcome.Draw, info.Outcome);
        Assert.Equal("THE GAME IS A DRAW", info.Title);
        Assert.Equal("", info.WinnerLine);            // nothing to crown on a draw
        Assert.Equal("The game was declared a draw", info.Reason);
    }

    [Fact]
    public void UnknownLocalSide_WithRealWinner_ReadsAsDefeatNotDraw()
    {
        // The local player has no side yet (null) but a real winner exists —
        // must read as a defeat (somebody won), never collapse into a draw.
        var go = Make(winner: 0, reason: "Alpha won by outlasting all other sides.",
            (0, "Alpha"), (1, "Bravo"));
        var info = GameOverPanelInfo.From(go, mySideId: null);

        Assert.Equal(GameOverOutcome.Defeat, info.Outcome);
        Assert.Equal("YOU HAVE BEEN DEFEATED.", info.Title);
        Assert.Equal("WINNER:  Alpha", info.WinnerLine);
    }

    [Fact]
    public void MissingWinnerSideName_FallsBackToSideId()
    {
        // A real winner id whose rgSides record was never populated (e.g. the
        // "<bug>" placeholder) must not print "WINNER:" with nothing after it.
        var go = Make(winner: 2, reason: "side2 won");
        var info = GameOverPanelInfo.From(go, mySideId: 2);
        Assert.Equal("WINNER:  side2", info.WinnerLine);
    }

    [Fact]
    public void NonAsciiReason_IsScrubbedToAscii()
    {
        // The HUD spritefonts bake ASCII 32..126 only; any unicode char would
        // throw at draw time, so the resolver must scrub it up front.
        var go = Make(winner: 0, reason: "Bravo \u2014 won \u2713", (0, "Alpha"), (1, "Bravo"));
        var info = GameOverPanelInfo.From(go, mySideId: 1);
        Assert.All(info.Reason + info.Title + info.WinnerLine,
            ch => Assert.True(ch >= 32 && ch <= 126, $"non-ASCII U+{(int)ch:X4}"));
        Assert.DoesNotContain("\u2014", info.Reason);
    }
}
