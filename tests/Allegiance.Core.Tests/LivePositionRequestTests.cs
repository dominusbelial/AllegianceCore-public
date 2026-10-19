using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P184 — the player-initiated, leader-gated side-join position-request
/// workflow plus FORCE_TEAM_READY. The port of native's
/// POSITIONREQ(39/40)/POSITIONACK(41)/DELPOSITIONREQ(42)/FORCE_TEAM_READY(47)
/// flow (FedSrv.CPP:6658-6707, 6709-6747, 6847-…, 6987-7015, 7484-7516;
/// fsmission.cpp:4729-4774 RequestPosition, 4860-4960 AddJoinRequest/
/// RemoveJoinRequest, 5411-5448 SetForceReady/CheckForSideAllReady): a client
/// requests a side; an already-led side routes the request to its leader for
/// approval (broadcast POSITIONREQ(40) + pending-request bookkeeping), while a
/// leaderless side auto-accepts; the leader then accepts (seats the requester)
/// or rejects (DELPOSITIONREQ(DPR_Rejected)); the requester can self-cancel
/// (DELPOSITIONREQ(DPR_Canceled)); a departing requester's pending request is
/// dropped with a DELPOSITIONREQ(DPR_Rejected) broadcast on ReleasePeer; and a
/// leader can force their whole side ready, bypassing the per-player ready
/// check and potentially auto-starting the mission.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LivePositionRequestTests
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
            new Gm { Name = "Pos Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            new Gm { Name = "Pos A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Pos B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 847u, DwCookie = 0x847u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8490000000000000000ul, DrmHash = "pos",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

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

    private static short SideOfShip(Ship ship) => ship.SideId!.Value;

    private static Side Side(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (same pattern as LiveTeamLeadershipTests): peer 0 → mission 0 /
    /// side 0; peer 1 → mission 1 / side 0 (SAME numeric side id); peer 2 →
    /// mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 780f, 880f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
    private static int ReadI32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    // ── codec bytes ───────────────────────────────────────────────────────

    /// <summary>POSITIONREQ(39) is the single-{SideID} client request: header(4)
    /// + iSide short@4 = 6 bytes (2-aligned tail). POSITIONREQ(40) is the
    /// server's {ShipID, SideID} broadcast: header(4) + shipID@4 + iSide@6 =
    /// 8 bytes (same shape as JOIN_SIDE).</summary>
    [Fact]
    public void Position_request_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)39, PositionReq.Fmid);
        Assert.Equal(6, PositionReq.FixedSize);
        var pr = new PositionReq { SideId = 777 };
        byte[] b = pr.Encode();
        Assert.Equal(6, b.Length);
        Assert.Equal(6, ReadU16(b, 0));        // cbmsg
        Assert.Equal(39, ReadU16(b, 2));       // fmid
        Assert.Equal(777, ReadI16(b, 4));      // iSide @4
        Assert.Equal((short)777, PositionReq.Decode(b).SideId);

        Assert.Equal((ushort)40, PositionReqBroadcast.Fmid);
        Assert.Equal(8, PositionReqBroadcast.FixedSize);
        var pb = new PositionReqBroadcast { ShipId = 555, SideId = 778 }.Encode();
        Assert.Equal(8, pb.Length);
        Assert.Equal(8, ReadU16(pb, 0));
        Assert.Equal(40, ReadU16(pb, 2));
        Assert.Equal(555, ReadI16(pb, 4));     // shipID @4
        Assert.Equal(778, ReadI16(pb, 6));     // iSide @6
        var dpb = PositionReqBroadcast.Decode(pb);
        Assert.Equal((short)555, dpb.ShipId);
        Assert.Equal((short)778, dpb.SideId);
    }

    /// <summary>POSITIONACK(41) = {ShipID@4, SideID@6, bool@8, 1 pad@9} = 10 B;
    /// DELPOSITIONREQ(42) = {ShipID@4, SideID@6, int reason@8} = 12 B;
    /// FORCE_TEAM_READY(47) = {SideID@4, bool@6, 1 pad@7} = 8 B (same shape as
    /// TEAM_READY).</summary>
    [Fact]
    public void Ack_del_force_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)41, PositionAck.Fmid);
        Assert.Equal(10, PositionAck.FixedSize);
        var pa = new PositionAck { ShipId = 888, SideId = 779, FAccepted = true }.Encode();
        Assert.Equal(10, pa.Length);
        Assert.Equal(10, ReadU16(pa, 0));
        Assert.Equal(41, ReadU16(pa, 2));
        Assert.Equal(888, ReadI16(pa, 4));     // shipID @4
        Assert.Equal(779, ReadI16(pa, 6));     // iSide @6
        Assert.Equal(1, pa[8]);                // fAccepted true @8
        Assert.Equal(0, pa[9]);                // pad @9
        var dpa = PositionAck.Decode(pa);
        Assert.Equal((short)888, dpa.ShipId);
        Assert.Equal((short)779, dpa.SideId);
        Assert.True(dpa.FAccepted);

        Assert.Equal((ushort)42, DelPositionReq.Fmid);
        Assert.Equal(12, DelPositionReq.FixedSize);
        var dd = new DelPositionReq { ShipId = 889, SideId = 790, Reason = DelPositionReqReason.SideGone }.Encode();
        Assert.Equal(12, dd.Length);
        Assert.Equal(12, ReadU16(dd, 0));
        Assert.Equal(42, ReadU16(dd, 2));
        Assert.Equal(889, ReadI16(dd, 4));
        Assert.Equal(790, ReadI16(dd, 6));
        Assert.Equal((int)DelPositionReqReason.SideGone, ReadI32(dd, 8));
        var ddd = DelPositionReq.Decode(dd);
        Assert.Equal((short)889, ddd.ShipId);
        Assert.Equal((short)790, ddd.SideId);
        Assert.Equal(DelPositionReqReason.SideGone, ddd.Reason);

        Assert.Equal((ushort)47, ForceTeamReady.Fmid);
        Assert.Equal(8, ForceTeamReady.FixedSize);
        var ff = new ForceTeamReady { SideId = 791, FForceReady = true }.Encode();
        Assert.Equal(8, ff.Length);
        Assert.Equal(8, ReadU16(ff, 0));
        Assert.Equal(47, ReadU16(ff, 2));
        Assert.Equal(791, ReadI16(ff, 4));
        Assert.Equal(1, ff[6]);
        Assert.Equal(0, ff[7]);
        var dff = ForceTeamReady.Decode(ff);
        Assert.Equal((short)791, dff.SideId);
        Assert.True(dff.FForceReady);
    }

    /// <summary>The full native DelPositionReqReason enum round-trips (all 24
    /// values named), and the three this port actually sends have the right
    /// native values.</summary>
    [Fact]
    public void Del_position_req_reason_values_match_native()
    {
        Assert.Equal(0, (int)DelPositionReqReason.Rejected);
        Assert.Equal(1, (int)DelPositionReqReason.Canceled);
        Assert.Equal(14, (int)DelPositionReqReason.SideGone);
        Assert.Equal(23, (int)DelPositionReqReason.BotsOnTeam0Only);
        // a round-trip through the codec preserves any value
        var msg = new DelPositionReq { ShipId = 1, SideId = 2, Reason = DelPositionReqReason.BotsOnTeam0Only };
        Assert.Equal(DelPositionReqReason.BotsOnTeam0Only, DelPositionReq.Decode(msg.Encode()).Reason);
    }

    // ── POSITIONREQ: auto-accept on a leaderless side ─────────────────────

    /// <summary>⭐ Requesting a currently-empty (no-leader) side auto-accepts
    /// immediately: the requester is seated via the accept path (JOIN_SIDE /
    /// SET_TEAM_LEADER broadcasts), no POSITIONREQ(40) is emitted, and nothing
    /// is left pending. Native RequestPosition's leaderless fast path
    /// (fsmission.cpp:4757).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_side_request_auto_accepts()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var b = new FltBot();
        short bId = Seat(server, b, "PA1");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.Equal(side0, SideOfShip(ShipOf(server, bId)));   // spawn round-robins peer 0 → side 0

        b.SendPositionReq(side1);   // side1 is empty (no leader)
        int got = PumpUntil(server,
            () => SideOfShip(ShipOf(server, bId)) == side1 && b.JoinSides.Any(j => j.SideId == side1), 800);
        Assert.True(got < 800, "auto-accept did not seat the requester on the empty side");

        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));
        Assert.Equal(bId, Side(server, side1).LeaderShipId);          // first player → leader
        Assert.Contains(b.JoinSides, j => j.ShipId == bId && j.SideId == side1);
        Assert.Contains(b.SetTeamLeaders, t => t.ShipId == bId && t.SideId == side1);
        Assert.Contains(b.QuitSides, q => q.ShipId == bId && q.Reason == QuitSideReason.SwitchingSides);
        Assert.Empty(b.PositionReqs);                                 // no POSITIONREQ(40)
        Assert.Empty(b.DelPositionReqs);                              // nothing rejected
    }

    // ── POSITIONREQ on an occupied side: broadcast + pending, not seated ──

    /// <summary>⭐ Requesting an already-led side broadcasts POSITIONREQ(40)
    /// whole-mission and does NOT seat the requester yet (the leader must
    /// approve).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Occupied_side_request_broadcasts_and_does_not_seat()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PB1");
        short bId = Seat(server, b, "PB2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        // Establish A as side0's leader; B spawns on side1 (no leader).
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));

        Flush(server);
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // B requests the occupied side0.
        b.SendPositionReq(side0);
        int got = PumpUntil(server,
            () => a.PositionReqs.Any(p => p.ShipId == bId && p.SideId == side0)
               && b.PositionReqs.Any(p => p.ShipId == bId && p.SideId == side0), 800);
        Assert.True(got < 800, "occupied-side request did not broadcast POSITIONREQ(40)");

        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // NOT seated yet
        Assert.Empty(b.JoinSides);                              // no JOIN_SIDE
        Assert.Empty(b.SetTeamLeaders);
    }

    // ── POSITIONACK: accept / reject / non-leader ─────────────────────────

    /// <summary>⭐ The occupied side's leader ACCEPTING via POSITIONACK seats
    /// the requester (JOIN_SIDE) and clears the pending request (a second
    /// accept afterwards is a silent no-op).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_accept_seats_requester_and_clears_pending()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PC1");
        short bId = Seat(server, b, "PC2");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Flush(server);

        b.SendPositionReq(side0);                                      // B requests side0 (pending)
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendPositionAck(bId, side0, accepted: true);                 // A accepts B
        int got = PumpUntil(server,
            () => SideOfShip(ShipOf(server, bId)) == side0 && b.JoinSides.Any(j => j.ShipId == bId && j.SideId == side0), 800);
        Assert.True(got < 800, "accept did not seat the requester");

        Assert.Equal(side0, SideOfShip(ShipOf(server, bId)));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);           // A stays leader
        Assert.Contains(b.JoinSides, j => j.ShipId == bId && j.SideId == side0);
        Assert.DoesNotContain(b.DelPositionReqs, d => d.ShipId == bId); // no DELPOSITIONREQ on accept

        // Pending request is cleared: a SECOND accept by the leader is a no-op.
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();
        a.SendPositionAck(bId, side0, accepted: true);
        Flush(server);
        Assert.Empty(b.JoinSides);
        Assert.Empty(a.PositionReqs);
    }

    /// <summary>⭐ The leader REJECTING via POSITIONACK broadcasts
    /// DELPOSITIONREQ(DPR_Rejected) whole-mission and leaves the requester
    /// unseated.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_reject_broadcasts_and_leaves_unseated()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PD1");
        short bId = Seat(server, b, "PD2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Flush(server);

        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendPositionAck(bId, side0, accepted: false);                // A rejects B
        int got = PumpUntil(server,
            () => a.DelPositionReqs.Any(d => d.ShipId == bId && d.SideId == side0 && d.Reason == DelPositionReqReason.Rejected)
               && b.DelPositionReqs.Any(d => d.ShipId == bId && d.SideId == side0 && d.Reason == DelPositionReqReason.Rejected), 800);
        Assert.True(got < 800, "reject did not broadcast DELPOSITIONREQ(DPR_Rejected)");

        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // unseated
        Assert.Empty(b.JoinSides);
        // Pending cleared: a subsequent accept by the leader is a silent no-op.
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();
        a.SendPositionAck(bId, side0, accepted: true);
        Flush(server);
        Assert.Empty(b.JoinSides);
    }

    /// <summary>⭐ A NON-leader's POSITIONACK is refused: no state change, no
    /// broadcast, the pending request stays intact.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_leader_ack_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        using var b = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        // Seed A as side0's leader while it is empty; then B (requester → side1)
        // and C (non-leader → side0 via spawn round-robin) arrive.
        short aId = Seat(server, a, "PE1");
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, "PE2");   // peer1 → side1 (requester)
        short cId = Seat(server, c, "PE3");   // peer2 → side0 (non-leader)
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Flush(server);

        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId)
            && b.PositionReqs.Any(p => p.ShipId == bId)
            && c.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();
        c.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        c.SendPositionAck(bId, side0, accepted: true);   // C is NOT the leader
        // Same-peer ordered PING is a receive/dispatch barrier for the silent
        // refusal; fixed tick counts don't establish that the ACK was handled.
        const uint barrier = 0x184b;
        c.SendPing(barrier);
        Assert.True(PumpUntil(server, () => c.Pings.Any(p => p.TimeClient == barrier), 20000) < 20000,
            "non-leader ACK dispatch barrier did not arrive");

        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // still unseated
        Assert.Empty(c.JoinSides);
        Assert.Empty(c.PositionReqs);
        Assert.Empty(c.DelPositionReqs);
        Assert.Empty(b.JoinSides);

        // The pending request is STILL intact — the real leader can now accept it.
        a.SendPositionAck(bId, side0, accepted: true);
        int got = PumpUntil(server, () => SideOfShip(ShipOf(server, bId)) == side0, 800);
        Assert.True(got < 800, "real leader's accept after the refused non-leader ack did not seat B");
    }

    // ── DELPOSITIONREQ: self-cancel / foreign-cancel-refused / leader-reject ──

    /// <summary>⭐ The requester self-canceling via DELPOSITIONREQ(DPR_Canceled)
    /// clears the pending request (relayed whole-mission); the same message from
    /// someone else with DPR_Canceled is refused.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Self_cancel_clears_and_foreign_cancel_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        // Seed A as side0's leader while it is the only player there (P183 rule:
        // establish leadership on an EMPTY side, before teammates arrive). Then
        // seat B (requester → side1) and C (non-leader → side0 via the spawn
        // round-robin: peer N -> side N%2).
        short aId = Seat(server, a, "PF1");
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, "PF2");
        short cId = Seat(server, c, "PF3");
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Flush(server);

        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();
        c.ResetLeadershipSignals();

        // B cancels its OWN request with DPR_Canceled.
        b.SendDelPositionReq(bId, side0, DelPositionReqReason.Canceled);
        int cancelGot = PumpUntil(server, () => a.DelPositionReqs.Any(d => d.ShipId == bId && d.Reason == DelPositionReqReason.Canceled), 800);
        Assert.True(cancelGot < 800, "self-cancel was not relayed whole-mission");

        // Pending cleared: the leader's later accept is a no-op.
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();
        a.SendPositionAck(bId, side0, accepted: true);
        Flush(server);
        Assert.Empty(b.JoinSides);   // never seated
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));

        // A DIFFERENT peer (C, not the leader) sending DPR_Canceled for B is refused.
        c.ResetLeadershipSignals();
        a.ResetLeadershipSignals();
        c.SendDelPositionReq(bId, side0, DelPositionReqReason.Canceled);
        Flush(server);
        Assert.DoesNotContain(a.DelPositionReqs, d => d.ShipId == bId);   // no relay of the illegal cancel
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));
    }

    /// <summary>⭐ The leader rejecting directly via DELPOSITIONREQ(DPR_Rejected)
    /// (not via POSITIONACK) reaches the same end state: request cleared (relayed
    /// whole-mission) and the requester left unseated.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_reject_via_delpositionreq_reaches_same_end_state()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PG1");
        short bId = Seat(server, b, "PG2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Flush(server);

        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendDelPositionReq(bId, side0, DelPositionReqReason.Rejected);   // leader rejects directly
        int got = PumpUntil(server,
            () => b.DelPositionReqs.Any(d => d.ShipId == bId && d.SideId == side0 && d.Reason == DelPositionReqReason.Rejected), 800);
        Assert.True(got < 800, "leader reject via DELPOSITIONREQ not relayed");

        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // unseated
        Assert.Empty(b.JoinSides);
        // Pending cleared: subsequent accept no-op.
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();
        a.SendPositionAck(bId, side0, accepted: true);
        Flush(server);
        Assert.Empty(b.JoinSides);
    }

    // ── nonexistent side / disconnect ─────────────────────────────────────

    /// <summary>⭐ Requesting a nonexistent side gets an immediate
    /// DELPOSITIONREQ(DPR_SideGone) to the requester ONLY (not a whole-mission
    /// broadcast — other peers hear nothing).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nonexistent_side_request_gets_sidegone_to_requester_only()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PH1");
        short bId = Seat(server, b, "PH2");
        short side1 = server.Missions[0].Sides[1].Id;

        Flush(server);
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        short noSuchSide = 789;   // not a side in this mission
        b.SendPositionReq(noSuchSide);
        int got = PumpUntil(server,
            () => b.DelPositionReqs.Any(d => d.ShipId == bId && d.SideId == noSuchSide && d.Reason == DelPositionReqReason.SideGone), 800);
        Assert.True(got < 800, "requester did not receive DELPOSITIONREQ(DPR_SideGone)");

        Assert.Contains(b.DelPositionReqs, d => d.ShipId == bId && d.SideId == noSuchSide && d.Reason == DelPositionReqReason.SideGone);
        Assert.Empty(a.DelPositionReqs);            // NOT broadcast to others
        Assert.Empty(a.PositionReqs);
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // requester's own side unchanged
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: a requester disconnecting while their
    /// request is still pending broadcasts DELPOSITIONREQ(DPR_Rejected) on the
    /// ReleasePeer path (native RemoveJoinRequest(pfsPlayer, NULL) in
    /// RemovePlayerFromMission). Removing that wiring breaks this test.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Requester_disconnect_broadcasts_delpositionreq_rejected()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PI1");
        short bId = Seat(server, b, "PI2");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Flush(server);

        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(reqGot < 800, "pending request not broadcast");

        a.ResetLeadershipSignals();

        b.Dispose();   // abrupt drop while B's request is still pending
        int got = PumpUntil(server,
            () => a.DelPositionReqs.Any(d => d.ShipId == bId && d.SideId == side0 && d.Reason == DelPositionReqReason.Rejected), 800);
        Assert.True(got < 800, "departing requester's pending request was not broadcast as DELPOSITIONREQ(DPR_Rejected)");
        Assert.Contains(a.DelPositionReqs, d => d.ShipId == bId && d.SideId == side0 && d.Reason == DelPositionReqReason.Rejected);
    }

    // ── FORCE_TEAM_READY ──────────────────────────────────────────────────

    /// <summary>⭐ FORCE_TEAM_READY(true) from the side's leader marks the side
    /// ready — even with an unready player still connected on that side — and,
    /// since that is the only occupied side, auto-starts the mission. This is the
    /// discriminating case: the per-player ready check genuinely could NOT make
    /// the side ready (a teammate is explicitly unready), proving the force-ready
    /// branch is really bypassing it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Force_team_ready_true_auto_starts_even_with_unready_teammate()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "PK1");
        short cId = Seat(server, c, "PK2");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader on side0
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Flush(server);

        // A ready, C explicitly NOT ready → per-player check says side0 not-ready.
        a.SendPlayerReady(true, aId);
        c.SendPlayerReady(false, cId);
        Flush(server);
        Assert.False(Side(server, side0).Ready);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        a.ResetLeadershipSignals();
        c.ResetLeadershipSignals();

        // A (the leader) forces side0 ready despite C being unready.
        a.SendForceTeamReady(side0, true);
        int got = PumpUntil(server,
            () => Side(server, side0).Ready && a.EnterGames.Count > 0, 800);
        Assert.True(got < 800, "force-ready did not make the side ready and auto-start the mission");

        Assert.True(Side(server, side0).ForceReady);
        Assert.True(Side(server, side0).Ready);
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);   // auto-start triggered
        Assert.Contains(a.TeamReadys, t => t.SideId == side0 && t.FReady);
        Assert.Contains(a.EnterGames, _ => true);
    }

    /// <summary>⭐ FORCE_TEAM_READY(false) afterward re-applies the normal
    /// per-player check: with a connected player still unready, the side reverts
    /// to not-ready (TEAM_READY(false)).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Force_team_ready_false_reverts_to_normal_check()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "PL1");
        short cId = Seat(server, c, "PL2");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C unready on side0
        Flush(server);

        // Force ready → side ready regardless of C.
        a.SendForceTeamReady(side0, true);
        int fwd = PumpUntil(server, () => Side(server, side0).Ready, 800);
        Assert.True(fwd < 800, "force-true did not make the side ready");
        Assert.True(Side(server, side0).ForceReady);

        a.ResetLeadershipSignals();

        // Un-force → per-player check re-applies → C is unready → not-ready.
        a.SendForceTeamReady(side0, false);
        int rev = PumpUntil(server, () => a.TeamReadys.Any(t => t.SideId == side0 && !t.FReady), 800);
        Assert.True(rev < 800, "force-false did not revert to not-ready");
        Assert.False(Side(server, side0).ForceReady);
        Assert.False(Side(server, side0).Ready);
    }

    /// <summary>⭐ FORCE_TEAM_READY from a NON-leader is refused: the flag is
    /// not set and nothing is relayed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Force_team_ready_non_leader_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "PM1");
        short cId = Seat(server, c, "PM2");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leads side0
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader
        Flush(server);

        c.ResetLeadershipSignals();
        a.ResetLeadershipSignals();

        c.SendForceTeamReady(side0, true);   // C is NOT the leader
        Flush(server);

        Assert.False(Side(server, side0).ForceReady);
        Assert.False(Side(server, side0).Ready);
        Assert.Empty(c.ForceTeamReadys);     // no relay came back
        Assert.Empty(a.TeamReadys);
    }

    // ── multi-mission scoping ─────────────────────────────────────────────

    /// <summary>⭐ Multi-mission scoping (the P180/P178 must-not-regress bug
    /// class): a POSITIONREQ(40) broadcast and a FORCE_TEAM_READY relay/flag for
    /// mission A must never leak to a mission B peer sharing the same numeric
    /// side id, nor mutate mission B's side state.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_position_and_force_scope_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id)
        using var a1 = new FltBot();   // mission A, side 1
        short a0Id = Seat(server, a0, "MN0");
        short b0Id = Seat(server, b0, "MN1");
        short a1Id = Seat(server, a1, "MN2");

        short mA0 = server.Missions[0].Sides[0].Id;
        short mB0 = server.Missions[1].Sides[0].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        // a0 leads A-side0; b0 leads B-side0.
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), mB0));
        Flush(server);

        a0.ResetLeadershipSignals();
        b0.ResetLeadershipSignals();
        a1.ResetLeadershipSignals();

        // a1 (mission A) requests the occupied A-side0 → POSITIONREQ(40) to
        // mission A only; the mission B peer with the same side id must see nothing.
        a1.SendPositionReq(mA0);
        int got = PumpUntil(server,
            () => a0.PositionReqs.Any(p => p.ShipId == a1Id && p.SideId == mA0)
               && a1.PositionReqs.Any(p => p.ShipId == a1Id && p.SideId == mA0), 800);
        Assert.True(got < 800, "mission A POSITIONREQ(40) not broadcast to mission A");
        Assert.Empty(b0.PositionReqs);   // must-not-regress: mission B peer is blind

        // a0 forces A-side0 ready: relay + flag scoped to mission A.
        a0.ResetLeadershipSignals();
        b0.ResetLeadershipSignals();
        a0.SendForceTeamReady(mA0, true);
        int fgot = PumpUntil(server, () => server.Missions[0].Sides.First(s => s.Id == mA0).ForceReady, 800);
        Assert.True(fgot < 800, "force-ready did not apply to mission A side0");
        Assert.True(server.Missions[0].Sides.First(s => s.Id == mA0).ForceReady);
        Assert.False(server.Missions[1].Sides.First(s => s.Id == mB0).ForceReady);   // mission B untouched
        Assert.Empty(b0.ForceTeamReadys);   // no relay leaked to mission B
    }
}
