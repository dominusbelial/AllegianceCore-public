using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests for <see cref="ClientWorld"/>. No sockets, no server:
/// messages are constructed directly, exactly the way AllegianceCore's own
/// <c>Wire/*Tests.cs</c> construct-then-round-trip messages, and fed through
/// <see cref="ClientWorld.Apply"/> with the SAME ship id across all three
/// message types. The whole point is proving the upsert: three different
/// message types targeting one id must produce ONE <see cref="ShipEntity"/>.
/// </summary>
public class ClientWorldUnitTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    [Fact]
    public void ThreeMessageTypes_UpsertOneEntity_NotThree()
    {
        const short shipId = 42;
        var world = new ClientWorld();

        // Message 1 — the SET_CLUSTER(7) "here is your ship" assignment,
        // embedding the ship's spawn position at sim tick 99.
        var spawn = MakeShip(shipId, new Vector3(1000f, -2000f, 3000f));
        var setCluster = new SetCluster
        {
            SectorId = 7,
            Ship = ServerShipUpdate.FromModel(spawn.Id, spawn, 99u),
            Cookie = 0x2A,
        };

        // Message 2 — a SINGLE_SHIP_UPDATE(78) with a new position at tick 777.
        var moved = MakeShip(shipId, new Vector3(10f, 20f, 30f));
        var update = new SingleShipUpdate
        {
            OtTarget = 0,
            OidTarget = shipId,
            Ship = ServerShipUpdate.FromModel(moved.Id, moved, 777u),
            BIsRipcording = false,
        };

        // Message 3 — a SHIP_STATUS(90) placing the ship in sector 8 at station 3.
        var status = new ShipStatusMessage
        {
            ShipId = shipId,
            HullId = 502,
            SectorId = 8,
            StationId = 3,
            ParentId = -1,
            State = ShipStatusMessage.StateFlying,
            Unknown = false,
            Detected = true,
            StateTime = 999u,
        };

        world.Apply(setCluster);
        Assert.Single(world.Ships, e => e.Key == shipId);   // exactly one entity so far
        Assert.Equal(shipId, world.MyShipId);
        Assert.True(world.Ships[shipId].IsMine);
        Assert.Equal(7, world.Ships[shipId].SectorId);       // from SetCluster before any status
        Assert.Equal(99u, world.Ships[shipId].LastUpdateTick);

        world.Apply(update);                                  // same id, second message type
        world.Apply(status);                                  // same id, third message type

        Assert.Single(world.Ships);                           // STILL one — the whole point
        Assert.Equal(shipId, world.Ships.Keys.Single());
        Assert.Equal(shipId, world.MyShipId);                 // unchanged by later messages

        // Latest reflects the LAST ServerShipUpdate applied (the SingleShipUpdate).
        Assert.Equal(new Vector3(10f, 20f, 30f), world.Ships[shipId].Latest!.Position);
        Assert.Equal(777u, world.Ships[shipId].LastUpdateTick);

        // SectorId reflects the SHIP_STATUS value, overriding SetCluster's 7.
        Assert.Equal(8, world.Ships[shipId].SectorId);
        Assert.Equal(3, world.Ships[shipId].StationId);
        Assert.Equal(-1, world.Ships[shipId].ParentId);
        Assert.Equal(ShipStatusMessage.StateFlying, world.Ships[shipId].State);
        Assert.Equal(999u, world.Ships[shipId].StateTime);

        // The very same ShipEntity instance is being upserted, not replaced.
        var before = world.Ships[shipId];
        world.Apply(new ShipStatusMessage { ShipId = shipId, SectorId = 11, StationId = -1 });
        Assert.Same(before, world.Ships[shipId]);
    }

    [Fact]
    public void SingleShipUpdate_ForNonSelfShip_MovesTeammateEntitysLatest()
    {
        // The precise claim the README's old "not yet covered" line doubted:
        // teammate movement. The dispatch path is ALREADY unconditional —
        // ConnectionSession feeds every inbound SINGLE_SHIP_UPDATE to
        // ClientWorld.Apply regardless of OidTarget, and Apply upserts
        // Ships[OidTarget] with Latest. So a SingleShipUpdate naming a
        // NON-self id must move THAT teammate's entity's Latest, not be dropped
        // because the id isn't ours. (The reason it is never observed live is a
        // server-side broadcast gap in the read-only reference server, which
        // sends each ship's update only to its own peer — see README.)
        var world = WorldWithMyShip(8001);
        const short teammate = 8662;
        world.Apply(new ExportShip { ShipId = teammate, SideId = 8555, CharacterName = "alice" });
        Assert.True(world.Ships.ContainsKey(teammate));
        Assert.Null(world.Ships[teammate].Latest);            // no movement yet

        var moved = MakeShip(teammate, new Vector3(333f, -444f, 555f));
        world.Apply(new SingleShipUpdate
        {
            OtTarget = 0,
            OidTarget = teammate,                              // NOT my ship id
            Ship = ServerShipUpdate.FromModel(moved.Id, moved, 666u),
            BIsRipcording = false,
        });

        Assert.Equal(2, world.Ships.Count);                   // mine + teammate, no phantom
        Assert.Equal(new Vector3(333f, -444f, 555f), world.Ships[teammate].Latest!.Position);
        Assert.Equal(666u, world.Ships[teammate].LastUpdateTick);
        Assert.False(world.Ships[teammate].IsMine);           // still not mine
        Assert.Equal((short)8001, world.MyShipId);            // my own id untouched
    }

    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 909,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, TestHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                990u),
            Cookie = 0x9,
        });
        return world;
    }

    [Fact]
    public void DistinctShipIds_ProduceDistinctEntities()
    {
        var world = new ClientWorld();
        var a = MakeShip(1, Vector3.Zero);
        var b = MakeShip(2, new Vector3(5f, 5f, 5f));

        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(a.Id, a, 1u) });
        world.Apply(new SingleShipUpdate { OtTarget = 0, OidTarget = b.Id, Ship = ServerShipUpdate.FromModel(b.Id, b, 2u) });

        Assert.Equal(2, world.Ships.Count);
        Assert.Equal((short)1, world.MyShipId);   // set by the first SET_CLUSTER only
        Assert.Contains(b.Id, world.Ships.Keys);
    }

    [Fact]
    public void IgnoresUnknownMessageTypes()
    {
        var world = new ClientWorld();
        var a = MakeShip(9, Vector3.Zero);
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(a.Id, a, 1u) });
        int before = world.Ships.Count;

        // A message type ClientWorld does not handle must be a no-op.
        world.Apply(new JoinedMission());
        Assert.Equal(before, world.Ships.Count);
        Assert.Equal((short)9, world.MyShipId);
    }
}
