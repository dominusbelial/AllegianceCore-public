using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// M-2 regression tests for the B-key scoreboard toggle gate. While the docked
/// hangar/teleport screen is open, B is bound to "back to hangar"
/// (HandleDockedScreen), so the scoreboard toggle must not ALSO fire on the
/// same press. The gate therefore has to exclude the docked state alongside
/// the pre-existing composing exclusions (chat / quick-chat).
/// </summary>
public class ScoreboardToggleGateTests
{
    [Theory]
    //          chatActive qcMenuOpen dockedActive expected
    [InlineData(false,      false,     false,       true)]   // normal flight: B toggles scoreboard
    [InlineData(true,       false,     false,       false)]  // chat composing: blocked
    [InlineData(false,      true,      false,       false)]  // quick-chat menu open: blocked
    [InlineData(false,      false,     true,        false)]  // M-2 — docked/teleport pane open: blocked
    [InlineData(true,       true,      true,        false)]
    public void Gate_BlocksDockedAndComposingSurfaces(bool chatActive, bool qcMenuOpen, bool dockedActive, bool expected)
    {
        Assert.Equal(expected, Game1.ScoreboardToggleAllowed(chatActive, qcMenuOpen, dockedActive));
    }

    [Fact]
    public void DockedGate_IsTheM2RegressionGuard()
    {
        // Removing the !_dockedActive clause (the M-2 bug) makes the docked row
        // return true; this pins it to false so a re-bind can't regress.
        Assert.False(Game1.ScoreboardToggleAllowed(chatActive: false, qcMenuOpen: false, dockedActive: true));
    }
}
