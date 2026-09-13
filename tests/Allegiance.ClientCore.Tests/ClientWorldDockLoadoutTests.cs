using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the new docking/loadout
/// surface on <see cref="ClientWorld"/> — the <c>FM_S_DOCKED(61)</c> notify and
/// the <c>FM_S_BUY_LOADOUT_ACK(5)</c> grant. Messages are constructed directly
/// (construct-then-round-trip style, like the sibling tests) and fed through
/// <see cref="ClientWorld.Apply"/>, asserting the right <see cref="ShipEntity"/>
/// fields update. Both messages carry no ship id, and each is routed by the
/// server only to the requesting peer — so the target ship is
/// <see cref="ClientWorld.MyShipId"/>, which these tests exercise via an
/// established SET_CLUSTER assignment first.
/// </summary>
public class ClientWorldDockLoadoutTests
{
    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 801, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 818f, HardpointOffset: 640,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>A world where <c>MyShipId</c> is already assigned (as the live
    /// server always does before docking/loadout traffic can arrive), so the
    /// target of the ship-less DockedNotify/BuyLoadoutAck is determinable.</summary>
    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 900,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                880u),
            Cookie = 0x80,
        });
        return world;
    }

    private const short MyId = 812;

    // ── DockedNotify(61) ────────────────────────────────────────────────

    [Fact]
    public void DockedNotify_SetsStationId_OnMyShip()
    {
        var world = WorldWithMyShip(MyId);

        world.Apply(new DockedNotify { StationId = 913 });

        Assert.Single(world.Ships);                       // no phantom ship created
        Assert.Equal(MyId, world.MyShipId);
        Assert.Equal((short)913, world.Ships[MyId].StationId);
    }

    [Fact]
    public void DockedNotify_ClearsStationId_OnUndock()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new DockedNotify { StationId = 914 });
        Assert.Equal((short)914, world.Ships[MyId].StationId);

        world.Apply(new DockedNotify { StationId = -1 });   // NA = flying free

        Assert.Equal(-1, world.Ships[MyId].StationId);
        Assert.Single(world.Ships);                         // same entity, not a fresh one
    }

    [Fact]
    public void DockedNotify_BeforeMyShipId_IsANoOp()
    {
        var world = new ClientWorld();   // no SET_CLUSTER yet → MyShipId null

        world.Apply(new DockedNotify { StationId = 915 });

        Assert.Empty(world.Ships);       // nothing docked, no phantom ship
        Assert.Null(world.MyShipId);
    }

    // ── BuyLoadoutAck(5) ────────────────────────────────────────────────

    [Fact]
    public void BuyLoadoutAck_SetsHullAndLoadout_OnMyShip()
    {
        var world = WorldWithMyShip(MyId);
        Assert.Null(world.Ships[MyId].Loadout);   // baseline: no ack seen yet

        var parts = new[]
        {
            new ExpandedPartData(PartId: 532, Amount: 534, MountId: (byte)2, FractionMounted: ExpandedPartData.FullyMounted),
            new ExpandedPartData(PartId: 537, Amount: 539, MountId: (byte)4, FractionMounted: (byte)0x7A),
        };

        world.Apply(new BuyLoadoutAck
        {
            HullId = 802,
            FBoughtEverything = true,
            FLaunch = false,
            Parts = parts,
        });

        Assert.Single(world.Ships);
        Assert.Equal((short)802, world.Ships[MyId].HullId);   // hull adopted
        var loadout = world.Ships[MyId].Loadout!;             // established below
        Assert.NotNull(loadout);
        Assert.Equal(2, loadout.Count);                       // part tail kept verbatim
        Assert.Equal((short)532, loadout[0].PartId);
        Assert.Equal((short)537, loadout[1].PartId);
        Assert.Equal(ExpandedPartData.FullyMounted, loadout[0].FractionMounted);
        Assert.Equal((short)4, (short)loadout[1].MountId);
    }

    [Fact]
    public void BuyLoadoutAck_EmptyParts_MeansHullOnlyLoadout()
    {
        var world = WorldWithMyShip(MyId);

        world.Apply(new BuyLoadoutAck { HullId = 803, FBoughtEverything = false, FLaunch = false, Parts = [] });

        Assert.Equal((short)803, world.Ships[MyId].HullId);
        Assert.NotNull(world.Ships[MyId].Loadout);    // present but empty (hull-only, the P26 shape)
        Assert.Empty(world.Ships[MyId].Loadout!);
    }

    [Fact]
    public void BuyLoadoutAck_BeforeMyShipId_IsANoOp()
    {
        var world = new ClientWorld();

        world.Apply(new BuyLoadoutAck { HullId = 804, FBoughtEverything = true, FLaunch = true, Parts = [] });

        Assert.Empty(world.Ships);
        Assert.Null(world.MyShipId);
    }

    // ── collision-free upserting: ack+docked notify on the same ship ─────

    [Fact]
    public void DockedNotify_And_BuyLoadoutAck_UpsertTheSameShipEntity()
    {
        var world = WorldWithMyShip(MyId);
        var before = world.Ships[MyId];

        world.Apply(new DockedNotify { StationId = 916 });
        world.Apply(new BuyLoadoutAck { HullId = 805, FBoughtEverything = false, FLaunch = true, Parts = [] });

        Assert.Single(world.Ships);
        Assert.Same(before, world.Ships[MyId]);       // identity preserved, no replacement
        Assert.Equal((short)916, world.Ships[MyId].StationId);
        Assert.Equal((short)805, world.Ships[MyId].HullId);
        Assert.NotNull(world.Ships[MyId].Loadout);
    }
}
