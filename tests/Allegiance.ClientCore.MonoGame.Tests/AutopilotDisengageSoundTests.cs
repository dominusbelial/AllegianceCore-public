namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P756/S35 — pins the autopilot-disengage sound decision so the production
/// handler cannot accidentally double-play or fire when autopilot was already
/// off. The full <see cref="Game1"/> cannot be instantiated headlessly, so this
/// tests the exact rule the private <c>DisengageAutopilot</c> method uses
/// (<c>WinTrek/WinTrek.cpp:8315</c> plays <c>salAutopilotDisengageSound</c> only
/// when the autopilot was actually engaged).
/// </summary>
public class AutopilotDisengageSoundTests
{
    [Fact]
    public void DisengageCue_Fires_WhenAutopilotWasEngaged()
        => Assert.True(Game1.ShouldPlayAutopilotDisengageCue(wasEngaged: true));

    [Fact]
    public void DisengageCue_Silences_WhenAutopilotWasAlreadyOff()
        => Assert.False(Game1.ShouldPlayAutopilotDisengageCue(wasEngaged: false));
}
