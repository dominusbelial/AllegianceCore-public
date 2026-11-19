using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P1058 — the hangar's ready/launch gate. Pure tests of the one decision the
/// browser button (Classic and Modern share it) and the click guard use: which
/// own ship id the click may name, and every state that must disable it with a
/// reason instead of dropping the click.
/// </summary>
public sealed class HangarLaunchTests
{
    [Fact]
    public void Join_declared_ship_is_launchable_before_the_launch_pose_arrives()
    {
        // JOINED_MISSION declares the player's own ship (clintlib/appmsg.cpp:2150-2214)
        // while SET_CLUSTER's pose/cookie (appmsg.cpp:1009-1093) arrives later: the
        // ready click already has the native ship id (WinTrek.cpp:8192-8206 uses
        // GetShipID()), so this window must not be treated as "no ship".
        var readiness = HangarLaunch.Evaluate(hasSession: true, reconnecting: false,
            myShipId: null, joinedShipId: 8098, ownShipPresent: true);
        Assert.Equal(HangarLaunch.Blocker.None, readiness.Blocker);
        Assert.True(readiness.CanLaunch);
        Assert.Equal((short)8098, readiness.ShipId);
    }

    [Fact]
    public void Launch_pose_identity_wins_once_it_arrives()
    {
        var readiness = HangarLaunch.Evaluate(true, false, myShipId: 8099, joinedShipId: 8098, ownShipPresent: true);
        Assert.True(readiness.CanLaunch);
        Assert.Equal((short)8099, readiness.ShipId);
    }

    [Theory]
    [InlineData(true, false, null, null, false, HangarLaunch.Blocker.NoShipAssigned)] // no identity yet
    [InlineData(true, false, (short)8098, null, false, HangarLaunch.Blocker.NoShipAssigned)] // id outside the world table
    [InlineData(true, true, (short)8098, (short)8098, true, HangarLaunch.Blocker.Reconnecting)]     // between sockets
    [InlineData(false, false, (short)8098, (short)8098, true, HangarLaunch.Blocker.NoSession)]      // session retired
    public void Every_blocked_state_names_why_and_cannot_launch(bool hasSession, bool reconnecting,
        short? myShipId, short? joinedShipId, bool ownShipPresent, HangarLaunch.Blocker expected)
    {
        var readiness = HangarLaunch.Evaluate(hasSession, reconnecting, myShipId, joinedShipId, ownShipPresent);
        Assert.Equal(expected, readiness.Blocker);
        Assert.False(readiness.CanLaunch);
        Assert.Null(readiness.ShipId);
    }

    [Fact]
    public void Only_the_unblocked_state_reports_can_launch()
    {
        foreach (var blocker in Enum.GetValues<HangarLaunch.Blocker>())
        {
            var readiness = new HangarLaunch.Readiness(blocker, blockedShipId(blocker));
            Assert.Equal(blocker == HangarLaunch.Blocker.None, readiness.CanLaunch);
        }

        static short? blockedShipId(HangarLaunch.Blocker blocker) => blocker == HangarLaunch.Blocker.None ? (short)8098 : null;
    }

    [Fact]
    public void An_unblocked_readiness_without_a_ship_never_reports_can_launch()
        => Assert.False(new HangarLaunch.Readiness(HangarLaunch.Blocker.None, null).CanLaunch);
}
