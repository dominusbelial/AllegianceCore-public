using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P678LobbyMessageTests
{
    internal static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));

    internal static void Seat(ConnectionSession session, short id = 5, short side = 0)
    {
        Feed(session, new SetCluster
        {
            SectorId = 0,
            Ship = ServerShipUpdate.FromValues(id, 0, default, 1, Vector3.Zero, Orientation.Identity,
                Vector3.Zero, new float[3], 1, 1, 1, 1, 1, 1, 1),
        }, new PlayerInfo { ShipId = id, ISide = side, CharacterName = "Leader" },
        new SetTeamLeader { SideId = side, ShipId = id });
    }

    [Fact]
    public void Lobby_settings_follow_each_echo_independently_and_never_mutate_optimistically()
    {
        using var session = new ConnectionSession();
        Feed(session, new LockLobby { FLock = true }, new AutoAccept { SideId = 0, FAutoAccept = true },
            new AutoAccept { SideId = 1, FAutoAccept = false });
        Assert.True(session.World.LobbyLocked);
        Assert.False(session.World.SidesLocked);
        Assert.True(session.World.AutoAcceptBySideId[0]);
        Assert.False(session.World.AutoAcceptBySideId[1]);
        session.SendLockLobby(false); // offline; sending is not receipt
        Assert.True(session.World.LobbyLocked);
        Feed(session, new LockSides { FLock = true }, new LockLobby { FLock = false },
            new AutoAccept { SideId = 0, FAutoAccept = false });
        Assert.False(session.World.LobbyLocked);
        Assert.True(session.World.SidesLocked);
        Assert.False(session.World.AutoAcceptBySideId[0]);
        Assert.Empty(session.UnknownFmids);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    public void Position_request_tracks_membership_and_gates_critical_notice_to_own_leader(bool leader, short requestedSide, bool notice)
    {
        using var session = new ConnectionSession();
        Seat(session);
        if (!leader) Feed(session, new SetTeamLeader { SideId = 0, ShipId = 99 });
        Feed(session, new PlayerInfo { ShipId = 9, ISide = -2, CharacterName = "Applicant" },
            new PositionReqBroadcast { ShipId = 9, SideId = requestedSide },
            new PositionReqBroadcast { ShipId = 9, SideId = requestedSide });
        Assert.Single(session.World.PendingPositionRequests);
        Assert.Equal(notice ? 2 : 0, session.World.FeedbackLines.Count(l => l.IsCritical && l.Text.Contains("Applicant wishes")));
        Assert.Equal(2, session.World.TakeSoundEvents().Count(e => e.Kind == ClientSoundEventKind.PlayerWaiting));
        Assert.Empty(session.World.TakeSoundEvents());
        // Native AddRequest excludes players already on the target side.
        Feed(session, new PositionReqBroadcast { ShipId = 5, SideId = 0 });
        Assert.DoesNotContain(new ClientWorld.PositionRequest(0, 5), session.World.PendingPositionRequests);
        Feed(session, new JoinSide { ShipId = 9, SideId = requestedSide });
        Assert.Empty(session.World.PendingPositionRequests);
    }

    [Theory]
    [InlineData(DelPositionReqReason.Rejected)]
    [InlineData(DelPositionReqReason.Canceled)]
    [InlineData(DelPositionReqReason.SidesLocked)]
    [InlineData(DelPositionReqReason.SideGone)]
    public void Removal_is_scoped_idempotent_and_retains_reason_even_without_a_player(DelPositionReqReason reason)
    {
        using var session = new ConnectionSession();
        Feed(session, new PositionReqBroadcast { ShipId = 8, SideId = 0 }, new PositionReqBroadcast { ShipId = 8, SideId = 1 },
            new PositionReqBroadcast { ShipId = 9, SideId = 0 }, new DelPositionReq { ShipId = 8, SideId = 0, Reason = reason });
        Assert.Equal(2, session.World.PendingPositionRequests.Count);
        Assert.Contains(new ClientWorld.PositionRequest(1, 8), session.World.PendingPositionRequests);
        Assert.Contains(new ClientWorld.PositionRequest(0, 9), session.World.PendingPositionRequests);
        Feed(session, new DelPositionReq { ShipId = 8, SideId = 0, Reason = reason });
        Assert.Equal(new ClientWorld.PositionRequestResult(0, 8, reason), session.World.Events.PositionRequestResults.Last());
        Assert.Null(session.World.MyPositionRequestResult);
        Assert.Equal(2, session.World.Events.PositionRequestResults.Count);
        Assert.Empty(session.World.Ships); // no phantom applicant
    }

    [Fact]
    public void Request_result_events_are_bounded()
    {
        using var session = new ConnectionSession();
        for (short id = 0; id <= EventLog.Bound; id++)
            Feed(session, new DelPositionReq { ShipId = id, SideId = 0, Reason = DelPositionReqReason.Rejected });
        Assert.Equal(EventLog.Bound, session.World.Events.PositionRequestResults.Count);
        Assert.Equal((short)1, session.World.Events.PositionRequestResults[0].ShipId);
    }

    [Fact]
    public void Cancel_removes_all_matching_active_and_queued_ballots_and_preserves_unrelated_votes()
    {
        using var session = new ConnectionSession();
        foreach (short id in new short[] { 1, 2, 1, 3 })
            Feed(session, new Ballot { BallotId = id, BallotText = $"vote {id}" });
        Feed(session, new CancelBallot { BallotId = 3 }, new CancelBallot { BallotId = 99 });
        Assert.Equal((short)1, session.World.ActiveBallot!.BallotId);
        Feed(session, new CancelBallot { BallotId = 1 });
        Assert.Equal((short)2, session.World.ActiveBallot!.BallotId);
        Feed(session, new CancelBallot { BallotId = 1 }, new CancelBallot { BallotId = 2 });
        Assert.Null(session.World.ActiveBallot);
        Assert.Empty(session.UnknownFmids);
    }

    [Fact]
    public void Ballot_expiration_is_strict_and_vote_advances_the_queue()
    {
        using var session = new ConnectionSession();
        Feed(session, new Ballot { BallotId = 1, TimeExpiration = 1000 }, new Ballot { BallotId = 2, TimeExpiration = 2000 });
        session.World.ObserveServerTime(1000, 50);
        Assert.Equal((short)1, session.World.GetActiveBallot(50)!.BallotId);
        Assert.Equal((short)2, session.World.GetActiveBallot(51)!.BallotId);
        session.SendVote(2, true);
        Assert.Null(session.World.GetActiveBallot(51));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconnect_and_rotation_clear_all_lobby_state(bool rotation)
    {
        using var session = new ConnectionSession();
        Feed(session, new LockLobby { FLock = true }, new LockSides { FLock = true },
            new AutoAccept { SideId = 0, FAutoAccept = true }, new PositionReqBroadcast { ShipId = 8, SideId = 0 },
            new DelPositionReq { ShipId = 9, SideId = 0 }, new Ballot { BallotId = 1 });
        if (rotation) session.World.BeginNextMission(); else session.World.Reset();
        Assert.False(session.World.LobbyLocked);
        Assert.False(session.World.SidesLocked);
        Assert.Empty(session.World.AutoAcceptBySideId);
        Assert.Empty(session.World.PendingPositionRequests);
        Assert.Empty(session.World.Events.PositionRequestResults);
        Assert.Null(session.World.MyPositionRequestResult);
        Assert.Null(session.World.ActiveBallot);
    }
}
