using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
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

    [Fact]
    public void TryGetSideColor_resolves_from_applied_roster_and_defaults_to_gray()
    {
        var world = new ClientWorld();
        // No roster yet → neutral gray fallback.
        Assert.False(world.TryGetSideColor(0, out _, out _, out _));
        world.Apply(new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(0, CivId: 1, PlayerCount: 2, "Alpha", 188f / 255f, 160f / 255f, 0f, 1f),
                new SideRoster.SideRecord(1, CivId: 3, PlayerCount: 1, "Bravo", 0f, 138f / 255f, 217f / 255f, 1f),
            ],
        });
        Assert.True(world.TryGetSideColor(0, out var r0, out var g0, out _));
        Assert.Equal(188f / 255f, r0, 6);
        Assert.Equal(160f / 255f, g0, 6);
        Assert.True(world.TryGetSideColor(1, out _, out var g1, out var b1));
        Assert.Equal(138f / 255f, g1, 6);
        Assert.Equal(217f / 255f, b1, 6);
        // A side not in the roster (e.g. an absent 6th side) → gray fallback.
        Assert.False(world.TryGetSideColor(5, out _, out _, out _));
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

    /// <summary>P262/P481 — FM_S_GAME_OVER(62) retains the full end-of-mission
    /// snapshot (reason + winner + per-side records) as <see cref="ClientWorld.GameOverResult"/>.
    /// P481 — it no longer flips the mission stage to Over: the stage is driven
    /// only by the real MISSION_STAGE broadcast (a full game-over sends
    /// <c>MISSION_STAGE Over</c> before the P439 rotation; the P455b side-only
    /// game-over does not). This is what lets the game-over Back button mirror
    /// native <c>OnButtonBack</c>'s stage branch.</summary>
    [Fact]
    public void GameOver_RetainsSnapshot_AndKeepsStageUntouched()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel((short)1,
                new Ship(1, TestHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u),
        });

        // Establish a Started stage so we can prove GAME_OVER does not touch it.
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });

        var sides = new SideEndgameInfo[GameOver.MaxSides];
        for (int i = 0; i < GameOver.MaxSides; i++)
            sides[i] = new SideEndgameInfo("<bug>", -1, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);
        sides[0] = new SideEndgameInfo("Alpha", 1, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);
        sides[1] = new SideEndgameInfo("Bravo", 3, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);

        world.Apply(new GameOver
        {
            Sides = sides,
            GameoverMessage = "Alpha won by outlasting all other sides.",
            SideWinner = 0,
            NumSides = 2,
            EjectPods = false,
        });

        Assert.NotNull(world.GameOverResult);
        Assert.Equal("Alpha won by outlasting all other sides.", world.GameOverResult!.GameoverMessage);
        Assert.Equal((short)0, world.GameOverResult.SideWinner);
        Assert.Equal("Alpha", world.GameOverResult.Sides[0].SideName);
        Assert.Equal("Bravo", world.GameOverResult.Sides[1].SideName);
        // P481 — GAME_OVER alone does not flip the stage; it stays Started until
        // the server's real MISSION_STAGE Over broadcast (full game-over) arrives.
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission!.Stage);
    }

    /// <summary>P284 — CREATE_BUCKETS(191) seeds the joining side's development-
    /// tech mask (it has no SideId → applies to MySideId), and SIDE_TECH_CHANGE(75)
    /// updates that side's mask on research. This is the wire state the docked-shop
    /// buy gate reads: the mask flips a previously-hidden weapon/hull to buyable.</summary>
    [Fact]
    public void DevelopmentTechMask_SeededByCreateBuckets_AndUpdatedBySideTechChange()
    {
        var world = new ClientWorld();

        // MySideId must be known first — CREATE_BUCKETS maps to "the joining
        // side" via the own-ship EXPORT (ShipId = NA(-1)).
        world.Apply(new ExportShip { ShipId = -1, SideId = 5 });
        Assert.Equal((short)5, world.MySideId);

        // Seed: CREATE_BUCKETS carries the joining side's mask (say bits {0, 9}).
        var seed = new byte[TechTreeBitMask.ByteCount];
        seed[0] = 0b0000_0001;   // bit 0
        seed[1] = 0b0000_0010;   // bit 9
        world.Apply(new CreateBuckets { TtbmDevelopments = seed, TtbmInitial = seed });
        Assert.Equal(TechTreeBitMask.FromBytes(seed), world.DevelopmentTechsFor(5));

        // A development completes → SIDE_TECH_CHANGE carries the post-OR mask
        // for that side (say it adds bit 114, unlocking weapon 190 'ER Nanite 2').
        var grew = new byte[TechTreeBitMask.ByteCount];
        grew[14] = 0b0100_0000;  // bit 114
        for (int i = 0; i < seed.Length; i++) grew[i] |= seed[i];
        world.Apply(new SideTechChange { SideId = 5, TtbmDevelopments = grew });
        Assert.Equal(TechTreeBitMask.FromBytes(grew), world.DevelopmentTechsFor(5));

        // A different side's change is keyed separately and doesn't disturb ours.
        var other = new byte[TechTreeBitMask.ByteCount];
        other[3] = 0b1000_0000;  // bit 31
        world.Apply(new SideTechChange { SideId = 9, TtbmDevelopments = other });
        Assert.Equal(TechTreeBitMask.FromBytes(other), world.DevelopmentTechsFor(9));
        Assert.Equal(TechTreeBitMask.FromBytes(grew), world.DevelopmentTechsFor(5));

        // Unknown side → null (the shop's "tech unknown" → show-all degradation).
        Assert.Null(world.DevelopmentTechsFor(123));
    }
}
