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
/// P186 — the leader-gated team-lobby configuration toggles. The port of
/// native's LOCK_LOBBY(43)/LOCK_SIDES(44) (mission-owner gated), AUTO_ACCEPT(52)
/// (side-leader gated, seats pending requesters when turned on) and
/// CHANGE_TEAM_CIV(51) (side-leader gated, pre-start only, changes the side's
/// civilization), plus the real LockSides hook in the P184 POSITIONREQ flow
/// (FedSrv.CPP:6938-6962, 6908-6936, 7344-7367; fsmission.cpp:4590, 4974-5011,
/// 5268-5284). SET_TEAM_INVESTOR(122) is confirmed dead in native (only
/// Messages.h + Wopr interop bindings) and is deliberately not built.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTeamLobbyToggleTests
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
            new Gm { Name = "Toggle", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            new Gm { Name = "TG A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "TG B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 846u, DwCookie = 0x846u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8450000000000000000ul, DrmHash = "tg",
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

    /// <summary>Establish peer <c>seq</c> as the leader of side 0 of the given
    /// mission by joining while the side is empty (P183 rule).</summary>
    private static void LeadEmptySide(Srv server, FltBot bot, short shipId, short sideId)
        => Assert.True(server.TryJoinSide(ShipOf(server, shipId), sideId));

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    // ── codec bytes ───────────────────────────────────────────────────────

    /// <summary>LOCK_LOBBY(43) = {bool@4, 1 pad@5} = 6 B; LOCK_SIDES(44) same;
    /// AUTO_ACCEPT(52) = {SideID@4, bool@6, 1 pad@7} = 8 B; CHANGE_TEAM_CIV(51) =
    /// {SideID@4, CivID@6, bool@8, 1 pad@9} = 10 B.</summary>
    [Fact]
    public void Toggle_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)43, LockLobby.Fmid);
        Assert.Equal(6, LockLobby.FixedSize);
        var ll = new LockLobby { FLock = true }.Encode();
        Assert.Equal(6, ll.Length);
        Assert.Equal(6, ReadU16(ll, 0));
        Assert.Equal(43, ReadU16(ll, 2));
        Assert.Equal(1, ll[4]);     // fLock @4
        Assert.Equal(0, ll[5]);     // pad @5
        Assert.True(LockLobby.Decode(ll).FLock);

        Assert.Equal((ushort)44, LockSides.Fmid);
        Assert.Equal(6, LockSides.FixedSize);
        var lls = new LockSides { FLock = true }.Encode();
        Assert.Equal(6, lls.Length);
        Assert.Equal(44, ReadU16(lls, 2));
        Assert.Equal(1, lls[4]);
        Assert.True(LockSides.Decode(lls).FLock);

        Assert.Equal((ushort)52, AutoAccept.Fmid);
        Assert.Equal(8, AutoAccept.FixedSize);
        var aa = new AutoAccept { SideId = 771, FAutoAccept = true }.Encode();
        Assert.Equal(8, aa.Length);
        Assert.Equal(52, ReadU16(aa, 2));
        Assert.Equal(771, ReadI16(aa, 4));
        Assert.Equal(1, aa[6]);
        Assert.Equal(0, aa[7]);
        var daa = AutoAccept.Decode(aa);
        Assert.Equal((short)771, daa.SideId);
        Assert.True(daa.FAutoAccept);

        Assert.Equal((ushort)51, ChangeTeamCiv.Fmid);
        Assert.Equal(10, ChangeTeamCiv.FixedSize);
        var cc = new ChangeTeamCiv { SideId = 772, CivId = 773, Random = true }.Encode();
        Assert.Equal(10, cc.Length);
        Assert.Equal(51, ReadU16(cc, 2));
        Assert.Equal(772, ReadI16(cc, 4));
        Assert.Equal(773, ReadI16(cc, 6));
        Assert.Equal(1, cc[8]);     // random @8
        Assert.Equal(0, cc[9]);     // pad @9
        var dcc = ChangeTeamCiv.Decode(cc);
        Assert.Equal((short)772, dcc.SideId);
        Assert.Equal((short)773, dcc.CivId);
        Assert.True(dcc.Random);
    }

    // ── LOCK_LOBBY ────────────────────────────────────────────────────────

    /// <summary>⭐ The mission owner locking the lobby relays LOCK_LOBBY(43)
    /// whole-mission (sender included) and sets Mission.LockLobby.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lobby_lock_from_owner_relays_and_sets_flag()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TL1");
        short bId = Seat(server, b, "TL2");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0 AND is the mission owner (no owner yet)
        Flush(server);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);
        Assert.Equal(aId, Side(server, side0).LeaderShipId);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendLockLobby(true);
        int got = PumpUntil(server,
            () => server.Missions[0].LockLobby && a.LockLobbys.Any(x => x.FLock), 800);
        Assert.True(got < 800, "owner's LOCK_LOBBY(true) did not set Mission.LockLobby and relay");
        Assert.True(SpinWait.SpinUntil(() => a.LockLobbys.Any(x => x.FLock)
            && b.LockLobbys.Any(x => x.FLock), TimeSpan.FromSeconds(30)),
            "both peers must decode the whole-mission lobby lock before reset");

        Assert.True(server.Missions[0].LockLobby);
        Assert.Contains(a.LockLobbys, x => x.FLock);
        Assert.Contains(b.LockLobbys, x => x.FLock);   // whole-mission relay reaches the other peer too

        // Unlock.
        a.ResetLeadershipSignals();
        a.SendLockLobby(false);
        int ugot = PumpUntil(server, () => !server.Missions[0].LockLobby, 800);
        Assert.True(ugot < 800, "owner's LOCK_LOBBY(false) did not clear Mission.LockLobby");
        Assert.True(SpinWait.SpinUntil(() => a.LockLobbys.Any(x => !x.FLock)
            && b.LockLobbys.Any(x => !x.FLock), TimeSpan.FromSeconds(30)),
            "both peers must decode the whole-mission lobby unlock");
    }

    /// <summary>⭐ A non-owner — even another side's TEAM LEADER — is refused:
    /// Mission.LockLobby stays false and nothing is relayed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lobby_lock_from_non_owner_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TM1");
        short bId = Seat(server, b, "TM2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: side0 leader + mission owner
        LeadEmptySide(server, b, bId, side1);   // B: side1 leader, NOT the owner
        Flush(server);
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(bId, Side(server, side1).LeaderShipId);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        b.SendLockLobby(true);   // B is a team leader but not the mission owner
        Flush(server);

        Assert.False(server.Missions[0].LockLobby);   // flag untouched
        Assert.Empty(b.LockLobbys);                   // no relay came back
        Assert.Empty(a.LockLobbys);
    }

    // ── LOCK_SIDES ────────────────────────────────────────────────────────

    /// <summary>⭐ The mission owner locking sides relays LOCK_SIDES(44)
    /// whole-mission and sets Mission.LockSides; a non-owner (another side's
    /// leader) is refused.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sides_lock_from_owner_relays_and_non_owner_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TN1");
        short bId = Seat(server, b, "TN2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: owner
        LeadEmptySide(server, b, bId, side1);   // B: non-owner leader
        Flush(server);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendLockSides(true);
        int got = PumpUntil(server, () => server.Missions[0].LockSides, 800);
        Assert.True(got < 800, "owner's LOCK_SIDES(true) did not set Mission.LockSides");
        Assert.True(SpinWait.SpinUntil(() => a.LockSidess.Any(x => x.FLock)
            && b.LockSidess.Any(x => x.FLock), TimeSpan.FromSeconds(30)),
            "both peers must decode the whole-mission sides lock before reset");
        Assert.True(server.Missions[0].LockSides);
        Assert.Contains(b.LockSidess, x => x.FLock);   // whole-mission relay

        b.ResetLeadershipSignals();
        a.ResetLeadershipSignals();

        // B (non-owner) tries to unlock — refused, flag stays true.
        b.SendLockSides(false);
        Flush(server);
        const uint marker = 0x186b;
        b.SendPing(marker);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            server.TickSim();
            return b.Pings.Any(p => p.TimeClient == marker);
        }, TimeSpan.FromSeconds(30)), "ordered echo must follow the refused non-owner unlock");
        Assert.True(server.Missions[0].LockSides);     // unchanged
        Assert.Empty(b.LockSidess);                    // no relay
    }

    /// <summary>⭐ The REAL effect of LockSides: while on, a side-switch
    /// POSITIONREQ is refused to the requester only with
    /// DELPOSITIONREQ(DPR_SidesLocked); once unlocked, the existing P184
    /// behavior (leader-gated queued request) is restored — a real regression
    /// check against P184's own assumptions.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sides_lock_refuses_side_switch_and_unlock_restores_p184()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TO1");
        short bId = Seat(server, b, "TO2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: side0 leader (B spawns on side1)
        Flush(server);
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));

        a.SendLockSides(true);
        int lg = PumpUntil(server, () => server.Missions[0].LockSides, 800);
        Assert.True(lg < 800, "LOCK_SIDES(true) not applied");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // B switches from side1 to the occupied side0 — REFUSED while locked.
        b.SendPositionReq(side0);
        int dg = PumpUntil(server,
            () => b.DelPositionReqs.Any(d => d.SideId == side0 && d.Reason == DelPositionReqReason.SidesLocked), 800);
        Assert.True(dg < 800, "locked-side POSITIONREQ was not refused with DPR_SidesLocked");
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // NOT seated
        Assert.Contains(b.DelPositionReqs, d => d.SideId == side0 && d.Reason == DelPositionReqReason.SidesLocked);
        Assert.Empty(a.DelPositionReqs);                        // requester-only, not broadcast

        // Unlock → the P184 leader-gated path resumes: the request broadcasts
        // POSITIONREQ(40) and goes pending (not seated, leader must approve).
        b.ResetLeadershipSignals();
        a.ResetLeadershipSignals();
        a.SendLockSides(false);
        int ug = PumpUntil(server, () => !server.Missions[0].LockSides, 800);
        Assert.True(ug < 800, "LOCK_SIDES(false) not applied");

        b.SendPositionReq(side0);
        int pg = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId && p.SideId == side0), 800);
        Assert.True(pg < 800, "after unlock, the request did not resume P184's pending-broadcast path");
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // still not seated (leader approval needed)
        Assert.DoesNotContain(b.DelPositionReqs, d => d.Reason == DelPositionReqReason.SidesLocked);
    }

    // ── AUTO_ACCEPT ───────────────────────────────────────────────────────

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: a side's leader turning auto-accept ON
    /// immediately seats a player with a pending request on that side. Without
    /// the SetAutoAccept sweep, the pending requester stays unseated.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_accept_on_seats_pending_requester()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TP1");
        short bId = Seat(server, b, "TP2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0; B on side1
        Flush(server);
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));

        // B requests the occupied side0 → pending (broadcast POSITIONREQ(40)).
        b.SendPositionReq(side0);
        int rg = PumpUntil(server, () => a.PositionReqs.Any(p => p.ShipId == bId), 800);
        Assert.True(rg < 800, "pending request not broadcast");
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // still pending

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // A (side0 leader) turns auto-accept ON → B is seated by the sweep.
        a.SendAutoAccept(side0, true);
        int sg = PumpUntil(server,
            () => SideOfShip(ShipOf(server, bId)) == side0
               && b.JoinSides.Any(j => j.ShipId == bId && j.SideId == side0)
               && a.AutoAccepts.Any(x => x.SideId == side0 && x.FAutoAccept), 800);
        Assert.True(sg < 800, "auto-accept ON did not seat the pending requester");
        Assert.True(Side(server, side0).AutoAccept);
        Assert.Contains(a.AutoAccepts, x => x.SideId == side0 && x.FAutoAccept);   // whole-mission relay
        Assert.Equal(side0, SideOfShip(ShipOf(server, bId)));
    }

    /// <summary>⭐ A NEW request to an already-led side with AutoAccept set is
    /// seated immediately (no POSITIONREQ(40) pending), mirroring native
    /// RequestPosition's rgfAutoAccept branch (fsmission.cpp:4759).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_accept_on_seats_new_request_to_led_side()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TQ1");
        short bId = Seat(server, b, "TQ2");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0
        Assert.Equal(side1, SideOfShip(ShipOf(server, bId)));   // B spawns on side1 (peer1)

        a.SendAutoAccept(side0, true);
        int ag = PumpUntil(server, () => Side(server, side0).AutoAccept, 800);
        Assert.True(ag < 800, "AUTO_ACCEPT not applied");

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // B (on side1) requests the LED + auto-accept side0 → seated immediately.
        b.SendPositionReq(side0);
        int sg = PumpUntil(server, () => SideOfShip(ShipOf(server, bId)) == side0, 800);
        Assert.True(sg < 800, "auto-accept side did not seat a new requester immediately");
        Assert.Equal(side0, SideOfShip(ShipOf(server, bId)));
        Assert.Empty(a.PositionReqs);   // no POSITIONREQ(40) pending broadcast
    }

    /// <summary>⭐ AUTO_ACCEPT from a NON-leader is refused: the flag is not
    /// set and nothing is relayed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_accept_from_non_leader_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "TR1");
        short cId = Seat(server, c, "TR2");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);                    // A leads side0
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));  // C non-leader on side0
        Flush(server);

        c.ResetLeadershipSignals();
        a.ResetLeadershipSignals();

        c.SendAutoAccept(side0, true);   // C is NOT the leader
        Flush(server);

        Assert.False(Side(server, side0).AutoAccept);   // flag untouched
        Assert.Empty(c.AutoAccepts);                    // no relay came back
        Assert.Empty(a.AutoAccepts);
    }

    /// <summary>⭐ The iSide == NA (mission-owner-level auto-accept-of-leaders)
    /// variant is out of scope: refused/ignored, flag unchanged, no relay.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_accept_na_variant_ignored()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "TS1");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);
        a.ResetLeadershipSignals();

        a.SendAutoAccept(-1, true);   // NA side id — not the sender's own side
        Flush(server);

        Assert.False(Side(server, side0).AutoAccept);
        Assert.Empty(a.AutoAccepts);
    }

    // ── CHANGE_TEAM_CIV ───────────────────────────────────────────────────

    private static short OtherCiv(Srv server, short currentCivId) =>
        server.Missions[0].Core.Civilizations.Keys.First(c => c != currentCivId);

    /// <summary>⭐ A side's leader changing its civilization while the mission is
    /// STAGE_NOTSTARTED relaying CHANGE_TEAM_CIV(51) whole-mission and swapping
    /// the side's Civilization.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Change_team_civ_from_leader_pre_start_changes_and_relays()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "TU1");
        short bId = Seat(server, b, "TU2");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);   // A leads side0; B on side1
        Flush(server);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        short before = Side(server, side0).Civilization.CivilizationId;
        short target = OtherCiv(server, before);
        Assert.NotEqual(before, target);
        Assert.True(server.Missions[0].Core.Civilizations.ContainsKey(target));

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        a.SendChangeTeamCiv(side0, target);
        int got = PumpUntil(server,
            () => Side(server, side0).Civilization.CivilizationId == target
               && a.ChangeTeamCivs.Any(x => x.SideId == side0 && x.CivId == target)
               && b.ChangeTeamCivs.Any(x => x.SideId == side0 && x.CivId == target), 800);
        Assert.True(got < 800, "CHANGE_TEAM_CIV did not swap the side's civilization");
        Assert.Equal(target, Side(server, side0).Civilization.CivilizationId);
        Assert.Contains(a.ChangeTeamCivs, x => x.SideId == side0 && x.CivId == target);   // relay
        Assert.Contains(b.ChangeTeamCivs, x => x.SideId == side0 && x.CivId == target);   // whole-mission
    }

    /// <summary>⭐ CHANGE_TEAM_CIV is refused once the mission has Started: the
    /// civilization is unchanged and nothing is relayed (native only honors it
    /// at STAGE_NOTSTARTED, FedSrv.CPP:7359).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Change_team_civ_refused_once_started()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "TV1");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        short before = Side(server, side0).Civilization.CivilizationId;
        short target = OtherCiv(server, before);
        server.Missions[0].Stage = MissionStage.Started;   // force the mission started

        a.ResetLeadershipSignals();
        a.SendChangeTeamCiv(side0, target);
        Flush(server);

        Assert.Equal(before, Side(server, side0).Civilization.CivilizationId);   // unchanged
        Assert.Empty(a.ChangeTeamCivs);                                          // no relay
    }

    /// <summary>⭐ CHANGE_TEAM_CIV from a NON-leader is refused: unchanged, no relay.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Change_team_civ_refused_from_non_leader()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "TW1");
        short cId = Seat(server, c, "TW2");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader
        Flush(server);

        short before = Side(server, side0).Civilization.CivilizationId;
        short target = OtherCiv(server, before);

        c.ResetLeadershipSignals();
        c.SendChangeTeamCiv(side0, target);
        Flush(server);

        Assert.Equal(before, Side(server, side0).Civilization.CivilizationId);
        Assert.Empty(c.ChangeTeamCivs);
    }

    /// <summary>⭐ CHANGE_TEAM_CIV naming a civilization absent from the core is
    /// refused (logged and ignored): unchanged, no relay (native
    /// TrapHackBoot(pciv), FedSrv.CPP:7362).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Change_team_civ_refused_for_unknown_civ()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "TX1");

        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Flush(server);

        short before = Side(server, side0).Civilization.CivilizationId;
        short bogus = 9999;
        Assert.False(server.Missions[0].Core.Civilizations.ContainsKey(bogus));

        a.ResetLeadershipSignals();
        a.SendChangeTeamCiv(side0, bogus);
        Flush(server);

        Assert.Equal(before, Side(server, side0).Civilization.CivilizationId);
        Assert.Empty(a.ChangeTeamCivs);
    }

    // ── multi-mission scoping ─────────────────────────────────────────────

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (same pattern as P184): peer 0 → mission 0 / side 0; peer 1 →
    /// mission 1 / side 0 (SAME numeric side id); peer 2 → mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 781f, 881f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>⭐ Multi-mission scoping (the P178/P180/P184 must-not-regress bug
    /// class): LOCK_SIDES(44) — and its LockSides gate — in mission A must never
    /// leak to, or gate, mission B's side switch, even though both share the same
    /// numeric side id.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_lock_sides_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0 (owner)
        using var b0 = new FltBot();   // mission B, side 0 (owner)
        using var a2 = new FltBot();   // mission A, side 1 (regular peer)
        short a0Id = Seat(server, a0, "MY0");
        short b0Id = Seat(server, b0, "MY1");
        short a2Id = Seat(server, a2, "MY2");

        short mA0 = server.Missions[0].Sides[0].Id;
        short mB0 = server.Missions[1].Sides[0].Id;
        short mA1 = server.Missions[0].Sides[1].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        LeadEmptySide(server, a0, a0Id, mA0);   // A: mission-A owner
        LeadEmptySide(server, b0, b0Id, mB0);   // B: mission-B owner
        Flush(server);

        a0.ResetLeadershipSignals();
        b0.ResetLeadershipSignals();
        a2.ResetLeadershipSignals();

        // A (mission A owner) locks sides in mission A only.
        a0.SendLockSides(true);
        int lg = PumpUntil(server, () => server.Missions[0].LockSides, 800);
        Assert.True(lg < 800, "mission-A LOCK_SIDES not applied");
        Assert.True(server.Missions[0].LockSides);
        Assert.False(server.Missions[1].LockSides);   // must-not-regress: mission B untouched
        Assert.Empty(b0.LockSidess);                  // no relay leaked to mission B

        // The gate fires in mission A: a2 (mission A side1) requesting mA0 is
        // refused DPR_SidesLocked.
        a2.SendPositionReq(mA0);
        int dg = PumpUntil(server, () => a2.DelPositionReqs.Any(d => d.Reason == DelPositionReqReason.SidesLocked), 800);
        Assert.True(dg < 800, "mission-A locked request not refused DPR_SidesLocked");
        Assert.Equal(mA1, SideOfShip(ShipOf(server, a2Id)));   // not seated
    }
}
