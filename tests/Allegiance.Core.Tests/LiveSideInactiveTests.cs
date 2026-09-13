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
/// P202 — SIDE_INACTIVE(68) pre-game roster shrinking: the mission owner marks
/// an *empty* side inactive/active before the mission starts, flipping the
/// mission-wide AllowEmptyTeams (AET) parameter when the last-active / first-
/// inactive transition warrants it. Port of FM_CS_SIDE_INACTIVE
/// (FedSrv.CPP:7379-7425), read in full this session.
/// <para><b>Scope boundary under test:</b> this is ONLY use #1 of native's
/// SIDE_INACTIVE machinery — the pre-game toggle. The in-game forced-
/// elimination flow (<c>DeactivateSide</c>, fsmission.cpp:5340-5380) is
/// explicitly out of scope (superseded by P187's resign substitution), and
/// P178's win-by-elimination (<c>CheckForVictoryByInactiveSides</c>) is left
/// untouched — it still detects side-emptiness via connected-peer presence and
/// never reads the new <see cref="Side.Active"/> field.</para>
/// <para>These tests use a 4-team mission (so empty sides 2 and 3 exist and
/// alliances have 3+ active candidates). [Collection("LiveNetwork")] keeps the
/// suite sequential with the others.</para>
/// </summary>
[Collection("LiveNetwork")]
public class LiveSideInactiveTests
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
            new Gm { Name = "SI", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Alpha", "Bravo", "Charlie", "Delta"] },
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
            new Gm { Name = "SI A", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Alpha", "Bravo", "Charlie", "Delta"] },
            new Gm { Name = "SI B", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Gamma", "Epsilon", "Zeta", "Eta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 873u, DwCookie = 0x873u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9480000000000000000ul, DrmHash = "al",
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

    /// <summary>SIDE_INACTIVE(68) = {sideID short@4, 3 bools@6,7,8, pad@9} = 10 B —
    /// outside Messages.h's pack(1) regions, so natural MSVC alignment (same
    /// family as LockSides/LockLobby P186). Client fills only sideID/bActive.</summary>
    [Fact]
    public void SideInactive_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)68, SideInactive.Fmid);
        Assert.Equal(10, SideInactive.FixedSize);

        var req = new SideInactive { SideId = 771, BActive = false }.Encode();
        Assert.Equal(10, req.Length);
        Assert.Equal(10, ReadU16(req, 0));   // cbmsg
        Assert.Equal(68, ReadU16(req, 2));   // fmid
        Assert.Equal(771, ReadI16(req, 4));  // sideID
        Assert.Equal(0, req[6]);             // bActive = false (client's own value)
        Assert.Equal(0, req[7]);             // bChangeAET untouched on the way in
        Assert.Equal(0, req[8]);             // bAET untouched on the way in
        Assert.Equal(0, req[9]);             // pad

        // A server reply fills the two AET fields and packs them in.
        var reply = new SideInactive { SideId = 772, BActive = true, BChangeAet = true, BAet = true }.Encode();
        Assert.Equal(1, reply[6]);
        Assert.Equal(1, reply[7]);
        Assert.Equal(1, reply[8]);

        var dr = SideInactive.Decode(reply);
        Assert.Equal((short)772, dr.SideId);
        Assert.True(dr.BActive);
        Assert.True(dr.BChangeAet);
        Assert.True(dr.BAet);

        var dq = SideInactive.Decode(req);
        Assert.Equal((short)771, dq.SideId);
        Assert.False(dq.BActive);
        Assert.False(dq.BChangeAet);
        Assert.False(dq.BAet);
    }

    // ── happy path ────────────────────────────────────────────────────────

    /// <summary>⭐ The mission owner deactivating an EMPTY side sets
    /// Side.Active=false, flips Mission.AllowEmptyTeams to true (it started
    /// false and this is the first inactive side), and broadcasts SideInactive
    /// whole-mission with the real bChangeAET=true / bAET=true reply fields —
    /// a real must-fail-if-removed proof.</summary>
    [Fact]
    public void Owner_deactivates_empty_side_flips_active_and_aet_whole_mission()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "SI1");
        short bId = Seat(server, b, "SI2");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        short side2 = m.Sides[2].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0 AND is the mission owner
        LeadEmptySide(server, b, bId, side1);   // B: a second peer on side1 (whole-mission witness)
        Flush(server);
        Assert.Equal(side0, m.MissionOwnerSideId);
        Assert.Equal(MissionStage.NotStarted, m.Stage);
        Assert.True(Side(server, m, side2).Active);
        Assert.False(m.AllowEmptyTeams);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendSideInactive(side2, false);
        int got = PumpUntil(server,
            () => !Side(server, m, side2).Active
               && m.AllowEmptyTeams
               && a.SideInactiveBroadcasts.Any(x => x.SideId == side2 && !x.BActive && x.BChangeAet && x.BAet)
               && b.SideInactiveBroadcasts.Any(x => x.SideId == side2 && !x.BActive && x.BChangeAet && x.BAet), 800);
        Assert.True(got < 800, "owner's SIDE_INACTIVE(side2,false) did not deactivate side2, flip AET, and broadcast whole-mission");

        Assert.False(Side(server, m, side2).Active);
        Assert.True(m.AllowEmptyTeams);
        // Whole-mission relay: both the sender and the mission peer saw it with the real AET reply.
        Assert.Contains(a.SideInactiveBroadcasts, x => x.SideId == side2 && !x.BActive && x.BChangeAet && x.BAet);
        Assert.Contains(b.SideInactiveBroadcasts, x => x.SideId == side2 && !x.BActive && x.BChangeAet && x.BAet);
    }

    /// <summary>⭐ Attempting to deactivate a side that CURRENTLY HAS a seated
    /// player is silently reverted, not refused: the broadcast reflects the
    /// side's real, unchanged bActive=true (and no AET flip occurs). Matching
    /// native's defensive re-check (FedSrv.CPP:7387-7390), not a harder refusal.</summary>
    [Fact]
    public void Deactivating_side_with_seated_player_is_silently_reverted()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "SI3");
        short bId = Seat(server, b, "SI4");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: mission owner
        LeadEmptySide(server, b, bId, side1);   // B seated on side1
        Flush(server);
        Assert.Equal(bId, Side(server, m, side1).LeaderShipId);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendSideInactive(side1, false);   // side1 is OCCUPIED -> revert, not refuse
        int got = PumpUntil(server,
            () => a.SideInactiveBroadcasts.Any(x => x.SideId == side1 && x.BActive && !x.BChangeAet)
               && b.SideInactiveBroadcasts.Any(x => x.SideId == side1 && x.BActive && !x.BChangeAet), 800);
        Assert.True(got < 800, "SIDE_INACTIVE(side1,false) did not broadcast a reverted (still-active) message");

        // Side stayed active; AET never flipped (no side is inactive).
        Assert.True(Side(server, m, side1).Active);
        Assert.False(m.AllowEmptyTeams);
        Assert.True(Side(server, m, side0).Active);
        // The broadcast carried the side's REAL, unchanged active state.
        Assert.Contains(a.SideInactiveBroadcasts, x => x.SideId == side1 && x.BActive && !x.BChangeAet);
        Assert.Contains(b.SideInactiveBroadcasts, x => x.SideId == side1 && x.BActive && !x.BChangeAet);
    }

    /// <summary>⭐ Reactivating the LAST inactive side flips AllowEmptyTeams
    /// back to false (AET on iff at least one side is inactive).</summary>
    [Fact]
    public void Reactivating_last_inactive_side_flips_aet_back_to_false()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "SI5");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side2 = m.Sides[2].Id;
        LeadEmptySide(server, a, aId, side0);   // A: mission owner
        Flush(server);

        // Deactivate side2 -> AET on.
        a.SendSideInactive(side2, false);
        int off = PumpUntil(server, () => !Side(server, m, side2).Active && m.AllowEmptyTeams, 800);
        Assert.True(off < 800, "first deactivation did not turn AET on");
        Assert.True(m.AllowEmptyTeams);

        a.ResetLeadershipSignals();

        // Reactivate side2 -> all sides active again -> AET flips back OFF.
        a.SendSideInactive(side2, true);
        int on = PumpUntil(server,
            () => Side(server, m, side2).Active
               && !m.AllowEmptyTeams
               && a.SideInactiveBroadcasts.Any(x => x.SideId == side2 && x.BActive && x.BChangeAet && !x.BAet), 800);
        Assert.True(on < 800, "reactivating the last inactive side did not flip AET back to false");
        Assert.True(Side(server, m, side2).Active);
        Assert.False(m.AllowEmptyTeams);
        Assert.Contains(a.SideInactiveBroadcasts, x => x.SideId == side2 && x.BActive && x.BChangeAet && !x.BAet);
    }

    // ── gates ─────────────────────────────────────────────────────────────

    /// <summary>⭐ A NON-owner — even another side's team leader — is refused:
    /// no state change, no broadcast (native TrapHackBoot(owner),
    /// FedSrv.CPP:7380).</summary>
    [Fact]
    public void Non_owner_refused_even_side_leader()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "SI6");
        short bId = Seat(server, b, "SI7");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        short side2 = m.Sides[2].Id;
        LeadEmptySide(server, a, aId, side0);   // A: side0 leader + mission owner
        LeadEmptySide(server, b, bId, side1);   // B: side1 leader, NOT the owner
        Flush(server);
        Assert.Equal(bId, Side(server, m, side1).LeaderShipId);
        Assert.Equal(side0, m.MissionOwnerSideId);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        b.SendSideInactive(side2, false);   // B is a team leader but not the mission owner
        Flush(server);

        Assert.True(Side(server, m, side2).Active);   // untouched
        Assert.False(m.AllowEmptyTeams);
        Assert.Empty(b.SideInactiveBroadcasts);       // no broadcast came back
        Assert.Empty(a.SideInactiveBroadcasts);
    }

    /// <summary>⭐ Once the mission has Started, even the owner is refused:
    /// unchanged, no broadcast (native only honors STAGE_NOTSTARTED,
    /// FedSrv.CPP:7380).</summary>
    [Fact]
    public void Refused_once_started()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "SI8");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side2 = m.Sides[2].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        m.Stage = MissionStage.Started;   // force the mission started

        a.ResetLeadershipSignals();
        a.SendSideInactive(side2, false);
        Flush(server);

        Assert.True(Side(server, m, side2).Active);   // unchanged
        Assert.False(m.AllowEmptyTeams);
        Assert.Empty(a.SideInactiveBroadcasts);       // no broadcast
    }

    // ── UpdateAlliances hook ──────────────────────────────────────────────

    /// <summary>⭐ Deactivating a side calls into UpdateAlliances (P190): a
    /// side that held an alliance has its membership cleared (and the group it
    /// belonged to dissolves), and a CHANGE_ALLIANCES(197) broadcast reflects
    /// the clear — the #ALLY hook of FedSrv.CPP:7424-7425.</summary>
    [Fact]
    public void Deactivating_a_side_clears_its_alliance_membership()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "SI9");
        short bId = Seat(server, b, "SI10");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        short side2 = m.Sides[2].Id;
        LeadEmptySide(server, a, aId, side0);   // A: mission owner
        LeadEmptySide(server, b, bId, side1);   // B on side1
        Flush(server);

        // Form an alliance between side0 and the (empty) side2.
        a.SendChangeAlliance(side0, side2);
        int formed = PumpUntil(server,
            () => Side(server, m, side0).AllyGroup == 0
               && Side(server, m, side2).AllyGroup == 0, 800);
        Assert.True(formed < 800, "alliance side0+side2 did not form");
        Assert.Equal((sbyte)0, Side(server, m, side2).AllyGroup);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // Deactivating side2 must clear its alliance (UpdateAlliances(side2, NA)).
        a.SendSideInactive(side2, false);
        int got = PumpUntil(server,
            () => !Side(server, m, side2).Active
               && Side(server, m, side2).AllyGroup == null
               && Side(server, m, side0).AllyGroup == null
               && b.AllianceBroadcasts.Any(x => x.Allies.All(v => v == -1)), 800);
        Assert.True(got < 800, "deactivating side2 did not clear its alliance membership");

        Assert.False(Side(server, m, side2).Active);
        Assert.Null(Side(server, m, side2).AllyGroup);   // the deactivated side's alliance is gone
        Assert.Null(Side(server, m, side0).AllyGroup);   // the group it was in dissolved
        Assert.Contains(b.AllianceBroadcasts, x => x.Allies.All(v => v == -1));   // broadcast reflects the clear
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

    /// <summary>⭐ Multi-mission scoping (the must-not-regress bug class): a
    /// SIDE_INACTIVE toggle and its whole-mission broadcast in mission A must
    /// never leak to, or alter, mission B, even though both share the same
    /// numeric side ids.</summary>
    [Fact]
    public void Multi_mission_side_inactive_scopes_per_mission()
    {
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0 (owner)
        using var b0 = new FltBot();   // mission B, side 0 (owner)
        using var a2 = new FltBot();   // mission A, side 1 (regular peer)
        short a0Id = Seat(server, a0, "SI11");
        short b0Id = Seat(server, b0, "SI12");
        short a2Id = Seat(server, a2, "SI13");

        Mission mA = server.Missions[0];
        Mission mB = server.Missions[1];
        short mA0 = mA.Sides[0].Id;
        short mB0 = mB.Sides[0].Id;
        short mA1 = mA.Sides[1].Id;
        short mA2 = mA.Sides[2].Id;   // empty side in mission A
        short mB2 = mB.Sides[2].Id;   // same numeric id, empty in mission B
        Assert.Equal(mA2, mB2);

        LeadEmptySide(server, a0, a0Id, mA0);   // A: mission-A owner
        LeadEmptySide(server, b0, b0Id, mB0);   // B: mission-B owner
        Flush(server);
        Assert.Equal(mA0, mA.MissionOwnerSideId);
        Assert.Equal(mB0, mB.MissionOwnerSideId);

        a0.ResetLeadershipSignals();
        b0.ResetLeadershipSignals();
        a2.ResetLeadershipSignals();

        // Mission-A owner deactivates mission-A's empty side 2.
        a0.SendSideInactive(mA2, false);
        int lg = PumpUntil(server,
            () => !Side(server, mA, mA2).Active
               && mA.AllowEmptyTeams
               && a0.SideInactiveBroadcasts.Any(x => x.SideId == mA2 && !x.BActive)
               && a2.SideInactiveBroadcasts.Any(x => x.SideId == mA2 && !x.BActive), 800);
        Assert.True(lg < 800, "mission-A SIDE_INACTIVE not applied and broadcast whole-mission");

        Assert.False(Side(server, mA, mA2).Active);
        Assert.True(mA.AllowEmptyTeams);

        // Must-not-regress: mission B's same-numbered side is untouched.
        Assert.True(Side(server, mB, mB2).Active);
        Assert.False(mB.AllowEmptyTeams);

        // The broadcast reached mission-A peers (sender + regular peer) but NOT
        // the mission-B peer.
        Assert.Contains(a0.SideInactiveBroadcasts, x => x.SideId == mA2 && !x.BActive);
        Assert.Contains(a2.SideInactiveBroadcasts, x => x.SideId == mA2 && !x.BActive);
        Assert.Empty(b0.SideInactiveBroadcasts);   // no leak to mission B
    }
}
