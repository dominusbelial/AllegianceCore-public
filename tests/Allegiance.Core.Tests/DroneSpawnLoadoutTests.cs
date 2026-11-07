using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using FltBot = global::Allegiance.Bot.Bot;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P956 — native <c>CreateDrone</c>'s preferred-loadout fill
/// (<c>Igc/common.cpp:2462-2480</c>) ported into <c>Server.SpawnNpcShip</c>.
/// Every drone type in the REAL core (PCore001) is spawned through the real
/// server primitive and compared, table-driven from the core, against the mount
/// list the native loop derives: same order, <c>ET_Weapon</c> up to
/// <c>GetMaxWeapons()</c> and mount 0 for everything else, only free sockets the
/// hull <c>CanMount</c>, amount <c>0x7fff</c> clamped by pack capacity /
/// expendable size (native <c>SetAmount</c>, <c>packigc.h:102-123</c>,
/// <c>launchertypeigc.h:130-145</c>).
/// <para>The second test is the wire leg: a seated same-side client must receive
/// the mounted parts exactly like a player loadout — in the same-side NPC
/// <c>EXPORT(26)</c> parts tail (P366) and in the spawn-time
/// <c>LOADOUT_CHANGE(96)</c> (native <c>QueueLoadoutChange</c> when the ship
/// joins its cluster, <c>FedSrv/fsship.cpp:511-542</c>).</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class DroneSpawnLoadoutTests
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
            new Gm { Name = "P956 Drone Loadout", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 956u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 95600000000000000ul, DrmHash = "p956droneloadout",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>One native <c>CreateAndAddPart(ppt, mount, 0x7fff)</c> result.</summary>
    private readonly record struct MountSpec(short PartId, byte MountId, short Amount);

    /// <summary>The native <c>SetAmount(0x7fff)</c> ceiling for one part type
    /// (<c>packigc.h:102-123</c>: hull max ammo/fuel;
    /// <c>launchertypeigc.h:130-145</c>: hull capacity / expendable size, min 1;
    /// single-item equipment keeps the requested amount).</summary>
    private static short NativeAmount(DataHullTypeIGC hull, StaticCore core, DataPartTypeIGC ppt)
    {
        switch (ppt.Type)
        {
            case DataPartTypeIGC.Pack pack:
                // c_packAmmo = 0 / c_packFuel = 1 (Igc/igc.h:549-550); native clamps
                // to the hull's max ammo / max fuel (packigc.h:102-123).
                return pack.PackType == 0 ? hull.MaxAmmo : (short)Math.Min(short.MaxValue, hull.MaxFuel);
            case DataPartTypeIGC.MissileLauncher m when core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var mt):
                return (short)Math.Max(1, hull.CapacityMagazine / Math.Max(1, (int)mt.LauncherExpendableSize));
            case DataPartTypeIGC.MineLauncher m when core.MineTypes.TryGetValue(m.ExpendableTypeId, out var mt):
                return (short)Math.Max(1, hull.CapacityDispenser / Math.Max(1, (int)mt.LauncherExpendableSize));
            case DataPartTypeIGC.ProbeLauncher m when core.ProbeTypes.TryGetValue(m.ExpendableTypeId, out var mt):
                return (short)Math.Max(1, hull.CapacityDispenser / Math.Max(1, (int)mt.LauncherExpendableSize));
            case DataPartTypeIGC.ChaffLauncher m when core.ChaffTypes.TryGetValue(m.ExpendableTypeId, out var mt):
                return (short)Math.Max(1, hull.CapacityChaffLauncher / Math.Max(1, (int)mt.LauncherExpendableSize));
            default:
                return 0x7fff;   // weapons & other single-item equipment
        }
    }

    /// <summary>The mount list native <c>CreateDrone</c> produces for a hull,
    /// written from the native loop only (Igc/common.cpp:2462-2480).</summary>
    private static List<MountSpec> NativeMounts(DataHullTypeIGC hull, StaticCore core)
    {
        var mounted = new List<MountSpec>();
        var occupied = new List<(EquipmentType Et, int Mount)>();
        foreach (var rawPartId in hull.PreferredPartsTypes)
        {
            if (rawPartId == -1) break;                       // NA terminates the list
            if (!core.PartTypes.TryGetValue(rawPartId, out var ppt)
                && !core.TryBuildExpendableLauncher(rawPartId, out ppt)) continue;
            var et = ppt.EquipmentType;
            int mountMax = et == EquipmentType.Weapon ? hull.MaxWeapons : 1;
            for (int i = 0; i < mountMax; i++)
            {
                // (GetMountedPart(et, i) == NULL) && pht->CanMount(ppt, i)
                if (occupied.Contains((et, i))) continue;
                if (!hull.CanMountPart(ppt, i)) continue;
                occupied.Add((et, i));
                mounted.Add(new MountSpec(ppt.PartId, (byte)i, NativeAmount(hull, core, ppt)));
            }
        }
        return mounted;
    }

    private static List<MountSpec> ActualMounts(Ship ship) =>
        ship.Parts.Select(p => new MountSpec(p.PartId, p.MountId, p.Amount)).ToList();

    private static IEnumerable<DataDroneTypeIGC> CoreDroneTypes(StaticCore core) =>
        core.DroneTypes.Values.OrderBy(d => d.DroneTypeId);

    private static string Table(DataDroneTypeIGC dt, DataHullTypeIGC hull, IEnumerable<MountSpec> mounts) =>
        $"drone {dt.DroneTypeId} '{dt.Name}' hull {hull.HullId} '{hull.Name}' pilot {dt.PilotType} " +
        $"preferred [{string.Join(",", hull.PreferredPartsTypes.TakeWhile(p => p != -1))}] -> " +
        string.Join(" ", mounts.Select(m => $"{m.PartId}@{m.MountId}x{m.Amount}"));

    /// <summary>⭐ Every drone type in the real core spawns with exactly native's
    /// preferred-part mounts, table-driven from the core.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_core_drone_type_spawns_with_native_preferred_part_mounts()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;              // pristine arena
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        short side = mission.Sides[0].Id;

        int types = 0, armed = 0, emptyHulls = 0, absentHulls = 0;
        float x = 800f;
        foreach (var dt in CoreDroneTypes(mission.Core))
        {
            // PCore001's reserved drone type 38 names hull 231, which the shipped
            // hull table does not carry; SpawnNpcShip's documented defensive
            // fallback then uses the core's lowest-id hull (P458 comment). Every
            // other type must land on the hull its drone-type record names
            // (native pdronetype->GetHullTypeID(), FedSrv.CPP:2228).
            DataHullTypeIGC? namedHull = mission.Core.HullTypes.TryGetValue(dt.HullTypeId, out var h) ? h : null;
            var ship = server.SpawnNpcShip(mission, sector, side, new Vector3(x, 600f, 600f), dt.DroneTypeId);
            var hull = namedHull ?? ship.HullType;
            if (namedHull is { } nh) Assert.Equal(nh.HullId, ship.HullType.HullId);
            else absentHulls++;
            var expected = NativeMounts(hull, mission.Core);
            var actual = ActualMounts(ship);
            Console.WriteLine($"[p956] {Table(dt, hull, actual)}" +
                (namedHull is null ? $"  [drone hull {dt.HullTypeId} absent from the core -> fallback hull]" : ""));
            Assert.Equal(expected, actual);
            Assert.Equal(dt.PilotType, ship.PilotType);
            Assert.True(ship.Autopilot, "native CreateDrone ends with SetAutopilot(true) (Igc/common.cpp:2484)");
            if (ship.HasDamagingPrimaryWeapon) armed++;
            if (expected.Count == 0) emptyHulls++;
            Assert.All(ship.Parts, p =>
                Assert.Equal(NativeAmount(hull, mission.Core, p.Type),
                    Ship.ClampPartAmount(hull, mission.Core, p.Type, 0x7fff)));
            types++;
            x += 400f;
        }

        Assert.True(types > 0, "PCore001 must expose drone types");
        // Native's whole point: a purchased armed drone type arrives armed, so it
        // can pass the damaging-primary defend guard (Igc/shipIGC.cpp:2680).
        Assert.True(armed > 0, "at least one core drone type must spawn with a damaging primary weapon");
        Assert.True(armed < types, "some core drone hulls carry no damaging primary (a bare-hull case must remain representable)");
        Console.WriteLine($"[p956] {types} drone type(s) spawned; {armed} armed with a damaging primary, " +
            $"{emptyHulls} with no preferred parts, {absentHulls} with an absent hull record (fallback)");
    }

    /// <summary>⭐ The mounted parts reach a same-side client exactly like a player
    /// loadout: the same-side NPC EXPORT(26) parts tail and the spawn-time
    /// LOADOUT_CHANGE(96) both carry native's preferred mounts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spawned_drone_parts_reach_a_same_side_client_as_export_and_loadout_change()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var bot = new FltBot();
        try
        {
            bot.Connect(server.BoundPort, "p956_wire");
            bot.SendLogonRequest(Request("p956_wire"));
            Assert.True(Pump(server, () => bot.ShipAssignment is not null, 8000) < 8000,
                "the observing client must be seated");
            var peer = server.Ships.First(l => l.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            var sector = peer.Sector!;
            short side = peer.SideId!.Value;

            int checkedTypes = 0;
            float x = 2000f;
            foreach (var dt in CoreDroneTypes(mission.Core))
            {
                DataHullTypeIGC? namedHull = mission.Core.HullTypes.TryGetValue(dt.HullTypeId, out var h) ? h : null;
                bot.ResetLoadoutChanges();
                var ship = server.SpawnNpcShip(mission, sector, side, new Vector3(x, 900f, 900f), dt.DroneTypeId);
                var hull = namedHull ?? ship.HullType;   // documented fallback for an absent hull record
                var expected = NativeMounts(hull, mission.Core);
                x += 400f;

                // (1) Same-side NPC EXPORT(26) — the identity message a client
                // creates the drone object from — carries the mounted parts.
                Pump(server, () => bot.Exports.Any(e => e.ShipId == ship.Id), 4000);
                var export = bot.Exports.FirstOrDefault(e => e.ShipId == ship.Id);
                Assert.NotNull(export);
                Assert.Equal(hull.HullId, export!.HullId);
                Assert.Equal(side, export.SideId);
                Assert.Equal(expected, export.Parts.Select(p => new MountSpec(p.PartId, p.MountId, p.Amount)).ToList());

                // (2) Spawn-time LOADOUT_CHANGE(96) — native QueueLoadoutChange when
                // the drone joins the sector (FedSrv/fsship.cpp:511-542) — carries
                // the same whole loadout. A hull with no preferred parts changes
                // nothing, so native queues nothing there either.
                if (expected.Count > 0)
                {
                    var sid = ship.Id;
                    var want = expected;
                    Assert.True(Pump(server, () => bot.LoadoutChanges.Any(m => m.SidShip == sid
                            && want.SequenceEqual(m.Parts
                                .Where(p => p.MountId < 0x80)         // equipped, not cargo
                                .Select(p => new MountSpec(p.PartId, p.MountId, p.Amount)))), 4000) < 4000,
                        $"LOADOUT_CHANGE(96) for drone {sid} must carry its preferred mounts");
                    var change = bot.LoadoutChanges.Last(m => m.SidShip == sid);
                    Assert.Equal(hull.HullId, change.HullId);
                }
                checkedTypes++;
                Console.WriteLine($"[p956-wire] {Table(dt, hull, expected)}");
            }
            Assert.True(checkedTypes > 0, "PCore001 must expose drone types");
        }
        finally
        {
            bot.Dispose();
        }
    }

    /// <summary>⭐ A spawned (production-primitive) Wingman defends a charge and
    /// really damages a threat near it — no hand-arming anywhere in this test.
    /// Native CreateDrone gives the drone its hull's preferred parts
    /// (<c>Igc/common.cpp:2462-2480</c>), the Wingman hull (212 'Interceptor')
    /// prefers <c>120</c> PW Mini-Gun in both weapon sockets, so the
    /// damaging-primary guard (<c>Igc/shipIGC.cpp:2680</c>) passes and the
    /// charge-centred acquire (<c>:2688-2695</c>) engages the threat.
    /// <para>PCore001's Wingman drone type (id 40) requires tech bit 7, which no
    /// civ's base techs carry (measured; the same finding P459 records), so no
    /// civ ever produces a reachable Wingman drone-type bucket — this test drives
    /// the exact spawn primitive <c>CompleteDroneType</c> calls, like the P459 /
    /// P952 fixtures.</para></summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spawned_wingman_defends_a_charge_and_damages_a_threat_without_manual_arming()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var ally = mission.Sides[0];
        var enemySide = mission.Sides[1];
        var wingType = mission.Core.DroneTypes.Values
            .Where(d => d.PilotType == Ship.PilotTypeWingman)
            .OrderBy(d => d.DroneTypeId)
            .First();                                          // id 40 'Wingman' (hull 212 Interceptor)
        var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.Length).First();

        Vector3 cp = new(600f, 610f, 605f);
        var charge = new Ship(19001, hull, cp, Vector3.Zero, Orientation.Identity) { SideId = ally.Id };
        mission.PlaceShip(charge, sector);
        Vector3 ep = new(1000f, 613f, 597f);                  // ~400u from the charge (inside the 1500 scan)
        var enemy = new Ship(19002, hull, ep, Vector3.Zero, Orientation.Identity) { SideId = enemySide.Id };
        mission.PlaceShip(enemy, sector);

        var wingman = server.SpawnNpcShip(mission, sector, ally.Id, new Vector3(658f, 599f, 611f), wingType.DroneTypeId);
        Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
        // The spawn alone armed it (CreateDrone preferred parts) — nothing here mounts.
        Assert.True(wingman.HasDamagingPrimaryWeapon,
            "the spawned Wingman must carry a damaging primary weapon from its hull's preferred parts");
        Assert.Equal(2, wingman.Parts.Count(p => p.EquipmentType == EquipmentType.Weapon));
        wingman.DefendTarget = charge;

        // Acquire: the charge-centred scan engages the threat near the charge.
        Assert.True(Pump(server, () => wingman.AttackTarget == enemy, 2000) < 2000,
            "the spawned Wingman must acquire the threat near its charge");
        Assert.Same(charge, wingman.DefendTarget);

        // Attack: it closes and its mounted gun REALLY damages the threat.
        bool sawTrigger = false;
        int ticks = 0;
        while (ticks < 20000 && enemy.Fraction >= 1f)
        {
            server.TickSim(); ticks++; Thread.Sleep(1);
            if ((wingman.StateM & ShipControlState.AllWeapons) != 0) sawTrigger = true;
        }
        Console.WriteLine($"[p956-defend] wingman {wingman.Id} attacked {enemy.Id}: ticks={ticks} " +
            $"sawTrigger={sawTrigger} enemyFraction={enemy.Fraction:R}");
        Assert.True(sawTrigger, "the Wingman must hold the trigger at least once");
        Assert.True(enemy.Fraction < 1f, $"the threat must take real damage: fraction={enemy.Fraction:R} after {ticks} ticks");
    }
}
