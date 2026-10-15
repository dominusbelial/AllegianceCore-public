using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P697GhostBattleLiveTests
{
    private static ServerConfig Config(bool native = true, double floor = 2, bool tiers = false) => new()
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
        Port = 0, UpdatesPerSecond = 20,
        Broadcast = new() { ShipUpdateModel = native ? "native" : "port", MinUpdateHz = floor,
            InterestTiers = tiers, ImmutableSnapshot = tiers, EncodeOnce = tiers, ParallelBuild = tiers },
        Games = [new() { Name = "P697", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static void Pump(Srv server, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed.TotalSeconds < 10) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "loopback condition must arrive within 10 seconds");
    }

    private static ConnectionSession Join(Srv server, string name = "p697-viewer")
    {
        var client = new ConnectionSession();
        client.Connect(server.BoundPort, name);
        client.SendLogonRequest(new LogonRequest { CharacterName = name, ZoneTicket = [1],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 697, DwCookie = 697, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = name == "p697-viewer" ? 99000000000000697ul : 99000000000000698ul, DrmHash = "P697" });
        Pump(server, () => { lock (client.Gate) return client.World.MyShipId is not null; });
        return client;
    }

    private static void SpawnViewer(Srv server) => server.ShipSpawner = (core, mission, id) =>
    {
        var ship = new Ship(id, core.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0),
            new Vector3(0, 10000, 0), Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return (ship, mission.Sectors[0]);
    };

    private static Ship Subject(Srv server, short id, short side, int index)
    {
        var mission = server.TestMission!;
        var hull = mission.Core.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0);
        var ship = new Ship(id, hull, new Vector3(20000 + index * 200, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    [SkippableTheory]
    [InlineData(true, 2, false)]
    [Trait("Requires", "Artwork")]
    public void Crowded_fight_orders_every_source_before_fire_even_outside_detection(bool native, double floor, bool tiers)
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config(native, floor, tiers));
        server.TestMission!.Stage = MissionStage.Started;
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var side = (short)(1 - viewer.SideId!.Value);
        var idle = Enumerable.Range(0, 32).Select(i => Subject(server, (short)(18000 + i), side, i)).ToArray();
        // Deliberately outside detection: the floor must match the existing
        // sector pose/identity rule, rather than leaving these to ranked luck.
        for (int i = 0; i < 22; i++) server.Tick();
        Assert.Contains(idle, s => !s.SeenBySide(viewer.SideId.Value));
        if (floor > 0)
        {
            Pump(server, () => { lock (client.Gate) return idle.All(s =>
                client.World.Ships.TryGetValue(s.Id, out var e) && e.Latest is not null); });
        }
        var core = server.TestMission.Core;
        var weapon = core.PartTypes.Values.Where(p => p.Type is DataPartTypeIGC.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type).First(w =>
                w.Dispersion == 0 && core.ProjectileTypes[w.ProjectileTypeId].Power > 0 && w.EnergyPerShot < 100);
        var shooters = Enumerable.Range(0, 32).Select(i => Subject(server, (short)(18100 + i), side, i + 32)).ToArray();
        foreach (var ship in shooters)
        {
            ship.MountWeapon(weapon);
            ship.StateM = ShipControlState.AllWeapons;
            ship.Energy = 1_000_000;
            ship.Ammo = 30000;
        }
        // These ships fire on their first sim update, before the normal export
        // sweep or any pose warmup. Both the sim and wire paths are real.
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 2) server.Tick();
        Pump(server, () => { lock (client.Gate) return shooters.All(s =>
            client.World.Projectiles.Any(p => p.LauncherId == s.Id)); });
        lock (client.Gate)
        {
            var world = client.World;
            Console.WriteLine("BASELINE " + world.Diag.FormatLine());
            Assert.True(world.Diag.ReceivedEnemy >= 32);
            Assert.Equal(0, world.Diag.FromNeverPosed);
            Assert.Equal(0, world.Diag.FromStale);
            Assert.All(shooters, s =>
            {
                Assert.Equal(s.HullType.HullId, world.Ships[s.Id].HullId);
                Assert.Equal(viewer.Sector!.Id, world.Ships[s.Id].SectorId);
                Assert.False(world.Ships[s.Id].IsNotVisible);
            });
        }
        Assert.Contains(shooters, s => !s.SeenBySide(viewer.SideId.Value));

    }

}
