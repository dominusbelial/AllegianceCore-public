using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P775 — the client half of the native PLAYERINFO moments: because the
/// joiner's PLAYERINFO now precedes its JOIN_SIDE (native sends it before
/// AddPlayerToSide, fsmission.cpp:406-411 → appmsg.cpp:2909-2915), a genuine
/// mid-game join fires the P736 salutations (clintlib.cpp:4142-4160) on the
/// port client, the roster is populated for every human, and drones learned
/// at creation / in the join burst carry their PlayerInfo. Test 7 pins the
/// Step-0 render/target audit: a PLAYERINFO-only ship (no pose, sector 0)
/// must never be drawn or targetable.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P775ClientPlayerInfoMomentsTests
{
    private static void Pump(Srv server, Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(2); }
        Assert.True(done(), "Timed out waiting for server state");
    }

    private static ConnectionSession Seat(Srv server, string name, short sideId)
    {
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
        Pump(server, () => session.Ack is not null);
        session.SendPositionReq(sideId);
        Pump(server, () => { lock (session.Gate) return session.World.MyShipId is not null; });
        return session;
    }

    /// <summary>Test 6 — after a mid-game join, an existing teammate's
    /// ClientWorld records SalRecruitsArrived for the joiner and an enemy
    /// records SalEnemyJoiners (ClientWorld.cs Apply(JoinSide), the P736
    /// salutation — native clintlib.cpp:4142-4160); every session's
    /// ScoreRoster lists every human; an enemy drone carries its
    /// PlayerInfo/DtidDrone (target pane, HUD-20); and a kill by the own ship
    /// still raises KillBonusPercent (P773).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Joiner_salutation_and_roster_are_populated_on_the_port_client()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "P775c", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        server.Missions[0].Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var side0 = mission.Sides[0].Id;
        var side1 = mission.Sides[1].Id;
        short droneType = mission.Core.DroneTypes.Keys.First();

        using var mate = Seat(server, "Mate", side0);      // existing teammate
        using var enemy = Seat(server, "Foe", side1);      // enemy
        var enemyDrone = server.SpawnNpcShip(mission, mission.Sectors[0], side1,
            new Vector3(300, 0, 0), droneType);            // created BEFORE the joiner → learned in the join burst

        using var joiner = Seat(server, "Newbie", side0);  // the mid-game joiner
        short joinerShip = joiner.World.MyShipId!.Value;
        short mateShip = mate.World.MyShipId!.Value;

        // The salutation: the mate's client must record SalRecruitsArrived for
        // the joiner (same side), the enemy's SalEnemyJoiners (clintlib.cpp:4155/:4162).
        // TakeSoundEvents drains — capture once, then assert on the capture.
        List<ClientSoundEvent> mateEvents = new(), enemyEvents = new();
        Pump(server, () =>
        {
            lock (mate.Gate)
            {
                mateEvents.AddRange(mate.World.TakeSoundEvents());
                return mateEvents.Any(e => e.Kind == ClientSoundEventKind.SalRecruitsArrived && e.Id == joinerShip);
            }
        });
        Assert.Contains(mateEvents, e => e.Kind == ClientSoundEventKind.SalRecruitsArrived && e.Id == joinerShip);
        Pump(server, () =>
        {
            lock (enemy.Gate)
            {
                enemyEvents.AddRange(enemy.World.TakeSoundEvents());
                return enemyEvents.Any(e => e.Kind == ClientSoundEventKind.SalEnemyJoiners && e.Id == joinerShip);
            }
        });
        Assert.Contains(enemyEvents, e => e.Kind == ClientSoundEventKind.SalEnemyJoiners && e.Id == joinerShip);

        // Every session's roster lists every human.
        string[] names = ["Mate", "Foe", "Newbie"];
        Pump(server, () => names.All(n => Roster(mate).Any(e => e.CharacterName == n))
            && names.All(n => Roster(enemy).Any(e => e.CharacterName == n))
            && names.All(n => Roster(joiner).Any(e => e.CharacterName == n)));

        // The enemy drone (learned via the join burst, every sector) carries
        // its PlayerInfo — the target pane / HUD-20 inputs.
        ShipEntity? drone;
        lock (joiner.Gate) joiner.World.Ships.TryGetValue(enemyDrone.Id, out drone);
        Assert.NotNull(drone);
        Assert.NotNull(drone!.Player);
        Assert.Equal(droneType, drone.Player!.DtidDrone);
        Assert.False(drone.Player!.IsHuman);            // a drone is never a funding/roster pilot

        // A kill by the own ship still raises KillBonusPercent (P773): the
        // joiner's ship destroys the enemy drone.
        var killerEntry = server.Ships.Single(s => s.Ship.Id == joinerShip);
        enemyDrone.ReceiveDamage(0, 1e9f, server.SimNow, enemyDrone.Position, Vector3.Zero, killerEntry.Ship);
        Pump(server, () => { lock (joiner.Gate) return joiner.World.KillBonusPercent > 0f; });
    }

    private static ScoreRosterEntry[] Roster(ConnectionSession session)
    { lock (session.Gate) return session.World.ScoreRoster.ToArray(); }

    /// <summary>A minimal decodable ServerShipUpdate body (the BotFixRegression
    /// Pose pattern): ship id at offset 0, x at offset 12.</summary>
    private static ServerShipUpdate Pose(short id, float x = 0)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes, id);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), x);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    /// <summary>Test 7 — a PLAYERINFO-only ship (created by PLAYERINFO alone:
    /// hull 0, SectorId 0, no pose) is never drawn and never targetable
    /// (ShipEntity.CanDrawInSector needs PoseSectorId + Latest;
    /// GatherSectorCandidates needs Latest + a real sector), and it stays out
    /// of every list the Step-0 audit named: the sector map's known-enemy
    /// sectors (SectorMapQueries.KnownEnemySectors) and the investment-funding
    /// pilot list stays correct (drones are never human).</summary>
    [Fact]
    public void Playerinfo_only_ship_is_not_drawn_or_targetable()
    {
        var world = new ClientWorld();
        // The viewer: a seated ship on side 0 in sector 7, with a real pose.
        world.Apply(new ExportShip { ShipId = 10, SideId = 0 });
        world.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "me", PilotType = PlayerInfo.PilotTypePlayer, DtidDrone = PlayerInfo.NA });
        world.Apply(new JoinSide { ShipId = 10, SideId = 0 });
        world.Apply(new SetCluster { Ship = Pose(10, 0), SectorId = 7 });

        // A PLAYERINFO for an unseen enemy DRONE — the join-burst seed of a
        // drone in another sector (native fsmission.cpp:3958-3968).
        world.Apply(new PlayerInfo
        {
            ShipId = 20, ISide = 1, CharacterName = "Ship 20",
            PilotType = Ship.PilotTypeMiner, DtidDrone = 40,
        });

        var e = world.Ships[20];
        Assert.NotNull(e.Player);                       // the PlayerInfo is kept on the ship
        Assert.Equal((short)1, e.SideId);
        Assert.Null(e.Latest);                          // never posed
        Assert.Null(e.PoseSectorId);
        for (short sector = 0; sector < 12; sector++)
            Assert.False(e.CanDrawInSector(sector), $"sector {sector}: a never-posed ship must not draw");

        // Sector map: KnownEnemySectors counts only HasRealIdentity ships with
        // a nonzero status sector — the PLAYERINFO-only drone adds nothing.
        var known = SectorMapQueries.KnownEnemySectors(
            world.Ships.Values.Select(s => (s.SectorId, s.SideId, s.HasRealIdentity, s.Unknown)), 0);
        Assert.DoesNotContain(known, kv => kv.Key == 0 && kv.Value > 0);
        Assert.Empty(known);
    }

    /// <summary>P775 review (3c) — allegiance-mcp's bot LOD counts a human as
    /// "near" a sector only once that human's ship has a pose and a real
    /// sector. A PLAYERINFO-only human (SectorId = the C# default 0, a real
    /// cluster id) must not mark cluster 0 as human-occupied, or every bot in
    /// cluster 0 runs at full decision rate for nothing.</summary>
    [Fact]
    public void Bot_lod_ignores_an_unseen_human_until_its_sector_is_known()
    {
        var world = new ClientWorld();
        world.Apply(new PlayerInfo
        {
            ShipId = 30, ISide = 1, CharacterName = "Foe",
            PilotType = PlayerInfo.PilotTypePlayer, DtidDrone = PlayerInfo.NA,
        });
        var foe = world.Ships[30];
        Assert.Equal((short)0, foe.SectorId);
        Assert.Null(Allegiance.Tools.McpSpawner.BotLodPolicy.HumanSector(foe));

        // Real telemetry: a status (sector 4) and a pose.
        world.Apply(new ShipStatusMessage { ShipId = 30, HullId = 1, SectorId = 4, StationId = -1, ParentId = -1 });
        world.Apply(new SetCluster { Ship = Pose(30, 0), SectorId = 4 });
        Assert.Equal((short)4, Allegiance.Tools.McpSpawner.BotLodPolicy.HumanSector(foe));
    }
}
