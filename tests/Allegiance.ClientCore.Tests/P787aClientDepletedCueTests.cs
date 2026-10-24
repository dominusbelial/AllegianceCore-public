using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P787a — the own-ship launcher DEPLETED (reload-failure) cues on the
/// pure-renderer client. Native plays salMissilesDepletedSound /
/// salChaffDepletedSound / salDispenserEmptySound at the fire-time reload
/// failure (clintlib.cpp:3223-3226,3407-3421); this port's server performs the
/// launcher reload itself and, on failure, drops the empty launcher and relays
/// DROP_PART(33) (clintlib.cpp:3685-3695). The client infers the depletion edge
/// from what it observes: its own ship's launcher arriving at an empty state
/// via that DROP_PART relay.
/// </summary>
public class P787aClientDepletedCueTests
{
    private const short MyId = 982;
    private const short LauncherPartId = 957;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 983, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 998f, HardpointOffset: 987,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static ClientWorld WorldWithMyShip(short launcherAmount, EquipmentType et, DataPartTypeIGC.Subtype subtype)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 991,
            Ship = ServerShipUpdate.FromModel(
                MyId,
                new Ship(MyId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                992u),
            Cookie = 0x80,
        });
        world.PartTypeResolver = pid => pid == LauncherPartId
            ? new DataPartTypeIGC(
                Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "launcher", Description: "",
                GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
                PartId: LauncherPartId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
                Type: subtype)
            : null;
        // Seat a mounted launcher at mount 0 in the own ship's loadout.
        world.Apply(new BuyLoadoutAck
        {
            HullId = 993,
            FBoughtEverything = true,
            FLaunch = false,
            Parts = [new ExpandedPartData(PartId: LauncherPartId, Amount: launcherAmount, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted)],
        });
        return world;
    }

    [Fact]
    public void OwnEmptyMissileLauncherDrop_RecordsMissilesDepletedCue()
    {
        var world = WorldWithMyShip(launcherAmount: 0, EquipmentType.Magazine, new DataPartTypeIGC.MissileLauncher(11));
        world.Apply(new DropPart { ShipID = MyId, Et = EquipmentType.Magazine, Mount = 0 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.SalMissilesDepleted, ev.Kind);
        Assert.Equal(MyId, ev.Id);
    }

    [Fact]
    public void OwnEmptyChaffLauncherDrop_RecordsChaffDepletedCue()
    {
        var world = WorldWithMyShip(launcherAmount: 0, EquipmentType.ChaffLauncher, new DataPartTypeIGC.ChaffLauncher(21));
        world.Apply(new DropPart { ShipID = MyId, Et = EquipmentType.ChaffLauncher, Mount = 0 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.SalChaffDepleted, ev.Kind);
    }

    [Fact]
    public void OwnEmptyDispenserDrop_RecordsDispenserEmptyCue()
    {
        var world = WorldWithMyShip(launcherAmount: 0, EquipmentType.Dispenser, new DataPartTypeIGC.MineLauncher(31));
        world.Apply(new DropPart { ShipID = MyId, Et = EquipmentType.Dispenser, Mount = 0 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.SalDispenserEmpty, ev.Kind);
    }

    [Fact]
    public void OwnLoadedLauncherDrop_DoesNotCueDepleted()
    {
        // A MANUAL drop of a launcher that still has rounds (Amount > 0) must
        // NOT play the depleted cue — only the reload-failure (empty) edge does.
        var world = WorldWithMyShip(launcherAmount: 5, EquipmentType.Magazine, new DataPartTypeIGC.MissileLauncher(11));
        world.Apply(new DropPart { ShipID = MyId, Et = EquipmentType.Magazine, Mount = 0 });

        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void RemoteEmptyLauncherDrop_DoesNotCueDepleted()
    {
        // Another ship's launcher dropping (even empty) must not cue the LOCAL
        // player — native gates all three to pShip == m_ship.
        var world = WorldWithMyShip(launcherAmount: 0, EquipmentType.Magazine, new DataPartTypeIGC.MissileLauncher(11));
        world.Apply(new DropPart { ShipID = 41, Et = EquipmentType.Magazine, Mount = 0 });

        Assert.Empty(world.TakeSoundEvents());
    }
}
