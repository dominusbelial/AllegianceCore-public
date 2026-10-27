using System.Collections;
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
/// P850 — closes the gap where the server only BUILDS (broadcasts)
/// <c>QUIT_SIDE(126)</c> and <c>SET_TEAM_LEADER(121)</c> but had no inbound
/// dispatch case. These prove the newly-added inbound handlers over the real
/// loopback: an in-process server + real TCP bots (the same harness
/// <c>RandomizeTeamsLiveTests</c> uses).
/// <list type="bullet">
/// <item><b>QUIT_SIDE(126)</b> (FedSrv.CPP:6823-6906): a player quits its own
/// side (reason <see cref="QuitSideReason.Quit"/>) or a side leader boots a
/// same-side teammate (<see cref="QuitSideReason.LeaderBooted"/>); the leaver
/// is moved to the team lobby (SIDE_TEAMLOBBY, -2), staying in the mission.
/// A non-leader kicking someone is refused; a leader boot is refused while
/// <c>LockTeamSettings</c> is set; a missing target is a no-op.</item>
/// <item><b>SET_TEAM_LEADER(121)</b> (FedSrv.CPP:7624-7687, minus native's
/// bot-auth sub-branches): a side leader transfers leadership to a same-side
/// teammate (<c>Side.LeaderShipId</c> changes + a whole-mission broadcast).
/// Refused for a non-leader and when the target is on a different side.</item>
/// <item><b>SET_MISSION_OWNER(123)</b> (FedSrv.CPP:7690-7713) is NOT given an
/// inbound handler: native gates the whole body on bot authentication
/// (<c>isBot == true</c>, a WOPR feature); this port has no bot-role/auth
/// concept, so the handler is N/A by design.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class P850MissingFmHandlersTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0, bool lockTeamSettings = false) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "P850 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"], LockTeamSettings = lockTeamSettings,
            },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 885u, DwCookie = 0x885u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8880000000000000000ul, DrmHash = "p850",
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

    private static Ship ShipOf(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private static short SideOf(Srv server, short id) =>
        ShipOf(server, id).SideId!.Value;

    private static Mission Mission0(Srv server) => server.Missions[0];

    /// <summary>Spawn every peer UNSEATED (SideId=null) on mission 0, so the
    /// test controls seating entirely via TryJoinSide (the same technique as
    /// RandomizeTeamsLiveTests, which makes leadership establishment on the
    /// first join of an empty side deterministic).</summary>
    private static void SetUnseatedSpawner(Srv server)
    {
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = server.Missions[0];
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(400f, 700f, 920f), Vector3.Zero, Orientation.Identity);
            ship.SideId = null;
            m.PlaceShip(ship, m.Sectors[0]);
            return (ship, m.Sectors[0]);
        };
    }

    // ── codec round trips ──────────────────────────────────────────────

    /// <summary>QUIT_SIDE(126) codec: header(4) + VarItem ib/cb(4) + ShipId
    /// short(2) + pad(2) + reason int(4) = 16 fixed, plus the variable CB_ZTS
    /// message blob. Round-trips shipId/reason/message intact.</summary>
    [Fact]
    public void QuitSide_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)126, QuitSide.Fmid);
        Assert.Equal(16, QuitSide.FixedSize);

        var msg = new QuitSide { ShipId = 577, Reason = QuitSideReason.LeaderBooted, Message = "booted" };
        byte[] b = msg.Encode();
        Assert.Equal(16 + 7, b.Length);   // 16 fixed + CB_ZTS blob ("booted" 6 chars + NUL)
        Assert.Equal((ushort)b.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b));   // cbmsg

        var d = QuitSide.Decode(b);
        Assert.Equal((ushort)126, d.GetFmid());
        Assert.Equal((short)577, d.ShipId);
        Assert.Equal(QuitSideReason.LeaderBooted, d.Reason);
        Assert.Equal("booted", d.Message);

        // The empty-message form is the 16 fixed bytes plus one trailing NUL
        // (the CB_ZTS blob is always written).
        byte[] bare = new QuitSide { ShipId = 578, Reason = QuitSideReason.Quit }.Encode();
        Assert.Equal(17, bare.Length);
        var db = QuitSide.Decode(bare);
        Assert.Equal((short)578, db.ShipId);
        Assert.Equal(QuitSideReason.Quit, db.Reason);
        Assert.Equal("", db.Message);
    }

    /// <summary>SET_TEAM_LEADER(121) codec: header(4) + SideId short(2) +
    /// ShipId short(2) = 8 fixed. Round-trips sideId/shipId intact.</summary>
    [Fact]
    public void SetTeamLeader_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)121, SetTeamLeader.Fmid);
        Assert.Equal(8, SetTeamLeader.FixedSize);

        byte[] b = new SetTeamLeader { SideId = 602, ShipId = 903 }.Encode();
        Assert.Equal(8, b.Length);
        Assert.Equal(8, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b));   // cbmsg
        Assert.Equal(121, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2))); // fmid

        var d = SetTeamLeader.Decode(b);
        Assert.Equal((ushort)121, d.GetFmid());
        Assert.Equal((short)602, d.SideId);
        Assert.Equal((short)903, d.ShipId);
    }

    // ── QUIT_SIDE(126) ─────────────────────────────────────────────────

    /// <summary>⭐ (a) Self-quit: a player sends QUIT_SIDE for its own ship
    /// (reason Quit) and is moved to the team lobby — SIDE_TEAMLOBBY(-2) —
    /// while staying in the mission (FedSrv.CPP:6862,6886-6901 via
    /// RemovePlayerFromSide / MoveToTeamLobby).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QuitSide_self_quit_moves_player_to_team_lobby()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        short aId = Seat(server, a, "QA"); short bId = Seat(server, b, "QB");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader + mission owner
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));

        a.SendQuitSide(aId, QuitSideReason.Quit);
        int done = PumpUntil(server, () => SideOf(server, aId) == -2, 2000);
        Assert.True(done < 2000, "self QUIT_SIDE did not move A to the team lobby");

        // A is now in the team lobby, still present in the mission.
        Assert.Equal((short)-2, SideOf(server, aId));
        Assert.Contains(server.Ships, l => l.Ship.Id == aId);
        // B is untouched.
        Assert.Equal(side1, SideOf(server, bId));
    }

    /// <summary>⭐ (b) A non-leader sending QUIT_SIDE to kick another player is
    /// refused (FedSrv.CPP:6862-6864 — the sender must be the team leader to
    /// boot, or be booting themselves).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QuitSide_refused_for_non_leader_kicking_another()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "KA"); short bId = Seat(server, b, "KB"); short cId = Seat(server, c, "KC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // B non-leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);

        // B (non-leader) tries to boot C.
        b.SendQuitSide(cId, QuitSideReason.LeaderBooted);
        PumpUntil(server, () => false, 80);   // give any wrongful action time to occur
        Assert.Equal(side0, SideOf(server, cId));
        Assert.Equal(side0, SideOf(server, bId));
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
    }

    /// <summary>⭐ (c, unlocked) A side leader booting a same-side teammate is
    /// honoured when LockTeamSettings is false (FedSrv.CPP:6862-6889): the
    /// target is moved to the team lobby, the leader stays.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QuitSide_leader_boot_honoured_when_team_settings_unlocked()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());   // LockTeamSettings = false

        using var a = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "BA"); short cId = Seat(server, c, "BC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C teammate

        a.SendQuitSide(cId, QuitSideReason.LeaderBooted);
        int done = PumpUntil(server, () => SideOf(server, cId) == -2, 2000);
        Assert.True(done < 2000, "leader boot did not move C to the team lobby");
        Assert.Equal((short)-2, SideOf(server, cId));
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
    }

    /// <summary>⭐ (c, locked) The same leader boot is REFUSED when
    /// LockTeamSettings is true (FedSrv.CPP:6889: <c>!bIsBoot ||
    /// !bLockTeamSettings</c>): C stays on the side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QuitSide_leader_boot_refused_when_team_settings_locked()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config(lockTeamSettings: true));

        using var a = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "LA"); short cId = Seat(server, c, "LC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C teammate

        a.SendQuitSide(cId, QuitSideReason.LeaderBooted);
        PumpUntil(server, () => false, 80);   // give any wrongful action time to occur
        Assert.Equal(side0, SideOf(server, cId));   // refused — C stays
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
    }

    /// <summary>⭐ (d) QUIT_SIDE with a missing target ship id is a no-op:
    /// the sender (and everyone else) is left untouched (FedSrv.CPP:6843-6844,
    /// <c>if (!pfssAck) break</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QuitSide_refused_when_target_missing()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "MA"); short cId = Seat(server, c, "MC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));

        a.SendQuitSide(7777, QuitSideReason.Quit);   // no such ship in the mission
        PumpUntil(server, () => false, 80);
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side0, SideOf(server, cId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
    }

    // ── SET_TEAM_LEADER(121) ───────────────────────────────────────────

    /// <summary>⭐ (e, positive) A side leader transfers leadership to a
    /// same-side teammate: Side.LeaderShipId changes and SET_TEAM_LEADER is
    /// broadcast (FedSrv.CPP:7651-7652, <c>SetLeader</c>; mirror of ApplyMutiny).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SetTeamLeader_transfers_leadership_to_same_side_teammate()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "TA"); short cId = Seat(server, c, "TC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader + owner
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C teammate
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        ShipOf(server, cId).WingId = 3;
        a.ResetSetWingIdsReceived();

        a.SendSetTeamLeader(side0, cId);
        int done = PumpUntil(server, () => m.Sides.First(s => s.Id == side0).LeaderShipId == cId
            && a.SetWingIds.Any(w => w.ShipID == cId && w.WingId == 0 && w.BCommanded), 2000);
        Assert.True(done < 2000, "leader transfer to same-side teammate did not happen");
        Assert.Equal(cId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(side0, SideOf(server, cId));
        Assert.Equal(side0, SideOf(server, aId));
        // P850 review (Claude): native SetLeader puts the new leader on command
        // wing 0 and tells the side (fsmission.cpp:1533-1541).
        Assert.Equal(0, ShipOf(server, cId).WingId);
    }

    /// <summary>⭐ (e, non-leader) A non-leader sending SET_TEAM_LEADER is
    /// refused — leadership is unchanged (FedSrv.CPP:7639, the sender must be
    /// the current team leader).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SetTeamLeader_refused_for_non_leader()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "NA"); short bId = Seat(server, b, "NB"); short cId = Seat(server, c, "NC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // B non-leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);

        b.SendSetTeamLeader(side0, cId);   // B is not the leader
        PumpUntil(server, () => false, 80);
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
    }

    /// <summary>⭐ (e, different side) A leader transferring to a player on a
    /// DIFFERENT side is refused — both sides keep their leaders
    /// (FedSrv.CPP:7645, <c>GetSide() == psidePlayer</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SetTeamLeader_refused_when_target_on_different_side()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        short aId = Seat(server, a, "DA"); short bId = Seat(server, b, "DB");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader side0
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B leader side1

        a.SendSetTeamLeader(side0, bId);   // target B is on side1
        PumpUntil(server, () => false, 80);
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, m.Sides.First(s => s.Id == side1).LeaderShipId);
    }
}
