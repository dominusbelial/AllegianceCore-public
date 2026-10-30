using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P803 (PAR-8) — the LIGHT-GATE LOOPBACK: a second real client boards the
/// first's ship as a turret gunner over the real wire (production
/// <see cref="ConnectionSession.SendBoardShip"/> → real server BOARD_SHIP →
/// LOADOUT_CHANGE(96) PassengerData re-seat), and BOTH clients' rosters — the
/// native F4 equipment pane model (<c>WinTrek/inventoryimage.cpp:263-289</c>)
/// — resolve the occupant on the RIGHT turret mount. The gunner-side HUD model
/// inputs are asserted from the same client wire state: the source-ship pose
/// (native <c>GetSourceShip()</c>, <c>indicator.cpp:130</c>) and the TURRET
/// weapon mounted at the gunner's <c>BoardTurretId</c>
/// (<c>indicator.cpp:196-204</c>) whose projectile range the client's
/// <see cref="TargetWeaponRange"/> solve consumes. The occupant assertions fail
/// on master (the pane had no per-turret occupant rows).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P803TurretGunnerLoopbackLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // From PCore001.igc (the P416 ids): 792 = turret-capable carrier
    // (slots [2,6)), 504 = plain gunner hull.
    private const short CarrierHullId = 792;
    private const short GunnerHullId = 504;

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 803u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97700000000000803ul, DrmHash = "p803",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_boarded_gunner_huds_name_the_right_turret_on_both_clients()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship carrierShip = null!, gunnerShip = null!;
        short weaponPartId = -1;
        float projectileRange = 0f;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            var station = P550MonoTestDockHome.CapLandHome(mission, sideId);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var ship = new Ship(id, core.HullTypes[carrierShip is null ? CarrierHullId : GunnerHullId],
                StationPos(station) + new SimVec(25f + (carrierShip is null ? 0f : 10f), 0f, 0f), SimVec.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            if (carrierShip is null)
            {
                // Equip BOTH turret slots so the board lands on a known mount
                // (the carrier's first turret slot, mount 2) with a real weapon.
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                weaponPartId = weapon.PartId;
                short projectileId = weapon.Type is DataPartTypeIGC.Weapon w ? w.ProjectileTypeId : (short)-1;
                var projectile = core.ProjectileTypes.Values.First(pt => pt.ProjectileTypeId == projectileId);
                projectileRange = projectile.Speed * projectile.Lifespan;
                ship.MountPart(weapon, 1, (byte)ship.HullType.MaxFixedWeapons, 255);
                ship.MountPart(weapon, 1, (byte)(ship.HullType.MaxFixedWeapons + 1), 255);
                carrierShip = ship;
            }
            else
            {
                gunnerShip = ship;
            }
            return (ship, sector);
        });

        server.Boot(Config("P803 Turret Gunner Loopback"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var a = Connect(server, "PILOT");
        using var b = Connect(server, "GUNNER");
        Assert.True(PumpUntil(server,
            () => a.World.MyShipId is { } && b.World.MyShipId is { }, 4000) < 4000, "both peers seated");

        ConnectionSession pilot, gunner;
        if (a.World.MyShipId!.Value == carrierShip.Id) { pilot = a; gunner = b; }
        else { pilot = b; gunner = a; }
        short pilotId = carrierShip.Id, gunnerId = gunnerShip.Id;

        Assert.True(PumpUntil(server,
            () => carrierShip.StationId is not null && gunnerShip.StationId is not null, 400) < 400, "docked");

        gunner.SendBoardShip(pilotId);
        Assert.True(PumpUntil(server, () => gunnerShip.ParentShip is not null, 1200) < 1200, "boarded");
        short slot = gunnerShip.TurretId!.Value;
        Assert.True(slot >= carrierShip.HullType.MaxFixedWeapons, "boarded a turret mount");

        // Both clients observe the boarding: SHIP_STATUS carries ParentId (the
        // P58 same-side status); the carrier's LOADOUT_CHANGE(96) PassengerData
        // carries the turret mount.
        Assert.True(PumpUntil(server, () =>
        {
            lock (pilot.Gate) lock (gunner.Gate)
                return pilot.World.Ships.TryGetValue(gunnerId, out var gp) && gp.ParentId == pilotId
                    && gunner.World.Ships.TryGetValue(gunnerId, out var gg) && gg.ParentId == pilotId;
        }, 4000) < 4000, "both clients hold the boarded child");
        Assert.True(PumpUntil(server, () =>
        {
            lock (pilot.Gate)
                return pilot.World.Ships.TryGetValue(gunnerId, out var gp) && gp.BoardTurretId == slot;
        }, 4000) < 4000, "the pilot's roster holds the occupant on the right turret");
        Assert.True(PumpUntil(server, () =>
        {
            lock (gunner.Gate)
                return gunner.World.Ships.TryGetValue(gunnerId, out var gg) && gg.BoardTurretId == slot;
        }, 4000) < 4000, "the gunner's client holds its own turret mount (passenger list)");

        // ── both rosters: the F4 pane model names the gunner on that mount ──
        // Which connection gets the carrier depends on server handling order (the
        // spawner hands the FIRST ship the turret hull), so the occupant's name is
        // whichever session ended up gunning — not always the one named "GUNNER".
        string gunnerName = ReferenceEquals(gunner, a) ? "PILOT" : "GUNNER";
        (int obs, string? occ) Resolve(ClientWorld world)
        {
            var (observers, occupant) = HudInventoryPaneBuilder.SourceShipOccupants(world.Ships, pilotId);
            return (observers, occupant(slot));
        }
        lock (pilot.Gate)
        {
            var (observers, name) = Resolve(pilot.World);
            Assert.Equal(0, observers);
            Assert.Equal(gunnerName, name);
        }
        lock (gunner.Gate)
        {
            var (observers, name) = Resolve(gunner.World);
            Assert.Equal(0, observers);
            Assert.Equal(gunnerName, name);
        }

        // P891: hangar uses station/boarding metadata, never a docked parent pose.
        lock (gunner.Gate)
        {
            Assert.True(gunner.World.Ships[gunnerId].StationId >= 0);
            Assert.Null(gunner.World.Ships[pilotId].Latest);
        }
        pilot.SendDocked();
        int launchTicks = PumpUntil(server, () => carrierShip.StationId is null, 400);
        Assert.True(launchTicks < 400);
        // Native launch delivers guaranteed SET_CLUSTER to every child in the
        // launch dispatch (fsship.cpp:758-767,918-929); no further tick required.
        bool launchedOnWire = SpinWait.SpinUntil(() =>
        {
            lock (gunner.Gate)
                return gunner.World.Ships[pilotId].Latest is not null
                    && gunner.World.Ships[gunnerId].StationId == -1;
        }, TimeSpan.FromSeconds(5));
        if (!launchedOnWire)
        {
            lock (gunner.Gate) Console.WriteLine($"P891 before: launch tick pose={gunner.World.Ships[pilotId].Latest?.Time}, child station={gunner.World.Ships[gunnerId].StationId}");
            int delayed = PumpUntil(server, () =>
            {
                lock (gunner.Gate) return gunner.World.Ships[pilotId].Latest is not null
                    && gunner.World.Ships[gunnerId].StationId == -1;
            }, 100);
            Console.WriteLine($"P891 before: usable flight state delayed {delayed} additional ticks ({delayed * 50} simulation ms)");
        }
        Assert.True(launchedOnWire, "parent launch pose and child launch state must arrive in the launch tick");
        Console.WriteLine($"P891 launch: request processed after {launchTicks} pump ticks; parent pose delivered with zero additional ticks (50 ms server cadence)");

        // ── the gunner's HUD model inputs, from the GUNNER's own wire state ──
        lock (gunner.Gate)
        {
            var world = gunner.World;
            var me = world.Ships[gunnerId];
            var parent = world.Ships[pilotId];

            // Source-ship pose (indicator.cpp:130): the parent's Latest is the
            // pose the HUD velocity marker/speed read; the boarded child has no
            // own stream.
            Assert.NotNull(parent.Latest);
            Assert.Equal(pilotId, me.ParentId);
            Assert.Equal(slot, me.BoardTurretId);

            // The TURRET weapon at the gunner's mount, visible to the gunner's
            // client through the same-side LOADOUT_CHANGE(96) grant (native
            // pship->GetMountedPart(ET_Weapon, turretID), indicator.cpp:199-204).
            var mounted = (parent.Loadout ?? []).FirstOrDefault(e => e.MountId == slot);
            Assert.Equal(weaponPartId, mounted.PartId);
            // Its projectile range is what TargetWeaponRange.IsInRange solves
            // against (speed × lifespan): strictly positive core data.
            Assert.True(projectileRange > 0f);
        }

        // Bounded real TCP sample: two clients with 400 unboarded cluster
        // candidates (the same candidate pressure as a 200v200 encounter).
        var mission = carrierShip.Mission!;
        var sector = carrierShip.Sector!;
        int add = 400 - sector.GetShipUpdateCandidates().Count;
        for (short i = 0; i < add; i++)
            mission.PlaceShip(new Ship((short)(20000 + i), gunnerShip.HullType,
                new SimVec(10000 + i * 100, 10000, 10000), SimVec.Zero, Orientation.Identity)
                { SideId = carrierShip.SideId }, sector);
        var costs = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            carrierShip.Deviation = -10000;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            server.TickSim();
            costs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            uint tick = (uint)server.LoopIterations;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (gunner.Gate) return gunner.World.Ships[pilotId].LastUpdateTick >= tick;
            }, TimeSpan.FromSeconds(5)), $"parent pose starved at pressure tick {tick}");
        }
        Console.WriteLine($"P891 TCP pressure: 400 candidates, 40/40 fresh source poses; tick ms mean={costs.Average():F4}, p95={costs.Order().ElementAt(37):F4}, max={costs.Max():F4}");

        Console.WriteLine($"P803-LOOPBACK-LIVE: gunner {gunnerId} boarded pilot {pilotId} at turret mount {slot}; both rosters name the occupant; turret weapon (range {projectileRange:0}m) resolved on the gunner's wire state");
    }

    private static SimVec StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);
}
