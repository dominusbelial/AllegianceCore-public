using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P187 — the ballot/voting system (#resign / #draw / #mutiny chat commands,
/// BALLOT(17)/CANCEL_BALLOT(18)/VOTE(19) wire, per-mission pending-ballot state
/// and the four OnPassed effects). Scenarios: codec bytes, the three chat
/// triggers (non-leader mutiny creates + broadcasts; own-leader mutiny refused;
/// duplicate-mutiny refused), mutiny passing/failing leadership transfer, the
/// end-to-end #draw → OfferDraw → AcceptDraw → draw/game-over chain, the
/// resign-ballot per-player withdrawal substitution (must-fail-if-removed), a
/// VOTE from a non-eligible ship being ignored, and multi-mission + multi-side
/// scoping of the BALLOT broadcast. The native tally rule (verified from
/// <c>Ballot::HasPassed</c>/<c>AllVotesAreIn</c>/<c>Update</c>,
/// fsmission.cpp:5817-5875) is exercised through the all-votes-in resolution
/// path throughout.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class BallotVotingLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ballot Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ballot A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Ballot B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 851u, DwCookie = 0x851u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8520000000000000000ul, DrmHash = "ballot",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pump briefly so already-sent broadcasts settle before a test
    /// resets accumulators (see LiveTeamLeadershipTests.Flush).</summary>
    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Side SideOf(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    /// <summary>Establish <paramref name="a"/> as side0's leader+owner by
    /// joining the (initially empty) side, and move <paramref name="b"/> onto
    /// occupied side0 as a non-leader. Mirrors LiveTeamLeadershipTests.</summary>
    private static (short aId, short bId, short side0) SeatLeaderAndMate(Srv server, FltBot a, FltBot b, string na, string nb)
    {
        short aId = Seat(server, a, na);
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader+owner of side0
        short bId = Seat(server, b, nb);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // B onto occupied side0, not leader
        Flush(server);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);
        return (aId, bId, side0);
    }

    // ── codec bytes ────────────────────────────────────────────────────────

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
    private static uint ReadU32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));

    /// <summary>BALLOT(17) is the only variable-length member of the cluster.
    /// Natural-alignment layout (no enclosing pragma pack, Messages.h:170):
    /// header(4) + VarItem ib/cb@4-7 + otInitiator short@8 + oidInitiator
    /// short@10 + ballotID short@12 + 2 pad@14-15 + uint32 timeExpiration@16 +
    /// bool bHideToLeader@20 + 3 pad@21-23 = 24 fixed; BallotText blob @24.</summary>
    [Fact]
    public void Ballot_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)17, Ballot.Fmid);
        Assert.Equal(24, Ballot.FixedSize);
        var bl = new Ballot
        {
            BallotText = "mutiny!",
            OtInitiator = (short)Allegiance.Core.IgcObjectType.Ship,
            OidInitiator = 903,
            BallotId = 711,
            TimeExpiration = 8392000u,
            HideToLeader = true,
        };
        byte[] b = bl.Encode();
        Assert.Equal(24 + 8, b.Length);        // 24 fixed + "mutiny!\0" (8)
        Assert.Equal(32, ReadU16(b, 0));       // cbmsg = total
        Assert.Equal(17, ReadU16(b, 2));       // fmid
        Assert.Equal(24, ReadU16(b, 4));       // ib @4 (fixed end)
        Assert.Equal(8, ReadU16(b, 6));        // cb @6 ("mutiny!" + NUL)
        Assert.Equal(0, ReadI16(b, 8));        // otInitiator @8 (OT_ship = 0)
        Assert.Equal(903, ReadI16(b, 10));     // oidInitiator @10
        Assert.Equal(711, ReadI16(b, 12));     // ballotID @12
        Assert.Equal(0, ReadI16(b, 14));       // pad @14-15
        Assert.Equal(8392000u, ReadU32(b, 16));// timeExpiration @16
        Assert.Equal(1, b[20]);                // bHideToLeader @20
        Assert.Equal(0, b[21]); Assert.Equal(0, b[22]); Assert.Equal(0, b[23]);  // tail pad
        var d = Ballot.Decode(b);
        Assert.Equal("mutiny!", d.BallotText);
        Assert.Equal((short)Allegiance.Core.IgcObjectType.Ship, d.OtInitiator);
        Assert.Equal((short)903, d.OidInitiator);
        Assert.Equal((short)711, d.BallotId);
        Assert.Equal(8392000u, d.TimeExpiration);
        Assert.True(d.HideToLeader);
    }

    /// <summary>CANCEL_BALLOT(18): header(4) + ballotID short@4 = 6 fixed.
    /// VOTE(19): header(4) + ballotID short@4 + bool bAgree@6 + pad@7 = 8 fixed.</summary>
    [Fact]
    public void CancelBallot_and_Vote_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)18, CancelBallot.Fmid);
        Assert.Equal(6, CancelBallot.FixedSize);
        var cb = new CancelBallot { BallotId = 714 }.Encode();
        Assert.Equal(6, cb.Length);
        Assert.Equal(6, ReadU16(cb, 0));       // cbmsg
        Assert.Equal(18, ReadU16(cb, 2));      // fmid
        Assert.Equal(714, ReadI16(cb, 4));     // ballotID @4
        Assert.Equal((short)714, CancelBallot.Decode(cb).BallotId);

        Assert.Equal((ushort)19, Vote.Fmid);
        Assert.Equal(8, Vote.FixedSize);
        var vt = new Vote { BallotId = 719, Agree = true }.Encode();
        Assert.Equal(8, vt.Length);
        Assert.Equal(19, ReadU16(vt, 2));      // fmid
        Assert.Equal(719, ReadI16(vt, 4));     // ballotID @4
        Assert.Equal(1, vt[6]);                // bAgree @6
        Assert.Equal(0, vt[7]);                // pad @7
        var dv = Vote.Decode(vt);
        Assert.Equal((short)719, dv.BallotId);
        Assert.True(dv.Agree);
    }

    // ── the pure tally rule (PendingBallot), quoted from native ─────────────

    /// <summary>Drives PendingBallot directly with the exact HasPassed /
    /// AllVotesAreIn semantics (Ballot::HasPassed, fsmission.cpp:5836-5854;
    /// Ballot::AllVotesAreIn, fsmission.cpp:5867-5875): a side that saw any
    /// vote passes only if in-favor STRICTLY exceeds opposed; every voting side
    /// must pass for the whole ballot to pass.</summary>
    [Fact]
    public void PendingBallot_tally_matches_native_majority_rule()
    {
        var b = new PendingBallot { VotingSideId = 0 };
        b.AddEligibleVoter(1, 0);
        b.AddEligibleVoter(2, 0);
        b.AddEligibleVoter(3, 0);
        Assert.False(b.AllVotesAreIn());

        // 1 for / 1 against / 1 abstaining → not a majority → fails even before
        // everyone has voted (native Update resolves on all-votes-in OR expiry,
        // then applies HasPassed).
        Assert.True(b.TryCastVote(1, 0, true));
        Assert.True(b.TryCastVote(2, 0, false));
        Assert.False(b.HasPassed());
        Assert.False(b.AllVotesAreIn());

        // Final voter tips it: 2 for / 1 against → passes.
        Assert.True(b.TryCastVote(3, 0, true));
        Assert.True(b.AllVotesAreIn());
        Assert.True(b.HasPassed());

        // A second vote from the same ship is refused (removed from eligibility).
        Assert.False(b.TryCastVote(1, 0, true));
    }

    // ── #mutiny ─────────────────────────────────────────────────────────────

    /// <summary>⭐ A non-leader on a side with an existing leader issuing
    /// "#mutiny" creates a real ballot and broadcasts BALLOT(17) to the voting
    /// side (FedSrv.CPP:5184-5199).</summary>
    [Fact]
    public void Mutiny_from_non_leader_creates_ballot_and_broadcasts_to_side()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BM1", "BM2");
        server.Missions[0].Stage = MissionStage.Started;

        Flush(server);
        a.ResetBallotSignals();
        b.ResetBallotSignals();

        b.SendChat("#mutiny");   // B is a non-leader on side0
        int got = PumpUntil(server, () => a.Ballots.Count >= 1 && b.Ballots.Count >= 1, 800);
        Assert.True(got < 800, "non-leader #mutiny did not produce a BALLOT broadcast to the side");

        var bl = a.Ballots[0];
        Assert.Equal(bId, bl.OidInitiator);                       // proposer = B
        Assert.Equal((short)Allegiance.Core.IgcObjectType.Ship, bl.OtInitiator);
        Assert.Equal((short)a.Ballots[0].BallotId, b.Ballots[0].BallotId);   // same ballot to both voters
        Assert.True(bl.TimeExpiration > 0);
        Assert.Contains("mutiny", bl.BallotText, StringComparison.OrdinalIgnoreCase);
        Assert.True(bl.HideToLeader);                             // KGJV #110: mutiny hides the proposer
    }

    /// <summary>⭐ The side's OWN leader issuing "#mutiny" is refused — no
    /// ballot created (FedSrv.CPP:5185-5187 "leader cant mutiny").</summary>
    [Fact]
    public void Mutiny_from_own_leader_refused_no_ballot()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BL1", "BL2");
        server.Missions[0].Stage = MissionStage.Started;

        Flush(server);
        a.ResetBallotSignals();
        b.ResetBallotSignals();

        a.SendChat("#mutiny");   // A IS the leader of side0
        int settled = PumpUntil(server, () => false, 60);
        Assert.True(settled < 999);
        Assert.Empty(a.Ballots);
        Assert.Empty(b.Ballots);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);
    }

    /// <summary>⭐ A passing mutiny transfers leadership to the initiator,
    /// broadcasting SET_TEAM_LEADER; a failing mutiny leaves leadership
    /// unchanged. Driven through the real all-votes-in resolution (native
    /// MutinyBallot::OnPassed, fsmission.cpp:6117-6168).</summary>
    [Fact]
    public void Mutiny_passing_transfers_leadership_failing_keeps_it()
    {
        // Passing case.
        using (var server = new Srv())
        {
            server.Boot(Config());
            using var a = new FltBot();
            using var b = new FltBot();
            var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BP1", "BP2");
            server.Missions[0].Stage = MissionStage.Started;

            Flush(server);
            b.ResetBallotSignals();
            b.ResetLeadershipSignals();

            b.SendChat("#mutiny");
            int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
            Assert.True(got < 800, "mutiny ballot not created");
            short ballotId = b.Ballots[0].BallotId;

            // A (the only other eligible voter) votes YES → all votes in → passes.
            a.SendVote(ballotId, true);
            int transferred = PumpUntil(
                server,
                () => SideOf(server, side0).LeaderShipId == bId
                   && b.SetTeamLeaders.Any(t => t.ShipId == bId && t.SideId == side0),
                800);
            Assert.True(transferred < 800, "passing mutiny did not transfer leadership to the initiator");
            Assert.Equal(bId, SideOf(server, side0).LeaderShipId);
        }

        // Failing case.
        using (var server = new Srv())
        {
            server.Boot(Config());
            using var a = new FltBot();
            using var b = new FltBot();
            var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BF1", "BF2");
            server.Missions[0].Stage = MissionStage.Started;

            Flush(server);
            b.ResetBallotSignals();
            b.ResetLeadershipSignals();

            b.SendChat("#mutiny");
            int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
            Assert.True(got < 800, "mutiny ballot not created");
            short ballotId = b.Ballots[0].BallotId;

            // A votes AGAINST → 1 for / 1 against → not a majority → fails.
            a.SendVote(ballotId, false);
            b.ResetBallotSignals();
            // Resolution is asynchronous (next SimPass). Poll by re-issuing a
            // fresh #mutiny: it is refused ("already in progress") until the
            // failed ballot is dropped, and succeeds (new ballot) the moment it
            // is — proving the failing mutiny resolved and was removed.
            int resolved = PumpUntil(server, () =>
            {
                b.SendChat("#mutiny");
                return b.Ballots.Count >= 1;
            }, 800);
            Assert.True(resolved < 800, "failed mutiny ballot never resolved (fresh #mutiny still blocked)");
            Assert.Equal(aId, SideOf(server, side0).LeaderShipId);         // leadership unchanged
            Assert.Empty(b.SetTeamLeaders);
        }
    }

    /// <summary>⭐ A second "#mutiny" while one is in flight for the same side
    /// is refused with a private reply — no second ballot (FedSrv.CPP:5188-5193).</summary>
    [Fact]
    public void Second_mutiny_in_flight_refused_with_private_reply()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BR1", "BR2");
        server.Missions[0].Stage = MissionStage.Started;

        Flush(server);
        b.ResetBallotSignals();
        b.ResetChatReceived();

        b.SendChat("#mutiny");                       // first ballot, still pending (A hasn't voted)
        int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(got < 800, "first mutiny ballot not created");

        b.SendChat("#mutiny");                       // duplicate while in flight
        int settled = PumpUntil(server, () => b.Chats.Any(c => c.Message.Contains("Mutiny vote already in progress")), 800);
        Assert.True(settled < 800, "duplicate #mutiny was not refused with a private reply");

        Assert.Single(b.Ballots);                    // no second ballot
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);   // nothing resolved
    }

    /// <summary>⭐ A VOTE from a ship NOT eligible for a ballot (different
    /// side) is ignored: it does not resolve the ballot, and does not change
    /// leadership (native Ballot::CastVote, fsmission.cpp:5847-5865).</summary>
    [Fact]
    public void Vote_from_non_eligible_ship_is_ignored()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "BN1", "BN2");
        server.Missions[0].Stage = MissionStage.Started;

        // Seat C and move it onto side1 (the OTHER side).
        short cId = Seat(server, c, "BN3");
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side1));

        Flush(server);
        b.ResetBallotSignals();

        b.SendChat("#mutiny");                       // side0 ballot; eligible = {A, B}
        int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(got < 800, "mutiny ballot not created");
        short ballotId = b.Ballots[0].BallotId;

        // C is on side1 → not an eligible voter for this side0 ballot.
        c.SendVote(ballotId, true);
        int paused = PumpUntil(server, () => false, 60);
        Assert.True(paused < 900);

        // The invalid vote did NOT resolve the ballot: a fresh #mutiny is still
        // refused because the original is still in flight (would-be new ballot blocked).
        b.SendChat("#mutiny");
        int refused = PumpUntil(server, () => b.Chats.Any(x => x.Message.Contains("Mutiny vote already in progress")), 800);
        Assert.True(refused < 800, "original mutiny was unexpectedly resolved (invalid vote took effect)");
        Assert.Single(b.Ballots);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);

        // The legitimate voter (A) still resolves it.
        a.SendVote(ballotId, true);
        int transferred = PumpUntil(server, () => SideOf(server, side0).LeaderShipId == bId, 800);
        Assert.True(transferred < 800, "legitimate vote did not transfer leadership");
    }

    // ── #resign ─────────────────────────────────────────────────────────────

    /// <summary>⭐ A passing #resign removes EVERY remaining player on that
    /// side from the mission — the stated P187 substitution for native
    /// DeactivateSide (out of scope). Must-fail-if-removed: after resolution the
    /// side is empty, both players are gone from the roster, the leader is
    /// cleared, and the survivor observes QUIT_MISSION for the other.</summary>
    [Fact]
    public void Resign_passing_removes_every_player_on_side()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "RS1", "RS2");
        server.Missions[0].Stage = MissionStage.Started;

        Flush(server);
        b.ResetBallotSignals();
        b.ResetLeadershipSignals();

        b.SendChat("#resign");
        int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(got < 800, "resign ballot not created");
        short ballotId = b.Ballots[0].BallotId;

        // A (the only other eligible voter) agrees → all votes in → passes.
        a.SendVote(ballotId, true);
        int removed = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == aId || s.Ship.Id == bId)
               && b.QuitMissions.Any(q => q.ShipId == aId),
            800);
        Assert.True(removed < 800, "passed resign did not remove both players from the mission");

        // Both players are gone from the roster; the side is empty of players.
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == aId || s.Ship.Id == bId);
        Assert.Null(SideOf(server, side0).LeaderShipId);
        // The side was the mission owner (A established it); with no other
        // occupied side, ownership reverts to null.
        Assert.Null(server.Missions[0].MissionOwnerSideId);
        // B survived long enough to observe A's QUIT_MISSION (B is removed
        // second; whole-mission broadcast reaches the still-present B).
        Assert.Contains(b.QuitMissions, q => q.ShipId == aId);
    }

    // ── #draw end-to-end ────────────────────────────────────────────────────

    /// <summary>⭐ "#draw" creates an OfferDraw ballot scoped to the sender's
    /// own side; it passing creates an AcceptDraw ballot scoped to the OTHER
    /// side(s); that one passing triggers the reused P178 game-over path — a
    /// real end-to-end chain (OfferDrawBallot::OnPassed → AcceptDrawBallot::
    /// OnPassed → GameOver, fsmission.cpp:6182-6194), not stages in isolation.</summary>
    [Fact]
    public void Draw_end_to_end_offer_then_accept_then_game_over()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();   // Alpha (side0)
        using var b = new FltBot();   // Bravo (side1)
        short aId = Seat(server, a, "DW1");
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A on Alpha, leader
        short bId = Seat(server, b, "DW2");
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B on Bravo
        server.Missions[0].Stage = MissionStage.Started;

        Flush(server);
        a.ResetBallotSignals();
        b.ResetBallotSignals();

        // B offers a draw: OfferDraw ballot scoped to B's OWN side (Bravo, side1).
        b.SendChat("#draw");
        int offerSeen = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(offerSeen < 800, "offer-draw ballot not created");
        var offer = b.Ballots[0];
        Assert.Equal((short)Allegiance.Core.IgcObjectType.Ship, offer.OtInitiator);
        Assert.Equal(bId, offer.OidInitiator);

        // B is the only player on Bravo → auto-yes → the offer ballot passes in
        // the same pass, immediately creating the AcceptDraw ballot scoped to
        // the OTHER side (Alpha). The AcceptDraw BALLOT is broadcast WHOLE-mission
        // (native GetGroupRealSides), so both A and B receive it — but only A is
        // an eligible voter (B is on the offering side). A did NOT receive the
        // offer ballot (that was Bravo-only, side-scoped).
        int acceptSeen = PumpUntil(server, () => a.Ballots.Count >= 1, 800);
        Assert.True(acceptSeen < 800, "passing offer-draw did not create the accept-draw ballot for the other side");
        Assert.Equal(2, b.Ballots.Count);      // offer (side) + accept (mission-wide)
        Assert.Single(a.Ballots);             // A saw only the accept ballot

        // Identify the accept ballot by its side initiator marker.
        var accept = a.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side);
        Assert.Equal(side1, accept.OidInitiator); // the OFFERING side (Bravo) is the initiator
        Assert.False(accept.HideToLeader);

        // A (Alpha's only player) accepts → all votes in → the accept ballot
        // passes → the reused P178 draw outcome: the mission flips to Over.
        // P439 — the shared game-over path immediately ROTATES: _missions[0] is
        // replaced by a freshly-assembled NotStarted mission, so the "did the
        // game end?" assertion must target the ORIGINAL mission object (captured
        // before the vote), which retains Stage=Over — exactly native's
        // restartable-game-over (ResetMission + SetStage(STAGE_NOTSTARTED)).
        var missionUnderTest = server.Missions[0];
        a.SendVote(accept.BallotId, true);
        int over = PumpUntil(server, () => missionUnderTest.Stage == MissionStage.Over, 800);
        Assert.True(over < 800, "accepted draw did not end the mission");
        Assert.Equal(MissionStage.Over, missionUnderTest.Stage);

        // The accepting side's voter cannot double-vote / the offering side is
        // not eligible — both captured by the accept ballot's eligibility being
        // exactly Alpha's players (native sideIDPlayer != sideIDInitiator).
        // A already voted; a further Vote for the (now-resolved) ballot is a no-op.
        a.SendVote(accept.BallotId, false);
        int st = PumpUntil(server, () => false, 40);
        Assert.True(st < 990);
        Assert.Equal(MissionStage.Over, missionUnderTest.Stage);   // still Over — ignored, not a regression
    }

    // ── multi-mission scoping ───────────────────────────────────────────────

    /// <summary>⭐ Multi-mission scoping of the BALLOT broadcast: a #mutiny on
    /// mission A side0 must reach only mission A side0's peers — not a peer on
    /// mission B with the SAME numeric side id (the must-not-regress P180/P178
    /// fixed-bug class), and not a same-mission different-side peer.</summary>
    [Fact]
    public void Multi_mission_ballot_scopes_per_mission_and_per_side()
    {
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0 (LEADER — P548 auto-assign)
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id as a0's)
        using var a1 = new FltBot();   // mission A, side 1
        using var a0b = new FltBot();  // mission A, side 0 (NON-leader — the mutiny caster)
        short a0Id = Seat(server, a0, "MBA0");
        short b0Id = Seat(server, b0, "MBB0");
        short a1Id = Seat(server, a1, "MBA1");
        short a0bId = Seat(server, a0b, "MBAB");

        short mA0 = server.Missions[0].Sides[0].Id;
        short mB0 = server.Missions[1].Sides[0].Id;
        Assert.Equal(mA0, mB0);        // same numeric side id across missions

        // P548: a0 (first on mission A side0) is its auto-elected leader;
        // a0b (second onto the same side) is a non-leader — the valid mutiny caster.
        Assert.Equal(a0Id, server.Missions[0].Sides.First(s => s.Id == mA0).LeaderShipId);
        Assert.NotEqual(a0Id, a0bId);                                                    // distinct ships
        Assert.NotEqual(a0bId, server.Missions[0].Sides.First(s => s.Id == mA0).LeaderShipId); // a0b is NOT the leader

        server.Missions[0].Stage = MissionStage.Started;
        server.Missions[1].Stage = MissionStage.Started;

        Flush(server);
        a0.ResetBallotSignals();
        b0.ResetBallotSignals();
        a1.ResetBallotSignals();
        a0b.ResetBallotSignals();

        // a0b (mission A side0, NON-leader) mutinies; the leader a0 is the target.
        a0b.SendChat("#mutiny");
        int seen = PumpUntil(server, () => a0b.Ballots.Count >= 1, 800);
        Assert.True(seen < 800, "mission A side0 mutiny did not create a ballot");

        // Mission A side0's own peers heard it…
        Assert.Single(a0b.Ballots);
        Assert.Single(a0.Ballots);
        // …but mission B's same-numeric-side-id peer heard nothing (mission scope)…
        Assert.Empty(b0.Ballots);
        // …and mission A's OTHER side heard nothing (side scope).
        Assert.Empty(a1.Ballots);
    }

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (same pattern as LiveTeamLeadershipTests). Peer 0 → mission 0 /
    /// side 0; peer 1 → mission 1 / side 0 (SAME numeric side id as peer 0's,
    /// but a different mission); peer 2 → mission 0 / side 1; peer 3 → mission
    /// 0 / side 0 (a SECOND, non-leader peer on the same side as peer 0).</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq switch { 0 => 0, 1 => 0, 2 => 1, _ => 0 };
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 650f, 890f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }
}
