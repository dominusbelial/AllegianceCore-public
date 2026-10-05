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
/// P183 — team/mission leadership + real side join/quit. The port of native
/// <c>AddPlayerToSide</c> (fsmission.cpp:539-661) as a directly-callable
/// <c>Server.TryJoinSide</c> side-switch capability, plus the
/// <c>RemovePlayerFromSide</c> leader-succession (fsmission.cpp:848-1099)
/// wired into the <c>ReleasePeer</c> disconnect path along with the previously
/// missing <c>QUIT_MISSION(124)</c> broadcast. Scenarios: codec bytes, first-on-
/// empty-side becomes leader+owner, second-on-occupied-side doesn't, the
/// side-switch broadcasts scoped per-mission (must-not-regress multi-mission),
/// leader succession on disconnect (deterministic "first other player"), owner
/// migration when the owner's side empties, owner→null with no broadcast when no
/// other side qualifies, and the must-fail-if-removed <c>QUIT_MISSION</c> on
/// every disconnect.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTeamLeadershipTests
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
            new Gm { Name = "Lead Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            new Gm { Name = "Lead A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Lead B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 841u, DwCookie = 0x841u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8430000000000000000ul, DrmHash = "lead",
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

    private static Side Side(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (the same pattern LiveTeamLobbyTests/InjectMultiMissionSpawn use).
    /// Peer 0 → mission 0 / side 0; peer 1 → mission 1 / side 0 (SAME numeric
    /// side id as peer 0's, but a different mission); peer 2 → mission 0 /
    /// side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
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

    /// <summary>Pump briefly so already-sent broadcasts (sent synchronously by a
    /// direct <c>TryJoinSide</c> call on the test thread) finish arriving at the
    /// bots' transports before the test resets accumulators or asserts on lists.
    /// Direct method calls don't ride the wire-driven pump the way real client
    /// actions do, so a <see cref="ResetLeadershipSignals"/> immediately after one
    /// can otherwise race an in-flight packet.</summary>
    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    // ── codec bytes ───────────────────────────────────────────────────────

    /// <summary>SET_TEAM_LEADER(121), SET_MISSION_OWNER(123) and JOIN_SIDE(125)
    /// all share the same <c>{sideID, shipID}</c> 8-byte layout
    /// (Messages.h:767-772 / 781-785 / 820-823): header(4) + sideID short@4 +
    /// shipID short@6.</summary>
    [Fact]
    public void Leadership_pair_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)121, SetTeamLeader.Fmid);
        Assert.Equal(8, SetTeamLeader.FixedSize);
        var tl = new SetTeamLeader { SideId = 301, ShipId = 55 };
        byte[] bt = tl.Encode();
        Assert.Equal(8, bt.Length);
        Assert.Equal(8, ReadU16(bt, 0));      // cbmsg
        Assert.Equal(121, ReadU16(bt, 2));    // fmid
        Assert.Equal(301, ReadI16(bt, 4));    // sideID @4
        Assert.Equal(55, ReadI16(bt, 6));     // shipID @6
        var dt = SetTeamLeader.Decode(bt);
        Assert.Equal((short)301, dt.SideId);
        Assert.Equal((short)55, dt.ShipId);

        Assert.Equal((ushort)123, SetMissionOwner.Fmid);
        var mo = new SetMissionOwner { SideId = 302, ShipId = 56 }.Encode();
        Assert.Equal(302, ReadI16(mo, 4));
        Assert.Equal(56, ReadI16(mo, 6));
        Assert.Equal((short)302, SetMissionOwner.Decode(mo).SideId);

        Assert.Equal((ushort)125, JoinSide.Fmid);
        var js = new JoinSide { SideId = 303, ShipId = 57 }.Encode();
        Assert.Equal(303, ReadI16(js, 4));
        Assert.Equal(57, ReadI16(js, 6));
        Assert.Equal((short)303, JoinSide.Decode(js).SideId);
    }

    /// <summary>QUIT_MISSION(124) and QUIT_SIDE(126) share the identical
    /// variable-length layout (Messages.h:812-818 / 825-829): header(4) +
    /// VarItem ib/cb@4-7 + shipID short@8 + 2 pad@10-11 + QuitSideReason
    /// int@12 = 16 fixed, then the CB_ZTS message blob. Pin the exact bytes
    /// and prove a non-empty trailing message round-trips.</summary>
    [Fact]
    public void Quit_codecs_round_trip_exact_bytes_with_message_blob()
    {
        Assert.Equal((ushort)124, QuitMission.Fmid);
        Assert.Equal(16, QuitMission.FixedSize);
        var qm = new QuitMission { ShipId = 42, Reason = QuitSideReason.LinkDead, Message = "bye" };
        byte[] b = qm.Encode();
        Assert.Equal(16 + 4, b.Length);        // 16 fixed + "bye\0" (4)
        Assert.Equal(20, ReadU16(b, 0));       // cbmsg = total
        Assert.Equal(124, ReadU16(b, 2));      // fmid
        Assert.Equal(16, ReadU16(b, 4));       // ibMessage @4 (fixed end)
        Assert.Equal(4, ReadU16(b, 6));        // cbMessage @6 ("bye" + NUL)
        Assert.Equal(42, ReadI16(b, 8));       // shipID @8
        Assert.Equal(0, ReadI16(b, 10));       // pad @10-11
        Assert.Equal((int)QuitSideReason.LinkDead, ReadI32(b, 12));   // reason @12
        var dm = QuitMission.Decode(b);
        Assert.Equal((short)42, dm.ShipId);
        Assert.Equal(QuitSideReason.LinkDead, dm.Reason);
        Assert.Equal("bye", dm.Message);

        Assert.Equal((ushort)126, QuitSide.Fmid);
        var qs = new QuitSide { ShipId = 43, Reason = QuitSideReason.SwitchingSides, Message = "" };
        byte[] bs = qs.Encode();
        Assert.Equal(16 + 1, bs.Length);       // 16 fixed + "\0" (1)
        Assert.Equal((int)QuitSideReason.SwitchingSides, ReadI32(bs, 12));
        var ds = QuitSide.Decode(bs);
        Assert.Equal((short)43, ds.ShipId);
        Assert.Equal(QuitSideReason.SwitchingSides, ds.Reason);
        Assert.Equal("", ds.Message);
    }

    // ── side-switch capability (TryJoinSide) ─────────────────────────────

    /// <summary>⭐ Joining an initially-empty side makes the joiner that side's
    /// leader, and (since no side owns the mission) also the mission owner —
    /// asserting the native rules (fsmission.cpp:584-613). The switch also
    /// emits QUIT_SIDE(SwitchingSides) for the old side then JOIN_SIDE plus
    /// the leadership broadcasts to the whole mission.</summary>
    [Fact]
    public void First_on_empty_side_becomes_leader_and_mission_owner()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "LF1");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.Equal(side0, SideOfShip(ShipOf(server, aId)));   // default spawner round-robins peer 0 → side 0

        // side1 is empty; switch A onto it.
        bool ok = server.TryJoinSide(ShipOf(server, aId), side1);
        Assert.True(ok);
        Assert.Equal(side1, SideOfShip(ShipOf(server, aId)));
        Assert.Equal(aId, Side(server, side1).LeaderShipId);
        Assert.Equal(side1, server.Missions[0].MissionOwnerSideId);

        Flush(server);
        // The switch quit side0 then joined side1 and became leader+owner.
        Assert.Contains(a.QuitSides, q => q.ShipId == aId && q.Reason == QuitSideReason.SwitchingSides);
        Assert.Contains(a.JoinSides, j => j.ShipId == aId && j.SideId == side1);
        Assert.Contains(a.SetTeamLeaders, t => t.ShipId == aId && t.SideId == side1);
        Assert.Contains(a.SetMissionOwners, m => m.ShipId == aId && m.SideId == side1);
    }

    /// <summary>⭐ A second player joining an already-occupied side does NOT
    /// become its leader — the existing leader stays (fsmission.cpp:601).</summary>
    [Fact]
    public void Second_on_occupied_side_does_not_become_leader()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "LG1");
        short bId = Seat(server, b, "LG2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        // Establish A as side0's leader (and mission owner).
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);

        Flush(server);
        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // B switches onto occupied side0.
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        Assert.Equal(side0, SideOfShip(ShipOf(server, bId)));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);   // A stays leader
        Flush(server);
        Assert.Empty(b.SetTeamLeaders);                        // B did not become leader
        Assert.Empty(b.SetMissionOwners);
        Assert.Contains(b.JoinSides, j => j.ShipId == bId && j.SideId == side0);
    }

    /// <summary>⭐ Multi-mission scoping: a side-switch/leadership broadcast for
    /// mission A must not leak to a peer on mission B that shares the same
    /// numeric side id (the must-not-regress P180/P178 fixed-bug class).</summary>
    [Fact]
    public void Multi_mission_leadership_scopes_per_mission()
    {
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id as a0's)
        using var a1 = new FltBot();   // mission A, side 1
        short a0Id = Seat(server, a0, "MLA0");
        short b0Id = Seat(server, b0, "MLB0");
        short a1Id = Seat(server, a1, "MLA1");

        short mA0 = server.Missions[0].Sides[0].Id;
        short mB0 = server.Missions[1].Sides[0].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions
        Assert.Equal(mA0, SideOfShip(ShipOf(server, a0Id)));
        Assert.Equal(mB0, SideOfShip(ShipOf(server, b0Id)));

        // Establish a0 as mission A side0's leader+owner. Only mission A peers
        // (a0, a1) may hear any of it.
        // P548 — a0 already auto-elected itself owner of mission A (its own
        // join path election) before a1 seated, so that original broadcast did
        // not reach a1. Clear the owner so this explicit re-join re-broadcasts
        // SET_MISSION_OWNER to the now-present a1 (preserving the original
        // "a1 hears a0's ownership" intent).
        server.Missions[0].MissionOwnerSideId = null;
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));
        Flush(server);

        // Mission B's peer saw absolutely nothing: its OWN auto-join broadcasts
        // (JOIN_SIDE / SET_TEAM_LEADER / SET_MISSION_OWNER) go out before the
        // peer is registered in _peerShips, and mission A's broadcasts are
        // mission-scoped — so nothing reaches it.
        Assert.Empty(b0.JoinSides);
        Assert.Empty(b0.SetTeamLeaders);
        Assert.Empty(b0.SetMissionOwners);
        Assert.Empty(b0.QuitMissions);
        // …yet mission B's OWN first auto-assigned player (b0) legitimately
        // elected itself owner of mission B (P548 routes auto-assign through
        // the join path; native AddPlayerToSide elects the owner when no side
        // owns the mission, fsmission.cpp:584-592). This is b0's own join, not
        // a leak from mission A (whose operations only touch mission A).
        Assert.Equal(mB0, server.Missions[1].MissionOwnerSideId);

        // Mission A's other peer saw the leadership announcements.
        Assert.Contains(a1.SetTeamLeaders, t => t.ShipId == a0Id && t.SideId == mA0);
        Assert.Contains(a1.SetMissionOwners, m => m.ShipId == a0Id && m.SideId == mA0);
    }

    // ── leader succession on disconnect (ReleasePeer) ────────────────────

    /// <summary>⭐ The team leader disconnecting with two teammates still on the
    /// side promotes the deterministic "first other player" (lowest ship id) —
    /// asserted exactly, not just "someone" — and broadcasts SET_TEAM_LEADER.</summary>
    [Fact]
    public void Leader_disconnect_promotes_first_other_player_exactly()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "LH1");   // first seated → smallest id
        short side0 = server.Missions[0].Sides[0].Id;
        // P439 — A's departure leaves only side0 occupied, so win-by-elimination
        // fires DeclareGameOver → rotation replaces _missions[0]. Assert the
        // succession on the ORIGINAL mission object.
        var mission0 = server.Missions[0];

        // Establish A as side0's leader+owner while it is the only player on the
        // (currently empty) side — so A is unambiguously the leader before B/C join.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);

        // Seat B and C, then move them onto occupied side0 (neither becomes leader).
        short bId = Seat(server, b, "LH2");
        short cId = Seat(server, c, "LH3");
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.True(aId < bId && bId < cId, "seat order must yield aId < bId < cId");

        Flush(server);
        b.ResetLeadershipSignals();

        a.Dispose();   // abrupt drop → ReleasePeer → leader succession
        int settled = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == aId) && b.SetTeamLeaders.Any(t => t.ShipId == bId && t.SideId == side0), 800);
        Assert.True(settled < 800, "leader departure did not promote the first other player");

        Assert.Equal(bId, mission0.Sides.First(s => s.Id == side0).LeaderShipId);   // exact successor
        Assert.Contains(b.SetTeamLeaders, t => t.ShipId == bId && t.SideId == side0);
    }

    /// <summary>⭐ The team leader disconnecting as the LAST player on their
    /// side clears LeaderShipId; because they were also the mission owner and
    /// another occupied side exists, that side's existing leader is promoted to
    /// mission owner (broadcasting SET_MISSION_OWNER).</summary>
    [Fact]
    public void Leader_last_on_side_migrates_mission_owner_to_other_occupied_side()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "LM1");
        short bId = Seat(server, b, "LM2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        // P439 — A's departure leaves only side1 occupied, so win-by-elimination
        // fires DeclareGameOver → rotation replaces _missions[0]. Assert the
        // ownership migration on the ORIGINAL mission object.
        var mission0 = server.Missions[0];

        // A becomes side0's leader+owner.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);

        // B becomes side1's leader (empty side), but NOT owner (side0 already owns it and has players).
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));
        Assert.Equal(bId, Side(server, side1).LeaderShipId);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);

        Flush(server);
        b.ResetLeadershipSignals();

        a.Dispose();   // A was the last player on owner-side side0
        int settled = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == aId) && b.SetMissionOwners.Any(m => m.ShipId == bId && m.SideId == side1), 800);
        Assert.True(settled < 800, "owner migration to the other occupied side did not happen");

        Assert.Null(mission0.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, mission0.Sides.First(s => s.Id == side1).LeaderShipId);
        Assert.Equal(side1, mission0.MissionOwnerSideId);
        Assert.Contains(b.SetMissionOwners, m => m.ShipId == bId && m.SideId == side1);
    }

    /// <summary>⭐ P548: with auto-assign now routed through the join path, the
    /// second player (auto-seated onto the OTHER empty side) IS a qualified
    /// leader. So when the owner-side leader A leaves (emptying side0), the
    /// mission-owner role migrates to B's side1 (SET_MISSION_OWNER broadcast,
    /// native RemovePlayerFromSide fsmission.cpp:996-1028) and, the mission
    /// being Started, the win-by-inactivity check ends the game (side1 wins).
    /// Formerly (pre-P548) an auto-assigned B was NOT a leader, so no other
    /// qualified side existed and ownership went null — the behavior this test
    /// originally asserted is exactly the P548 bug.</summary>
    [Fact]
    public void Owner_side_leader_leaves_migrates_owner_to_auto_elected_leader_and_ends_game()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "LN1");
        short bId = Seat(server, b, "LN2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        var mission0 = server.Missions[0];

        // P548 auto-assign: A owns side0, B owns side1 (each first onto an
        // empty side; native AddPlayerToSide fsmission.cpp:584-613).
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(side0, mission0.MissionOwnerSideId);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));
        Assert.Equal(bId, Side(server, side1).LeaderShipId);   // auto-assigned B IS a qualified leader (P548)
        Assert.Equal(side0, mission0.MissionOwnerSideId);      // A's side still owns the mission

        Flush(server);
        server.Missions[0].Stage = MissionStage.Started;       // a real started game
        b.ResetLeadershipSignals();

        a.Dispose();   // A was the last player on owner-side side0
        int settled = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == aId)
               && b.SetMissionOwners.Any(m => m.ShipId == bId && m.SideId == side1), 800);
        Assert.True(settled < 800, "owner migration to the auto-elected leader (side1) did not happen");

        // Original mission: side0's leader cleared; ownership migrated to side1.
        Assert.Null(mission0.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(side1, mission0.MissionOwnerSideId);
        Assert.Contains(b.SetMissionOwners, m => m.ShipId == bId && m.SideId == side1);
        // Win-by-inactivity ends the game (only side1 has players).
        Assert.Equal(MissionStage.Over, mission0.Stage);
    }

    /// <summary>⭐ Single player (leader+owner) on the only occupied side
    /// disconnects: both roles clear to null (state-only assertions — no one is
    /// left to observe a broadcast, and none is sent).</summary>
    [Fact]
    public void Sole_leader_owner_disconnect_clears_both_roles()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "LO1");

        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);

        a.Dispose();
        int settled = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == aId), 800);
        Assert.True(settled < 800, "sole leader not released");

        Assert.Null(Side(server, side0).LeaderShipId);
        Assert.Null(server.Missions[0].MissionOwnerSideId);
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: ANY disconnect broadcasts QUIT_MISSION
    /// to the whole mission's remaining peers — a real, previously-completely-
    /// missing signal (this disconnect path used to send nothing).</summary>
    [Fact]
    public void Any_disconnect_broadcasts_quit_mission_to_remaining_peers()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "LQ1");
        short bId = Seat(server, b, "LQ2");

        Flush(server);
        b.ResetLeadershipSignals();

        a.Dispose();   // non-leader, spawn-seated — no succession, just QUIT_MISSION
        int got = PumpUntil(server,
            () => b.QuitMissions.Any(q => q.ShipId == aId && q.Reason == QuitSideReason.LinkDead), 800);
        Assert.True(got < 800, "survivor did not receive QUIT_MISSION for the departed peer");
        Assert.Equal(QuitSideReason.LinkDead, Assert.Single(b.QuitMissions).Reason);
    }

    /// <summary>⭐ A departing NON-leader triggers NO leader-succession
    /// broadcasts at all — only QUIT_MISSION. The side's leader is untouched.</summary>
    [Fact]
    public void Non_leader_departure_broadcasts_only_quit_mission()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "LS1");
        short bId = Seat(server, b, "LS2");

        short side0 = server.Missions[0].Sides[0].Id;
        // P439 — B's departure empties side1, so the win-by-elimination path
        // fires DeclareGameOver, which ROTATES _missions[0] to a fresh mission.
        // Assert on the ORIGINAL mission object, which retains the leadership.
        var mission0 = server.Missions[0];

        // Establish A as side0 leader; move B onto side0 as a non-leader.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        Assert.Equal(aId, Side(server, side0).LeaderShipId);

        Flush(server);
        a.ResetLeadershipSignals();

        b.Dispose();   // B is NOT the leader
        int got = PumpUntil(server,
            () => a.QuitMissions.Any(q => q.ShipId == bId), 800);
        Assert.True(got < 800, "survivor did not receive QUIT_MISSION");

        Assert.Equal(aId, mission0.Sides.First(s => s.Id == side0).LeaderShipId);   // leader unchanged
        // The departed NON-leader b never led anything — on the ORIGINAL mission
        // no succession occurred. P480 then MIGRATES the survivor a into a fresh
        // mission, where its own re-seat makes it the fresh side0 leader (with a
        // NEW ship id); that is not a succession, so b never appears in any
        // leadership announcement and a hears QUIT_MISSION for b.
        Assert.DoesNotContain(a.SetTeamLeaders, t => t.ShipId == bId);
        Assert.DoesNotContain(a.SetMissionOwners, m => m.ShipId == bId);
        Assert.Contains(a.QuitMissions, q => q.ShipId == bId && q.Reason == QuitSideReason.LinkDead);
    }

    private static short SideOfShip(Ship ship) => ship.SideId!.Value;
}
