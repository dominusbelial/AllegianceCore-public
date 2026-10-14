using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P282 — live proof that the real ready-check flow gates mission start on real
/// ready state, end to end over the real wire with real <see cref="ConnectionSession"/>
/// clients. This is the client-side counterpart to the old Game1 auto-ready
/// hack's removal: a connected peer NO LONGER starts the mission by existing —
/// it must actively send PLAYER_READY (or, as the mission owner, FORCE_TEAM_READY)
/// exactly like the new READY-CHECK screen does (Game1 Y / O). Two peers on the
/// same side:
///  • the mission stays NotStarted while either is unready (even with both
///    connected) — the real behavior change from the instant-ready hack;
///  • each PLAYER_READY relay updates both rosters (ClientWorld.Roster) and the
///    per-side aggregate flips to true only once the LAST peer readies;
///  • the mission only then begins its start countdown (P180 TryAutoStart);
///  • a non-owner's FORCE_TEAM_READY is refused; the mission owner's forces the
///    whole side ready and starts the mission.
/// Real wire evidence: the PLAYER_READY(45) relays, TEAM_READY(46) aggregates
/// and FORCE_TEAM_READY(47) forwards land in each client's ClientWorld
/// (Roster / TeamReadyBySideId / ForceReadyBySideId).
/// </summary>
[Collection("LiveNetwork")]
public class ReadyCheckLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ready Live", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 801u, DwCookie = 0x321u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 83100000000000000ul, DrmHash = "rdy",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void ConnectSeat(Srv server, ConnectionSession s, string name, short side)
    {
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        s.SendPositionReq(side);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_stays_not_started_while_any_same_side_peer_is_unready_then_starts_when_last_readies()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.Equal((short)0, side0);

        using var a = new ConnectionSession();
        using var b = new ConnectionSession();
        ConnectSeat(server, a, "rdy_a", side0);
        ConnectSeat(server, b, "rdy_b", side0);

        int seated = PumpUntil(server,
            () => a.World.MyShipId is not null && b.World.MyShipId is not null, 2000);
        Assert.True(seated < 2000, "both peers must be seated after picking side 0");

        short aId = a.World.MyShipId!.Value;
        short bId = b.World.MyShipId!.Value;
        Assert.NotEqual(aId, bId);
        // Both peers landed on the same side.
        Assert.Equal(a.World.Ships[aId].SideId, b.World.Ships[bId].SideId);

        // ⭐ The real behavior change: with BOTH peers connected but NEITHER
        // ready, the mission must NOT auto-start (today's hack sent PLAYER_READY
        // unconditionally; the new screen makes the player toggle it).
        int settled = PumpUntil(server, () => false, 100);   // ~5 s of sim, plenty of ticks
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        // Peer A readies alone — still NOT started (B unready), and A's ready is
        // relayed to both rosters.
        a.SendPlayerReady(true, aId);
        int aReady = PumpUntil(server,
            () => a.World.Roster.ReadyByShipId.TryGetValue(aId, out var r1) && r1 &&
                  b.World.Roster.ReadyByShipId.TryGetValue(aId, out var r2) && r2,
            2000);
        Assert.True(aReady < 2000, "A's PLAYER_READY relay never updated both rosters");
        Assert.True(a.World.Roster.ReadyByShipId[aId]);
        Assert.True(b.World.Roster.ReadyByShipId[aId]);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);   // B still unready

        // B readies too — now the side's aggregate TEAM_READY(true) flips and the
        // mission begins its start countdown (P180 TryAutoStart).
        b.SendPlayerReady(true, bId);
        int started = PumpUntil(server, () => server.Missions[0].Stage != MissionStage.NotStarted, 2000);
        Assert.True(started < 2000, "mission never left NotStarted after the last peer readied");

        // The real TEAM_READY(true) broadcast reached BOTH clients for side 0,
        // and both rosters show both players ready.
        Assert.True(PumpUntil(server,
            () => a.World.TeamReadyBySideId.TryGetValue(side0, out var t1) && t1 &&
                  b.World.TeamReadyBySideId.TryGetValue(side0, out var t2) && t2, 2000) < 2000,
            "TEAM_READY(true) for side 0 never reached both clients");
        Assert.True(a.World.Roster.ReadyByShipId[bId]);
        Assert.True(b.World.Roster.ReadyByShipId[bId]);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Only_the_mission_owner_can_force_team_ready_and_it_starts_the_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        short side0 = server.Missions[0].Sides[0].Id;

        using var owner = new ConnectionSession();   // first on side0 → leader + mission owner
        using var nonLeader = new ConnectionSession();
        ConnectSeat(server, owner, "rdy_own", side0);
        // P647 — the leader is whoever SEATS first on the side. Under full-suite
        // load the two logons seat in a shuffled order, so the "nonLeader" can
        // beat the owner to seating and BECOME the leader — its FORCE_TEAM_READY
        // then legitimately starts the mission and the negative assertion below
        // (Stage stays NotStarted) spuriously fails. Guarantee owner seats first
        // so it is deterministically the leader before the non-leader connects.
        Assert.True(PumpUntil(server, () => owner.World.MyShipId is not null, 4000) < 4000,
            "owner must seat before the non-leader connects");
        ConnectSeat(server, nonLeader, "rdy_nl", side0);

        int seated = PumpUntil(server,
            () => owner.World.MyShipId is not null && nonLeader.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "both peers must be seated");
        short ownerId = owner.World.MyShipId!.Value;

        // Non-leader tries to force side 0 ready — refused (server leader-gates
        // FORCE_TEAM_READY), mission still not started, no force flag propagates.
        nonLeader.SendForceTeamReady(true, side0);
        PumpUntil(server, () => false, 40);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.DoesNotContain(side0, nonLeader.World.ForceReadyBySideId.Keys);

        // Mission owner (their side's leader) forces the whole side ready →
        // TEAM_READY(true) + the mission starts, even though nobody toggled ready.
        owner.SendForceTeamReady(true, side0);
        Assert.True(PumpUntil(server, () => server.Missions[0].Stage != MissionStage.NotStarted, 2000) < 2000,
            "owner FORCE_TEAM_READY never started the mission");

        // The FORCE_TEAM_READY forward (incl. the sender's own echo) landed on
        // both clients, and the side went aggregate-ready.
        Assert.True(PumpUntil(server,
            () => owner.World.ForceReadyBySideId.TryGetValue(side0, out var f1) && f1 &&
                  nonLeader.World.ForceReadyBySideId.TryGetValue(side0, out var f2) && f2, 2000) < 2000,
            "FORCE_TEAM_READY echo never reached both clients");
        Assert.True(PumpUntil(server,
            () => owner.World.TeamReadyBySideId.TryGetValue(side0, out var t) && t, 2000) < 2000,
            "forced ready never produced TEAM_READY(true) for side 0");
    }
}
