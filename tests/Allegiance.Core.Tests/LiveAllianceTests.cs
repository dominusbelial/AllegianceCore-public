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
/// P190 — team alliances: the mission owner's CHANGE_ALLIANCE(196) / the
/// server's CHANGE_ALLIANCES(197). Port of native FM_C_CHANGE_ALLIANCE
/// (FedSrv.CPP:7469-7481) + CFSMission::UpdateAlliances (fsmission.cpp:
/// 5638-5811), plus the deliberate owner-gate divergence stated in the report
/// (native does no owner check). Alliance groups form only with 3+ sides, so
/// these tests use a 4-team mission (the 4th side stays solo — native clears
/// ALL alliances when a single group covers every side with no solo left,
/// fsmission.cpp:5786-5792).
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveAllianceTests
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
            new Gm { Name = "Alliance", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Alpha", "Bravo", "Charlie", "Delta"] },
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
            new Gm { Name = "AL A", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Alpha", "Bravo", "Charlie", "Delta"] },
            new Gm { Name = "AL B", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Gamma", "Epsilon", "Zeta", "Eta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 847u, DwCookie = 0x847u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9450000000000000000ul, DrmHash = "al",
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

    private static Side Side(Srv server, Mission m, short sideId) =>
        m.Sides.First(s => s.Id == sideId);

    /// <summary>Establish peer <c>seq</c> as the leader of side 0 of the given
    /// mission by joining while the side is empty (P183 rule).</summary>
    private static void LeadEmptySide(Srv server, FltBot bot, short shipId, short sideId)
        => Assert.True(server.TryJoinSide(ShipOf(server, shipId), sideId));

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static sbyte[] AlliesOf(Mission m, params sbyte[] firstNSides)
    {
        var arr = Enumerable.Repeat((sbyte)-1, ChangeAlliances.SideAlliesMax).ToArray();
        for (int i = 0; i < firstNSides.Length; i++) arr[i] = firstNSides[i];
        return arr;
    }

    // ── codec bytes ───────────────────────────────────────────────────────

    /// <summary>CHANGE_ALLIANCE(196) = {SideID@4, SideID@6} = 8 B;
    /// CHANGE_ALLIANCES(197) = {char[6]@4} = 10 B (NA = -1 = 0xFF on the wire).</summary>
    [Fact]
    public void Alliance_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)196, ChangeAlliance.Fmid);
        Assert.Equal(8, ChangeAlliance.FixedSize);
        var ca = new ChangeAlliance { SideId = 770, SideAlly = -1 }.Encode();
        Assert.Equal(8, ca.Length);
        Assert.Equal(8, ReadU16(ca, 0));
        Assert.Equal(196, ReadU16(ca, 2));
        Assert.Equal(770, ReadI16(ca, 4));
        Assert.Equal(-1, ReadI16(ca, 6));
        var dca = ChangeAlliance.Decode(ca);
        Assert.Equal((short)770, dca.SideId);
        Assert.Equal((short)-1, dca.SideAlly);

        Assert.Equal((ushort)197, ChangeAlliances.Fmid);
        Assert.Equal(10, ChangeAlliances.FixedSize);
        Assert.Equal(6, ChangeAlliances.SideAlliesMax);
        var sbs = new sbyte[] { 0, -1, 2, -1, -1, -1 };
        var cas = new ChangeAlliances { Allies = sbs }.Encode();
        Assert.Equal(10, cas.Length);
        Assert.Equal(197, ReadU16(cas, 2));
        Assert.Equal(0, (sbyte)cas[4]);   // Allies[0] = 0
        Assert.Equal(-1, (sbyte)cas[5]);  // Allies[1] = NA -> 0xFF
        Assert.Equal(2, (sbyte)cas[6]);   // Allies[2] = 2
        Assert.Equal((byte)0xFF, cas[5]); // NA is the 0xFF wire byte
        var dcas = ChangeAlliances.Decode(cas);
        Assert.Equal(sbs, dcas.Allies);
    }

    // ── happy path ────────────────────────────────────────────────────────

    /// <summary>⭐ The mission owner allying side 0 to side 1 while the mission
    /// is NotStarted stores the group on both sides and broadcasts
    /// CHANGE_ALLIANCES(197) whole-mission ([0,0,NA,NA] — sides 2 and 3 solo).</summary>
    [Fact]
    public void Owner_change_alliance_pre_start_sets_and_broadcasts()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "AL1");
        short bId = Seat(server, b, "AL2");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        short side2 = m.Sides[2].Id;
        short side3 = m.Sides[3].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0 AND is the mission owner
        LeadEmptySide(server, b, bId, side1);
        Flush(server);
        Assert.Equal(side0, m.MissionOwnerSideId);
        Assert.Equal(MissionStage.NotStarted, m.Stage);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendChangeAlliance(side0, side1);
        int got = PumpUntil(server,
            () => Side(server, m, side0).AllyGroup == 0
               && Side(server, m, side1).AllyGroup == 0
               && a.AllianceBroadcasts.Any(x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0)))
               && b.AllianceBroadcasts.Any(x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0))), 800);
        Assert.True(got < 800, "owner's CHANGE_ALLIANCE(0,1) did not ally sides 0+1 and broadcast [0,0,NA,NA] whole-mission");

        Assert.Equal((sbyte)0, Side(server, m, side0).AllyGroup);
        Assert.Equal((sbyte)0, Side(server, m, side1).AllyGroup);
        Assert.Null(Side(server, m, side2).AllyGroup);   // solo
        Assert.Null(Side(server, m, side3).AllyGroup);   // solo
        Assert.Contains(a.AllianceBroadcasts, x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0)));   // sender included
        Assert.Contains(b.AllianceBroadcasts, x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0)));   // whole-mission relay
    }

    /// <summary>⭐ A NON-owner — even another side's team leader — is refused:
    /// no state change, no broadcast.</summary>
    [Fact]
    public void Owner_gate_refuses_non_owner_even_team_leader()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "AL3");
        short bId = Seat(server, b, "AL4");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: side0 leader + mission owner
        LeadEmptySide(server, b, bId, side1);   // B: side1 leader, NOT the owner
        Flush(server);
        Assert.Equal(bId, Side(server, m, side1).LeaderShipId);
        Assert.Equal(side0, m.MissionOwnerSideId);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        b.SendChangeAlliance(side1, side0);   // B is a team leader but not the mission owner
        Flush(server);

        Assert.Null(Side(server, m, side0).AllyGroup);   // untouched
        Assert.Null(Side(server, m, side1).AllyGroup);
        Assert.Empty(b.AllianceBroadcasts);                 // no broadcast came back
        Assert.Empty(a.AllianceBroadcasts);
    }

    /// <summary>⭐ Once the mission has Started, even the owner is refused:
    /// unchanged, no broadcast (native only honors STAGE_NOTSTARTED,
    /// FedSrv.CPP:7474).</summary>
    [Fact]
    public void Change_alliance_refused_once_started()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "AL5");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        m.Stage = MissionStage.Started;   // force the mission started

        a.ResetLeadershipSignals();
        a.SendChangeAlliance(side0, side1);
        Flush(server);

        Assert.Null(Side(server, m, side0).AllyGroup);   // unchanged
        Assert.Null(Side(server, m, side1).AllyGroup);
        Assert.Empty(a.AllianceBroadcasts);                 // no broadcast
    }

    /// <summary>⭐ Out-of-range sideID / sideAlly is refused (logged, ignored):
    /// no state change, no broadcast (native silently returns,
    /// fsmission.cpp:5642-5647).</summary>
    [Fact]
    public void Change_alliance_out_of_range_refused()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "AL6");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        a.ResetLeadershipSignals();

        // sideAlly out of range.
        a.SendChangeAlliance(side0, 99);
        Flush(server);
        Assert.Null(Side(server, m, side0).AllyGroup);
        Assert.Empty(a.AllianceBroadcasts);

        // sideID out of range (with a valid sideAlly).
        a.SendChangeAlliance(99, side0);
        Flush(server);
        Assert.Null(Side(server, m, side0).AllyGroup);
        Assert.Empty(a.AllianceBroadcasts);
    }

    // ── clear ─────────────────────────────────────────────────────────────

    /// <summary>⭐ sideAlly == NA clears an existing alliance for sideID: a prior
    /// [0,0,NA,NA] becomes all-NA and the broadcast reflects the clear.</summary>
    [Fact]
    public void Side_ally_na_clears_existing_alliance()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "AL7");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        a.ResetLeadershipSignals();

        a.SendChangeAlliance(side0, side1);
        int formed = PumpUntil(server, () => Side(server, m, side0).AllyGroup == 0, 800);
        Assert.True(formed < 800, "initial ally did not form");
        Assert.Equal((sbyte)0, Side(server, m, side1).AllyGroup);

        a.ResetLeadershipSignals();
        a.SendChangeAlliance(side0, -1);   // NA: clear side0's alliances
        int cleared = PumpUntil(server,
            () => Side(server, m, side0).AllyGroup == null
               && a.AllianceBroadcasts.Any(x => x.Allies.All(v => v == -1)), 800);
        Assert.True(cleared < 800, "CHANGE_ALLIANCE(0, NA) did not clear the alliance");

        Assert.Null(Side(server, m, side0).AllyGroup);
        Assert.Null(Side(server, m, side1).AllyGroup);   // the group dissolved, not just side0
        Assert.Contains(a.AllianceBroadcasts, x => x.Allies.All(v => v == -1));   // broadcast reflects the clear
    }

    // ── multi-side merge ──────────────────────────────────────────────────

    /// <summary>⭐ A REAL merge proof, not just the two-side happy path: two
    /// separate CHANGE_ALLIANCE calls fold sides 0,1,2 into one ally group,
    /// leaving side 3 solo — the resulting array is [0,0,0,NA], proving the
    /// group-id merge algorithm (not merely pairwise pairing) was ported.
    /// Native's validation (fsmission.cpp:5786-5792) clears everything if the
    /// single group swallowed every side with no solo left, hence the 4th solo
    /// side is required for this outcome.</summary>
    [Fact]
    public void Merge_three_sides_into_one_group_leaves_fourth_solo()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "AL8");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        short side2 = m.Sides[2].Id;
        short side3 = m.Sides[3].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        a.ResetLeadershipSignals();

        a.SendChangeAlliance(side0, side1);
        int first = PumpUntil(server, () => Side(server, m, side0).AllyGroup == 0, 800);
        Assert.True(first < 800, "first ally did not form");
        Assert.Equal((sbyte)0, Side(server, m, side1).AllyGroup);
        Assert.Null(Side(server, m, side2).AllyGroup);
        Assert.Null(Side(server, m, side3).AllyGroup);

        a.ResetLeadershipSignals();
        a.SendChangeAlliance(side0, side2);   // fold side 2 into side 0's group
        int merged = PumpUntil(server,
            () => Side(server, m, side2).AllyGroup == 0
               && a.AllianceBroadcasts.Any(x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0, 0))), 800);
        Assert.True(merged < 800, "second CHANGE_ALLIANCE did not merge side 2 into the group");

        Assert.Equal((sbyte)0, Side(server, m, side0).AllyGroup);
        Assert.Equal((sbyte)0, Side(server, m, side1).AllyGroup);
        Assert.Equal((sbyte)0, Side(server, m, side2).AllyGroup);   // all three in ONE group
        Assert.Null(Side(server, m, side3).AllyGroup);              // 4th side stays solo
        Assert.Contains(a.AllianceBroadcasts, x => x.Allies.SequenceEqual(AlliesOf(m, 0, 0, 0)));
    }

    // ── multi-mission scoping ─────────────────────────────────────────────

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (same pattern as P184/P186): peer 0 → mission 0 / side 0; peer 1 →
    /// mission 1 / side 0 (SAME numeric side id); peer 2 → mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 780f, 882f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>⭐ Multi-mission scoping (the P178/P180/P184 must-not-regress bug
    /// class): a CHANGE_ALLIANCE and its CHANGE_ALLIANCES broadcast in mission A
    /// must never leak to, or alter, mission B, even though both share the same
    /// numeric side ids.</summary>
    [Fact]
    public void Multi_mission_change_alliances_scopes_per_mission()
    {
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0 (owner)
        using var b0 = new FltBot();   // mission B, side 0 (owner)
        using var a2 = new FltBot();   // mission A, side 1 (regular peer)
        short a0Id = Seat(server, a0, "AL9");
        short b0Id = Seat(server, b0, "AL10");
        short a2Id = Seat(server, a2, "AL11");

        Mission mA = server.Missions[0];
        Mission mB = server.Missions[1];
        short mA0 = mA.Sides[0].Id;
        short mB0 = mB.Sides[0].Id;
        short mA1 = mA.Sides[1].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        LeadEmptySide(server, a0, a0Id, mA0);   // A: mission-A owner
        LeadEmptySide(server, b0, b0Id, mB0);   // B: mission-B owner
        Flush(server);
        Assert.Equal(mA0, mA.MissionOwnerSideId);
        Assert.Equal(mB0, mB.MissionOwnerSideId);

        a0.ResetLeadershipSignals();
        b0.ResetLeadershipSignals();
        a2.ResetLeadershipSignals();

        // A (mission A owner) allies mission-A sides 0 and 1.
        a0.SendChangeAlliance(mA0, mA1);
        int lg = PumpUntil(server,
            () => Side(server, mA, mA0).AllyGroup == 0
               && Side(server, mA, mA1).AllyGroup == 0
               && a0.AllianceBroadcasts.Any(x => x.Allies.SequenceEqual(AlliesOf(mA, 0, 0)))
               && a2.AllianceBroadcasts.Any(x => x.Allies.SequenceEqual(AlliesOf(mA, 0, 0))), 800);
        Assert.True(lg < 800, "mission-A CHANGE_ALLIANCE not applied and broadcast whole-mission");

        Assert.Equal((sbyte)0, Side(server, mA, mA0).AllyGroup);
        Assert.Equal((sbyte)0, Side(server, mA, mA1).AllyGroup);
        Assert.Null(Side(server, mA, mA.Sides[2].Id).AllyGroup);

        // Must-not-regress: mission B's same-numbered sides are untouched.
        Assert.Null(Side(server, mB, mB0).AllyGroup);
        Assert.Null(Side(server, mB, mB.Sides[1].Id).AllyGroup);

        // The broadcast reached mission-A peers (including the sender) but NOT
        // the mission-B peer.
        Assert.Contains(a0.AllianceBroadcasts, x => x.Allies.SequenceEqual(AlliesOf(mA, 0, 0)));
        Assert.Contains(a2.AllianceBroadcasts, x => x.Allies.SequenceEqual(AlliesOf(mA, 0, 0)));
        Assert.Empty(b0.AllianceBroadcasts);   // no leak to mission B
    }
}
