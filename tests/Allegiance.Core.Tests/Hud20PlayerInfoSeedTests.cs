using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-20 — native seeds every client with a PLAYERINFO(10) for every mission
/// ship, enemies and drones included (<c>FedSrv/FedSrv.CPP:2129-2134</c>,
/// <c>FedSrv/fsmission.cpp:3958-3968</c>; a new drone, <c>FedSrv.CPP:2269</c>),
/// carrying the ship's live experience (<c>FedSrv.CPP:2084</c>). The port seeds
/// the enemy / drone legs at the moment each (peer, ship) EXPORT latch is first
/// set (<c>SeedMissionPlayerInfo</c>); teammates keep the inline P63 send.
/// These tests drive the REAL export paths (cross-side sweep, same-side NPC
/// export in BroadcastShipStates serial + parallel) through the broadcast seams.
/// </summary>
[Collection("LiveNetwork")]
public sealed class Hud20PlayerInfoSeedTests
{
    private static readonly ConnectionId Recipient = new(701);

    private static Ship ShipAt(Mission mission, Sector sector, short id, short side,
        bool npc = false, short droneType = -1)
    {
        var hull = mission.Core.HullTypes.Values.First();
        var ship = new Ship(id, hull, new Vector3(id * 10, 0, 0), Vector3.Zero, Orientation.Identity)
        {
            SideId = side,
            IsNpc = npc,
            DroneTypeId = droneType,
            PilotType = npc ? Allegiance.Core.Sim.Ship.PilotTypeMiner : Allegiance.Core.Sim.Ship.PilotTypePlayer,
        };
        mission.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>Every (fmid, shipId) the peer received, in wire order.</summary>
    private static List<(ushort Fmid, short ShipId, PlayerInfo? Info)> Decode(
        Dictionary<ConnectionId, List<byte[]>> packets, ConnectionId conn)
    {
        var list = new List<(ushort, short, PlayerInfo?)>();
        if (!packets.TryGetValue(conn, out var sent)) return list;
        foreach (var packet in sent)
            foreach (var message in FedPacket.Unpack(packet))
            {
                if (message.Length < 4) continue;
                ushort fmid = BitConverter.ToUInt16(message, 2);
                if (fmid == PlayerInfo.Fmid)
                {
                    var info = PlayerInfo.Decode(message);
                    list.Add((fmid, info.ShipId, info));
                }
                else if (fmid == ExportShip.Fmid)
                    list.Add((fmid, ExportShip.Decode(message).ShipId, null));
            }
        return list;
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Enemy_and_enemy_drone_playerinfo_follows_their_first_cross_side_export_once()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var mission = server.TestMission!;
        var sector = server.TestSectors[0];

        var own = ShipAt(mission, sector, 20001, 0);
        var enemy = ShipAt(mission, sector, 20002, 1);
        enemy.AddKillBonus();
        enemy.AddKillBonus();
        enemy.AddKillBonus();                       // experience 3 -> 50*3/7 = 21.43%
        var drone = ShipAt(mission, sector, 20003, 1, npc: true, droneType: 40);

        server.TestSeedPeers([(Recipient, own, sector)]);

        // Not Started: the cross-side sweep (and so the seed) does not run.
        var notStarted = Decode(server.TestCollectBroadcast(1, parallel: false, step: "crossSideExports"), Recipient);
        Assert.DoesNotContain(notStarted, m => m.Fmid == PlayerInfo.Fmid);

        mission.Stage = MissionStage.Started;
        var first = Decode(server.TestCollectBroadcast(2, parallel: false, step: "crossSideExports"), Recipient);

        foreach (var id in new[] { enemy.Id, drone.Id })
        {
            int export = first.FindIndex(m => m.Fmid == ExportShip.Fmid && m.ShipId == id);
            int info = first.FindIndex(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == id);
            Assert.True(export >= 0, $"ship {id}: no cross-side EXPORT");
            Assert.True(info > export, $"ship {id}: PLAYERINFO must follow its EXPORT (native order)");
        }

        var enemyInfo = Assert.Single(first, m => m.Fmid == PlayerInfo.Fmid && m.ShipId == enemy.Id).Info!;
        // Native fExperience = GetExperience() (FedSrv.CPP:2084) — the raw streak.
        Assert.Equal(3f, enemyInfo.FExperience);
        Assert.Equal((short)1, enemyInfo.ISide);            // the enemy's own side
        Assert.Equal(PlayerInfo.NA, enemyInfo.DtidDrone);   // a human
        Assert.Equal(Allegiance.Core.Sim.Ship.PilotTypePlayer, enemyInfo.PilotType);

        var droneInfo = Assert.Single(first, m => m.Fmid == PlayerInfo.Fmid && m.ShipId == drone.Id).Info!;
        Assert.Equal((short)1, droneInfo.ISide);
        Assert.Equal((short)40, droneInfo.DtidDrone);       // FedSrv.CPP:2088-2091
        Assert.Equal(Allegiance.Core.Sim.Ship.PilotTypeMiner, droneInfo.PilotType);
        Assert.Equal(0f, droneInfo.FExperience);

        Assert.DoesNotContain(first, m => m.Fmid == PlayerInfo.Fmid && m.ShipId == own.Id);   // the join-burst send owns it

        // One shot per (recipient, ship), also across the P369 identity re-export:
        // native never re-sends experience after the seed (appmsg.cpp:2918-2928).
        for (uint t = 3; t < 3 + 64; t++)
            Assert.DoesNotContain(Decode(server.TestCollectBroadcast(t, parallel: false, step: "crossSideExports"), Recipient),
                m => m.Fmid == PlayerInfo.Fmid);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_side_drone_playerinfo_follows_its_npc_export_once(bool parallel)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var mission = server.TestMission!;
        var sector = server.TestSectors[0];
        mission.Stage = MissionStage.Started;

        var own = ShipAt(mission, sector, 20011, 0);
        var drone = ShipAt(mission, sector, 20012, 0, npc: true, droneType: 41);
        server.TestSeedPeers([(Recipient, own, sector)]);

        var first = Decode(server.TestCollectBroadcast(1, parallel, step: "shipStates"), Recipient);
        int export = first.FindIndex(m => m.Fmid == ExportShip.Fmid && m.ShipId == drone.Id);
        int info = first.FindIndex(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == drone.Id);
        Assert.True(export >= 0, "no same-side NPC EXPORT");
        Assert.True(info > export, "the drone's PLAYERINFO must follow its EXPORT");
        var droneInfo = first[info].Info!;
        Assert.Equal((short)0, droneInfo.ISide);
        Assert.Equal((short)41, droneInfo.DtidDrone);

        for (uint t = 2; t < 10; t++)
            Assert.DoesNotContain(Decode(server.TestCollectBroadcast(t, parallel, step: "both"), Recipient),
                m => m.Fmid == PlayerInfo.Fmid);
    }

    /// <summary>HUD-20 review (MAJOR) — two same-side peers in a STARTED mission
    /// each get exactly ONE PLAYERINFO about the other: the inline P63 teammate
    /// send owns teammates, and the mission-wide seed must never add a second
    /// (which would re-run the native client's CreateObject). Driven over many
    /// ticks of the real shipStates + cross-side export steps, serial and
    /// parallel.</summary>
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_same_side_peers_in_a_started_mission_get_exactly_one_playerinfo_each(bool parallel)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var mission = server.TestMission!;
        var sector = server.TestSectors[0];
        mission.Stage = MissionStage.Started;

        var a = ShipAt(mission, sector, 20021, 0);
        var b = ShipAt(mission, sector, 20022, 0);
        var enemy = ShipAt(mission, sector, 20023, 1);   // keeps the cross-side seed path live
        ConnectionId ca = new(711), cb = new(712), ce = new(713);
        server.TestSeedPeers([(ca, a, sector), (cb, b, sector), (ce, enemy, sector)]);

        var infos = new Dictionary<ConnectionId, List<short>> { [ca] = new(), [cb] = new(), [ce] = new() };
        for (uint t = 1; t <= 40; t++)
        {
            foreach (var step in new[] { "crossSideExports", "shipStates" })
            {
                var packets = server.TestCollectBroadcast(t, parallel, step);
                foreach (var conn in infos.Keys)
                    infos[conn].AddRange(Decode(packets, conn).Where(m => m.Fmid == PlayerInfo.Fmid).Select(m => m.ShipId));
            }
        }

        Assert.Equal(1, infos[ca].Count(id => id == b.Id));
        Assert.Equal(1, infos[cb].Count(id => id == a.Id));
        Assert.Equal(1, infos[ca].Count(id => id == enemy.Id));
        Assert.Equal(1, infos[cb].Count(id => id == enemy.Id));
        Assert.Equal(1, infos[ce].Count(id => id == a.Id));
        Assert.Equal(1, infos[ce].Count(id => id == b.Id));
    }
}

