using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed class P713LobbyScreenStateTests
{
    [Theory]
    [InlineData((short)0, "command")]
    [InlineData((short)1, "attack")]
    [InlineData((short)2, "defend")]
    [InlineData((short)3, "escort")]
    [InlineData((short)4, "search")]
    [InlineData((short)5, "alpha")]
    [InlineData((short)6, "bravo")]
    [InlineData((short)7, "charlie")]
    [InlineData((short)8, "delta")]
    [InlineData((short)9, "echo")]
    public void Wing_assignment_updates_known_teammates_and_notifies_only_commanded_self(short wing, string name)
    {
        using var session = new ConnectionSession();
        P678LobbyMessageTests.Seat(session);
        P678LobbyMessageTests.Feed(session, new PlayerInfo { ShipId = 6, ISide = 0, CharacterName = "Peer" },
            new PlayerInfo { ShipId = 7, ISide = 1, CharacterName = "Enemy" },
            new SetWingId { ShipID = 5, WingId = wing, BCommanded = true });
        Assert.Equal(wing, session.World.Ships[5].WingId);
        var feedback = Assert.Single(session.World.TakeFeedbackLines());
        Assert.True(feedback.IsCritical);
        Assert.Equal($"You have been assigned to wing {name}", feedback.Text);
        Assert.Equal(feedback.Text, session.World.Events.Chats.Last().Message);
        P678LobbyMessageTests.Feed(session, new SetWingId { ShipID = 6, WingId = wing, BCommanded = true },
            new SetWingId { ShipID = 5, WingId = wing },
            new SetWingId { ShipID = 7, WingId = wing, BCommanded = true },
            new SetWingId { ShipID = 99, WingId = wing, BCommanded = true });
        Assert.Equal(wing, session.World.Ships[6].WingId);
        Assert.Equal(0, session.World.Ships[7].WingId);
        Assert.False(session.World.Ships.ContainsKey(99));
        Assert.Empty(session.World.TakeFeedbackLines());
        P678LobbyMessageTests.Feed(session, new SetWingId { ShipID = 5, WingId = -1, BCommanded = true },
            new SetWingId { ShipID = 5, WingId = 10, BCommanded = true });
        Assert.Equal(wing, session.World.Ships[5].WingId);
        Assert.Empty(session.World.TakeFeedbackLines());
        Assert.Empty(session.UnknownFmids);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Every_team_commander_can_toggle_both_directions_and_clears_own_afk()
    {
        Require.Artwork();
        using var session = new ConnectionSession();
        P678LobbyMessageTests.Feed(session, new MissionDef { SzIGCStaticFile = "PCore001" });
        P678LobbyMessageTests.Seat(session, 5, 1);
        P678LobbyMessageTests.Feed(session, new SetMissionOwner { ShipId = 99 },
            new MissionStageMessage { Stage = (byte)MissionStage.NotStarted });
        Assert.False(session.World.AmMissionOwner);
        var action = LobbyScreenState.ToggleTeamReady(session.World, ownReady: false);
        Assert.Equal(new LobbyScreenState.ForceReadyAction(5, 1, true, true), action);
        // Sending alone cannot change the checkbox; the authoritative echo does.
        session.SendForceTeamReady(true, 1);
        Assert.Equal(action, LobbyScreenState.ToggleTeamReady(session.World, false));
        P678LobbyMessageTests.Feed(session, new ForceTeamReady { SideId = 1, FForceReady = true });
        Assert.Equal(new LobbyScreenState.ForceReadyAction(5, 1, false, false),
            LobbyScreenState.ToggleTeamReady(session.World, ownReady: true));
        P678LobbyMessageTests.Feed(session, new SetTeamLeader { SideId = 1, ShipId = 7 });
        Assert.Null(LobbyScreenState.ToggleTeamReady(session.World, false));
        // Owning the mission does not confer command of this side.
        P678LobbyMessageTests.Feed(session, new SetMissionOwner { ShipId = 5 });
        Assert.Null(LobbyScreenState.ToggleTeamReady(session.World, false));
        P678LobbyMessageTests.Feed(session, new SetTeamLeader { SideId = 1, ShipId = 5 },
            new MissionStageMessage { Stage = (byte)MissionStage.Started });
        Assert.Null(LobbyScreenState.ToggleTeamReady(session.World, false));
        Assert.Empty(session.UnknownFmids);
    }

    [Fact]
    public void Eliminated_player_stays_in_picker_until_join_side_admits_an_active_team()
    {
        using var session = new ConnectionSession();
        Assert.False(LobbyScreenState.CanLeaveSideSelection(session.World, false));
        P678LobbyMessageTests.Seat(session);
        Assert.True(LobbyScreenState.CanLeaveSideSelection(session.World, false));
        P678LobbyMessageTests.Feed(session, new SideInactive { SideId = 0 });
        Assert.False(LobbyScreenState.CanLeaveSideSelection(session.World, true));
        session.SendPositionReq(1);
        Assert.False(LobbyScreenState.CanLeaveSideSelection(session.World, true));
        P678LobbyMessageTests.Feed(session, new JoinSide { ShipId = 99, SideId = 1 });
        Assert.False(LobbyScreenState.CanLeaveSideSelection(session.World, true));
        P678LobbyMessageTests.Feed(session, new JoinSide { ShipId = 5, SideId = -2 });
        Assert.False(LobbyScreenState.CanLeaveSideSelection(session.World, true));
        P678LobbyMessageTests.Feed(session, new JoinSide { ShipId = 5, SideId = 1 });
        Assert.True(LobbyScreenState.CanLeaveSideSelection(session.World, true));
        Assert.Empty(session.UnknownFmids);
    }
}
