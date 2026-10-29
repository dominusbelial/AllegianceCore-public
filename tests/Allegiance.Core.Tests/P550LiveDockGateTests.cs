using Allegiance.Core;
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
/// P550 — the live loopback half of the dock-gate proof. A REAL client (loopback
/// <see cref="FltBot"/>) docks at a VALID station (a geometry-bearing type whose
/// bay the ship sits genuinely inside — the native <c>InGarage</c> geometric
/// path, <c>stationIGC.cpp:385-436</c> — with "unchanged feel": the same
/// <c>SHIP_STATUS(90)</c> docked broadcast as before), and is REFUSED at an
/// INVALID one (a same-side station type lacking <c>SabmLand</c>, so the native
/// land-ability gate, <c>shipIGC.cpp:725-727</c>, blocks the dock). Mirrors the
/// P53/P126 loopback harness (real TCP, real server tick). Companion to the pure
/// <see cref="P550DockAndRespawnTests"/>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P550LiveDockGateTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P550 Dock Gate", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 559u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 58800000000000000ul, DrmHash = "p550dock",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Boot a server whose ShipSpawner injects a synthetic station of
    /// <paramref name="stationType"/> at the origin of sector 0 (owned by the
    /// first side) and spawns the client ship at <paramref name="shipPos"/>,
    /// then seat one real client. Returns the spawned ship + the station.</summary>
    private static (Srv server, Ship ship, Station station, FltBot bot) Boot(
        DataStationTypeIGC stationType, Vector3 shipPos)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var server = new Srv();
        Ship? spawn = null;
        Station? placed = null;
        var stationSpot = new Vector3(0f, 0f, -2000f);   // clear of the map's real stations
        server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            if (placed is null)
            {
                short side = mission.Sides[0].Id;
                var data = new DataStationIGC(
                    Position: [stationSpot.X, stationSpot.Y, stationSpot.Z],
                    Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                    ClusterId: sector.Id, SideId: side, StationId: 5877,
                    StationTypeId: stationType.StationTypeId, BpHull: 1, BpShield: 1, Name: "P550_BASE");
                placed = new Station(data, stationType);
                mission.PlaceStation(placed, sector);
            }
            var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter));
            var ship = new Ship(id, hull, stationSpot + shipPos, Vector3.Zero, Orientation.Identity)
            { SideId = placed.SideId };
            mission.PlaceShip(ship, sector);
            spawn = ship;
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p550_gate");
        bot.SendLogonRequest(Request("p550_gate"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        return (server, spawn!, placed!, bot);
    }

    /// <summary>⭐ A real client docks at a VALID station — a geometry-bearing
    /// type (tecsy) whose bay the ship sits genuinely inside (the native
    /// InGarage geometric path). "Unchanged feel": the same SHIP_STATUS(90)
    /// docked broadcast arrives, exactly as a normal dock.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_client_docks_at_geometry_bearing_valid_station()
    {
        Require.Artwork();
        var tecsy = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"))).StationTypes[29];
        // Spawn IN the tecsy bay (its capgarage planes bound a bay at y≈-320..-440,
        // x,z near 0; (-50,-400,0) is genuinely InGarage for a fighter).
        var r = Boot(tecsy, new Vector3(-50f, -400f, 0f));
        using var server = r.server;
        using var bot = r.bot;
        var ship = r.ship;
        short shipId = ship.Id;

        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 4000);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);

        PumpUntil(server,
            () => bot.ShipStatuses.Any(s => s.ShipId == shipId && s.State == ShipStatusMessage.StateDocked),
            4000);
        Assert.Contains(bot.ShipStatuses,
            s => s.ShipId == shipId && s.State == ShipStatusMessage.StateDocked && s.StationId == 5877);
        Console.WriteLine($"[p550-live] real client docked at geometry-bearing valid station 5877 (native InGarage path), SHIP_STATUS docked received");
    }

    /// <summary>⭐ A real client is REFUSED at an INVALID station — a same-side
    /// station type lacking SabmLand (capital-only), so the native land-ability
    /// gate (shipIGC.cpp:725-727) blocks the fighter's dock even though it sits
    /// inside the dock radius.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_client_is_refused_at_station_without_sabml_and()
    {
        Require.Artwork();
        var tecsy = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"))).StationTypes[29];
        var capOnly = tecsy with { SabmCapabilities = DataStationTypeIGC.SabmCapLand };
        // Spawn IN the bay (geometry passes) so the ONLY blocker is the native
        // land-ability gate — the fighter needs SabmLand, which this base lacks.
        var r = Boot(capOnly, new Vector3(-50f, -400f, 0f));
        using var server = r.server;
        using var bot = r.bot;
        var ship = r.ship;
        short shipId = ship.Id;

        // Let many ticks elapse — a wrongly-permitted dock would latch by now.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);
        PumpUntil(server, () => bot.ShipStatuses.Count >= 3, 4000);
        Assert.DoesNotContain(bot.ShipStatuses, s => s.ShipId == shipId && s.StationId != -1);
        Console.WriteLine("[p550-live] real client refused at non-SabmLand station — no dock, stays flying");
    }
}
