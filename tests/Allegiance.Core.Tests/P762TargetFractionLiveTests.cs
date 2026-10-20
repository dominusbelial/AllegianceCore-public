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
/// P762 — SIM-0471, the native per-player target hull/shield fraction payload
/// (<c>FedSrv/FedSrv.CPP:4076-4124</c>), proven over a real loopback server.
/// Every target type is exercised: ship (its source ship), station, probe,
/// asteroid, missile, and a target outside the player's cluster (255/255).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P762TargetFractionLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P762 targets", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
    };
    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 762u, DwCookie = 0x762u, CrcFileList = -1,
        SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 76200000000000000ul, DrmHash = "p762target",
    };
    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }
    private static byte Percent(float f) => (byte)Math.Clamp((double)f * 240.0 + 0.5, 0, 240);

    private sealed class Seat : IDisposable
    {
        public Srv Server = null!; public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!; public Sector Sector = null!;
        public Mission Mission = null!;
        public void Dispose() { ABot?.Dispose(); BBot?.Dispose(); Server?.Dispose(); }
    }

    private static Seat SeatTwo()
    {
        var seat = new Seat { Server = new Srv() };
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0]; seat.Sector = sector; seat.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(placed == 0 ? 0 : 500, 0, 0), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot() };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            { bots[i].Connect(seat.Server.BoundPort, $"p762t{i}"); bots[i].SendLogonRequest(Request($"p762t{i}")); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null));
            seat.A = seat.Server.Ships.First(s => s.Ship.Id == bots[0].ShipAssignment!.Ship.ShipId).Ship;
            seat.B = seat.Server.Ships.First(s => s.Ship.Id == bots[1].ShipAssignment!.Ship.ShipId).Ship;
            seat.ABot = bots[0]; seat.BBot = bots[1];
            seat.A.Velocity = seat.B.Velocity = Vector3.Zero;
            return seat;
        }
        catch { foreach (var b in bots) b.Dispose(); seat.Server.Dispose(); throw; }
    }

    private static bool Saw(Seat seat, byte hull, byte shield, short? firstHeavy = null)
        => seat.BBot.HeavyShipsUpdates.Any(u =>
            u.BpTargetHull == hull && u.BpTargetShield == shield
            && (firstHeavy is null || (u.HeavyShipEntries.Length > 0 && u.HeavyShipEntries[0].ShipId == firstHeavy)));

    private static void Expect(Seat seat, byte hull, byte shield, short? firstHeavy, string what)
    {
        seat.BBot.ResetHeavyShipsUpdates();
        PumpUntil(seat.Server, () => Saw(seat, hull, shield, firstHeavy), 20000);
        Assert.True(Saw(seat, hull, shield, firstHeavy),
            $"{what}: expected hull {hull} shield {shield}" + (firstHeavy is { } id ? $" first heavy {id}" : ""));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Target_fraction_payload_covers_every_native_target_type_and_out_of_cluster()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        var mission = seat.Mission;
        var sector = seat.Sector;
        var core = Core();

        // ── ship: the source ship's hull, shield stays 255 (no mounted shield) ──
        seat.B.CommandTarget = seat.A;
        Expect(seat, Percent(seat.A.Fraction), 255, seat.A.Id, "ship target");

        // ── ship source collapse: targeting a child reports its root ship ──────
        var child = new Ship(8101, seat.A.HullType, seat.A.Position, Vector3.Zero, Orientation.Identity)
        { SideId = seat.A.SideId, ParentShip = seat.A };
        mission.PlaceShip(child, sector);
        seat.B.CommandTarget = child;
        Expect(seat, Percent(seat.A.Fraction), 255, seat.A.Id, "child ship target reports source ship");

        // ── station: hull + shield ─────────────────────────────────────────────
        var stType = mission.Core.StationTypes.Values.First();
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 8102, SideId = 0, ClusterId = sector.Id, Position = [300, 0, 0],
        }, stType);
        mission.PlaceStation(station, sector);
        var stationTarget = EquipmentTarget.Resolve(sector, IgcObjectType.Station, station.StationId);
        Assert.NotNull(stationTarget);
        seat.B.CommandTarget = stationTarget;
        Expect(seat, Percent(station.ArmorFraction), Percent(station.ShieldFraction), null, "station target");

        // ── asteroid: hull only ────────────────────────────────────────────────
        var rock = new Asteroid(new DataAsteroidIGC(1, [400, 0, 0], [0, 1, 0], [1, 0, 0], [0, 1, 0, 0],
            100, 100, 0, 8103, 100, 60, "roid.mdl", "", "", sector.Id, "target rock", 0));
        mission.PlaceAsteroid(rock, sector);
        seat.B.CommandTarget = EquipmentTarget.Resolve(sector, IgcObjectType.Asteroid, rock.Data.AsteroidId);
        Expect(seat, Percent(rock.Fraction), 255, null, "asteroid target");

        // ── probe: hull only ───────────────────────────────────────────────────
        var probeType = core.ProbeTypes.Values.First(p => p.Lifespan > 5);
        var probe = new Probe(8104, sector, probeType, new Vector3(600, 0, 0), mission.LastUpdate, 0);
        sector.Add(probe);
        seat.B.CommandTarget = probe;
        Expect(seat, Percent(probe.Fraction), 255, null, "probe target");

        // ── missile: hull only ─────────────────────────────────────────────────
        var missileType = core.MissileTypes.Values.First();
        var missile = new Missile(8105, sector, missileType, new Vector3(700, 0, 0), Vector3.Zero,
            new Vector3(0, 0, 1), mission.LastUpdate);
        sector.Add(missile);
        seat.B.CommandTarget = missile;
        Expect(seat, Percent(missile.Fraction), 255, null, "missile target");

        // ── out-of-cluster: both bytes stay 255 ────────────────────────────────
        var other = mission.Sectors.FirstOrDefault(s => !ReferenceEquals(s, sector));
        Assert.NotNull(other);
        var far = new Ship(8106, seat.A.HullType, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = seat.A.SideId };
        mission.PlaceShip(far, other);
        seat.B.CommandTarget = far;
        Expect(seat, 255, 255, null, "out-of-cluster target");
        Console.WriteLine("P762-TARGETS: ship child/source, station, asteroid, probe, missile and out-of-cluster all verified");
    }
}
