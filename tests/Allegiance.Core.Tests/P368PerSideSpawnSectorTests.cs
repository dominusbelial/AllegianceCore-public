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
/// P368 — every newly-spawned player ship must physically spawn in its FINAL
/// side's real home sector (the sector holding a station that side owns,
/// Station.SideId), not unconditionally in mission.Sectors[0]. This is the
/// regression + live proof for the P368 server fix.
///
/// Setup mirrors the live 2-client reproduction Domingo ran: a real
/// in-process Allegiance.Server bound to a real loopback socket, running the
/// real two-garrison map <c>m2xspiderw.igc</c> (measured by missiondump:
/// side 0's 'Garrison' sits in sector 1050 'Ganymede', side 1's in sector
/// 1051 'Organia' — the literal first two clusters of that map), with two real
/// <c>Bot</c> peers connecting over real TCP. One peer explicitly requests
/// side 0 (alpha) and the other side 1 (bravo) via POSITIONREQ — the exact
/// CompleteSideSelection → AssignShip → SpawnDefaultShip path the MonoGame
/// clients drive — and we read each seated ship's <see cref="Ship.Sector"/>
/// from the server to confirm they landed in DIFFERENT, correct per-side home
/// sectors.
///
/// Before the fix both ships sat in mission.Sectors[0] ('Ganymede') regardless
/// of side; the assertions below (and the printed sector names/ids) fail on
/// the old behaviour because the side was only assigned AFTER a sector-0
/// placement and never influenced the sector.
/// </summary>
[Collection("LiveNetwork")]
public class P368PerSideSpawnSectorTests
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
            new Gm { Name = "P368 Two Side", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 841u, DwCookie = 0x841u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8480000000000000000ul, DrmHash = "p368",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A station-owned sector for a side (the real-data expectation,
    /// derived from the live mission — not hardcoded sector ids).</summary>
    private static Sector? HomeSector(Srv server, short sideId)
    {
        foreach (var s in server.Missions[0].Sectors)
            foreach (var st in s.StationRuntimes)
                if (st.SideId == sideId)
                    return s;
        foreach (var s in server.Missions[0].Sectors)
            foreach (var st in s.Stations)
                if (st.SideId == sideId)
                    return s;
        return null;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Both_sides_spawn_in_their_own_home_sector_not_sector_zero()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        var mission = server.Missions[0];
        Assert.Equal(2, mission.Sides.Count);
        short alpha = mission.Sides[0].Id;   // 0
        short bravo = mission.Sides[1].Id;   // 1

        // Real-data homes (independent of the fix): sector owning a station per side.
        var alphaHome = HomeSector(server, alpha);
        var bravoHome = HomeSector(server, bravo);
        Assert.NotNull(alphaHome);
        Assert.NotNull(bravoHome);
        // m2xspiderw really does split the two garrisons across two sectors.
        Assert.NotEqual(alphaHome!.Id, bravoHome!.Id);

        // Two real peers over real loopback, each EXPLICITLY requesting a side.
        using var a = new FltBot { AutoSelectSideAfterLogon = false };
        using var b = new FltBot { AutoSelectSideAfterLogon = false };
        a.Connect(server.BoundPort, "p368_a");
        b.Connect(server.BoundPort, "p368_b");
        a.SendLogonRequest(Request("p368_a"));
        b.SendLogonRequest(Request("p368_b"));

        int ok = PumpUntil(server, () => a.Ack is { FValidated: true } && b.Ack is { FValidated: true }, 4000);
        Assert.True(ok < 4000, "both peers must validate their logon");

        a.SendPositionReq(alpha);
        b.SendPositionReq(bravo);

        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null &&
                  ShipOf(server, a.ShipAssignment.Ship.ShipId).Sector is not null &&
                  ShipOf(server, b.ShipAssignment.Ship.ShipId).Sector is not null,
            6000);
        Assert.True(seat < 6000, "both peers must seat (explicit side pick)");

        var shipA = ShipOf(server, a.ShipAssignment!.Ship.ShipId);
        var shipB = ShipOf(server, b.ShipAssignment!.Ship.ShipId);
        Assert.Equal(alpha, shipA.SideId!.Value);
        Assert.Equal(bravo, shipB.SideId!.Value);

        var secA = shipA.Sector!;
        var secB = shipB.Sector!;

        // THE FIX: each ship sits in ITS side's home sector, and the two differ.
        Assert.Equal(alphaHome.Id, secA.Id);
        Assert.Equal(bravoHome.Id, secB.Id);
        Assert.NotEqual(secA.Id, secB.Id);

        Console.WriteLine($"P368-LIVE: alpha ship {shipA.Id} (side {shipA.SideId}) spawned in sector {secA.Id} '{secA.Name}'");
        Console.WriteLine($"P368-LIVE: bravo ship {shipB.Id} (side {shipB.SideId}) spawned in sector {secB.Id} '{secB.Name}'");
        Console.WriteLine($"P368-LIVE: sectors differ = {secA.Id != secB.Id}; alpha home = {alphaHome.Id}, bravo home = {bravoHome.Id}");
    }
}