/// <summary>HUD-20 — the observer's wire turret id. Native
/// <c>PassengerData.turretID</c> is a <c>Mount</c> and <c>NA</c> (-1) is an
/// observer (<c>Inc/Messages.h:30-34</c>; <c>appmsg.cpp:2064-2068</c>). The
/// server used to encode <c>null</c> as mount 0.</summary>
public sealed class Hud20ObserverIdTests
{
    private static DataHullTypeIGC Hull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "t",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f, MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f], Thrust: 52500f, SideMultiplier: 4f,
        BackMultiplier: 4f, ScannerRange: 1000f, MaxFuel: 100f, Ecm: 0f, Length: 30f,
        MaxEnergy: 0f, RechargeRate: 0f, RipcordSpeed: 0f, RipcordCost: 0f,
        MaxAmmo: 0, HullId: 502, SuccessorHullId: -1, MaxWeapons: 0,
        MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540, DefenseType: 0,
        CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship Child(short id, short? turret) => new(
        id, Hull(), new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { TurretId = turret };

    [Fact]
    public void Observer_encodes_0xFF_and_a_gunner_its_mount()
    {
        var observer = Srv.PassengerRecord(Child(91, null));
        Assert.Equal(91, observer.ShipId);
        Assert.Equal(0xFF, observer.TurretId);          // NA on the wire

        var gunner = Srv.PassengerRecord(Child(92, 2));
        Assert.Equal(2, gunner.TurretId);
    }
}
