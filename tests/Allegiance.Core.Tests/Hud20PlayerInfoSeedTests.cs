using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-20 — native seeds every client with a PLAYERINFO(10) for every mission
/// ship, enemies and drones included (<c>FedSrv/FedSrv.CPP:2129-2134</c>,
/// <c>FedSrv/fsmission.cpp:3958-3968</c>; a new drone, <c>FedSrv.CPP:2269</c>),
/// carrying the ship's live experience (<c>FedSrv.CPP:2084</c>).
/// P775 — the seed moments are exactly native's three SendPlayerInfo call
/// sites (mission join joiner→mission / mission→joiner, drone creation), so
/// the lazy per-sighting sends are gone: the <c>crossSideExports</c> sweep and
/// both <c>BroadcastShipStates</c> modes emit NO PLAYERINFO at all, and the
/// wire content checks (FExperience / DtidDrone / ISide / PilotType) live on
/// the new creation and join sends.
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

    /// <summary>P775 — the EXPORT sweeps never carry PLAYERINFO anymore:
    /// native's SendPlayerInfo has exactly three call sites (mission join ×2
    /// and drone creation, FedSrv.CPP:2056-2135), none of them a sighting.
    /// Driven through the broadcast seams in both broadcast modes.</summary>
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Cross_side_exports_and_both_broadcast_modes_emit_no_playerinfo(bool parallel)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var mission = server.TestMission!;
        var sector = server.TestSectors[0];
        mission.Stage = MissionStage.Started;

        var own = ShipAt(mission, sector, 20001, 0);
        var enemy = ShipAt(mission, sector, 20002, 1);
        var drone = ShipAt(mission, sector, 20003, 1, npc: true, droneType: 40);
        server.TestSeedPeers([(Recipient, own, sector)]);

        for (uint t = 1; t <= 24; t++)
        {
            var fromShipStates = Decode(server.TestCollectBroadcast(t, parallel, step: "shipStates"), Recipient);
            var fromSweep = Decode(server.TestCollectBroadcast(t, parallel, step: "crossSideExports"), Recipient);
            Assert.DoesNotContain(fromShipStates, m => m.Fmid == PlayerInfo.Fmid);
            Assert.DoesNotContain(fromSweep, m => m.Fmid == PlayerInfo.Fmid);
        }
    }

    /// <summary>P775 — the join burst (native moment (2),
    /// fsmission.cpp:3958-3968) carries the enemy human's real content:
    /// fExperience = the raw kill streak (FedSrv.CPP:2084), ISide the enemy's
    /// own side, dtidDrone NA (a human, :2088-2090), pilotType player.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Join_burst_playerinfo_carries_enemy_human_content()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        server.Missions[0].Stage = MissionStage.Started;

        LogonRequest Req(string name) => new()
        {
            CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = 0x919u,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
            SteamID = 9100000000000000000ul, DrmHash = "p775hud20",
        };
        var enemyBot = new global::Allegiance.Bot.Bot();
        enemyBot.Connect(server.BoundPort, "Foe");
        enemyBot.SendLogonRequest(Req("Foe"));
        int pump = 0;
        while (enemyBot.ShipAssignment is null && pump++ < 6000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(pump < 6000, "enemy bot must seat");
        var enemyShip = server.Ships.First(s => s.Ship.Id == enemyBot.ShipAssignment!.Ship.ShipId);
        short enemySide = enemyShip.Ship.SideId!.Value;      // whatever side the auto-select seated it on
        enemyShip.Ship.AddKillBonus();
        enemyShip.Ship.AddKillBonus();
        enemyShip.Ship.AddKillBonus();                       // experience 3 -> 50*3/7 = 21.43% once derived

        var joinerBot = new global::Allegiance.Bot.Bot();
        joinerBot.Connect(server.BoundPort, "Late");
        joinerBot.SendLogonRequest(Req("Late"));
        joinerBot.SendPositionReq(server.Missions[0].Sides[0].Id);
        while (joinerBot.ShipAssignment is null && pump++ < 6000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(pump < 6000, "joiner bot must seat");

        // The join burst's tail (G after SET_CLUSTER) may still be in flight
        // when ShipAssignment lands — wait for the decode to catch up.
        while (joinerBot.PlayerInfos.All(p => p.ShipId != enemyShip.Ship.Id) && pump++ < 6000)
        { server.TickSim(); Thread.Sleep(1); }
        var all = joinerBot.PlayerInfos.ToList();
        Console.WriteLine($"HUD20DBG after wait: total={all.Count} ships=[{string.Join(",", all.Select(p => p.ShipId))}] enemy={enemyShip.Ship.Id} pump={pump}");
        var infos = joinerBot.PlayerInfos.Where(p => p.ShipId == enemyShip.Ship.Id).ToList();
        var info = Assert.Single(infos);                     // exactly one (the join burst; no sighting resend)
        Assert.Equal(3f, info.FExperience);                  // native fExperience = GetExperience() (FedSrv.CPP:2084)
        Assert.Equal(enemySide, info.ISide);                 // the enemy's own side
        Assert.Equal(PlayerInfo.NA, info.DtidDrone);         // a human
        Assert.Equal(Allegiance.Core.Sim.Ship.PilotTypePlayer, info.PilotType);
        joinerBot.Dispose();
        enemyBot.Dispose();
    }

    /// <summary>P775 — the creation announce (native moment (3),
    /// FedSrv.CPP:2269) carries the drone's content: dtidDrone the drone type's
    /// object id (:2088-2091), pilotType the drone type's pilot, ISide the
    /// drone's side, fExperience 0 (a drone starts unranked).</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Creation_announce_playerinfo_carries_drone_content()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var sector = server.TestSectors[0];
        short droneType = mission.Core.DroneTypes.Keys.First();

        var own = ShipAt(mission, sector, 20011, 0);
        server.TestSeedPeers([(Recipient, own, sector)]);
        // The viewer holds a real peer slot, so the creation announce reaches it.
        server._testSendCapture = (conn, pkt) => { };

        var infos = new List<PlayerInfo>();
        server._testSendCapture = (conn, pkt) =>
        {
            if (conn != Recipient) return;
            foreach (var message in FedPacket.Unpack(pkt))
                if (message.Length >= 4 && BitConverter.ToUInt16(message, 2) == PlayerInfo.Fmid)
                    lock (infos) infos.Add(PlayerInfo.Decode(message));
        };
        var drone = server.SpawnNpcShip(mission, sector, 0, new Vector3(5, 0, 0), droneType);
        server._testSendCapture = null;

        var info = Assert.Single(infos);
        Assert.Equal(drone.Id, info.ShipId);
        Assert.Equal((short)0, info.ISide);                  // the drone's own side
        Assert.Equal(droneType, info.DtidDrone);             // FedSrv.CPP:2088-2091
        Assert.Equal(mission.Core.DroneTypes[droneType].PilotType, info.PilotType);   // the drone type's real pilot
        Assert.Equal(0f, info.FExperience);
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
