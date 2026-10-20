using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P758 — the <c>QuitSideReason</c> wire enum is the native <c>QSR_*</c>
/// integers (<c>Messages.h:787-808</c>), and the booted player's own lobby
/// message is native <c>OnQuitMission</c>'s <c>QSR_*</c> → <c>strMessage</c>
/// switch (<c>trekigc.cpp:5265-5335</c>).
/// </summary>
public class P758QuitSideReasonTests
{
    [Theory]
    [InlineData(0, QuitSideReason.LeaderBooted)]
    [InlineData(1, QuitSideReason.OwnerBooted)]
    [InlineData(2, QuitSideReason.AdminBooted)]
    [InlineData(3, QuitSideReason.ServerShutdown)]
    [InlineData(4, QuitSideReason.SquadChange)]
    [InlineData(5, QuitSideReason.SideDestroyed)]
    [InlineData(6, QuitSideReason.RankLimits)]
    [InlineData(7, QuitSideReason.TeamSizeLimits)]
    [InlineData(8, QuitSideReason.Quit)]
    [InlineData(9, QuitSideReason.LinkDead)]
    [InlineData(10, QuitSideReason.DuplicateRemoteLogon)]
    [InlineData(11, QuitSideReason.DuplicateLocalLogon)]
    [InlineData(12, QuitSideReason.DuplicateCDKey)]
    [InlineData(13, QuitSideReason.SwitchingSides)]
    [InlineData(14, QuitSideReason.RandomizeSides)]
    [InlineData(15, QuitSideReason.FlushSides)]
    [InlineData(16, QuitSideReason.BalanceSides)]
    [InlineData(17, QuitSideReason.BannedBySteam)]
    public void QuitSideReason_values_match_native_QSR(int native, QuitSideReason value)
    {
        Assert.Equal(native, (int)value);
    }

    [Theory]
    [InlineData(QuitSideReason.LeaderBooted, "You have been booted by your commander! You can rejoin the game by logging in with a different ASGS callsign.")]
    [InlineData(QuitSideReason.OwnerBooted, "You have been lobby booted by the mission owner!  You can rejoin the game by logging in with a different ASGS callsign.")]
    [InlineData(QuitSideReason.AdminBooted, "You have been booted by a server administrator!")]
    [InlineData(QuitSideReason.ServerShutdown, "The game has been shut down by an administrator.")]
    [InlineData(QuitSideReason.DuplicateRemoteLogon, "Someone used your zone account to log into another game.")]
    [InlineData(QuitSideReason.DuplicateLocalLogon, "Someone used your zone account to log into the game you were playing.")]
    public void OwnBootMessage_matches_native_trekigc_text(QuitSideReason reason, string expected)
    {
        Assert.Equal(expected, ClientWorld.OwnBootMessage(reason));
    }

    [Theory]
    [InlineData(QuitSideReason.Quit)]
    [InlineData(QuitSideReason.LinkDead)]
    [InlineData(QuitSideReason.SwitchingSides)]
    [InlineData(QuitSideReason.RandomizeSides)]
    [InlineData(QuitSideReason.SquadChange)]
    [InlineData(QuitSideReason.SideDestroyed)]
    [InlineData(QuitSideReason.TeamSizeLimits)]
    [InlineData(QuitSideReason.DuplicateCDKey)]   // dynamic message travels on the wire
    [InlineData(QuitSideReason.BannedBySteam)]    // dynamic message travels on the wire
    public void OwnBootMessage_is_null_for_native_messageless_reasons(QuitSideReason reason)
    {
        Assert.Null(ClientWorld.OwnBootMessage(reason));
    }
}
