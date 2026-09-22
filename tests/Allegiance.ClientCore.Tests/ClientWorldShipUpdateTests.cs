using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P347 — client-side decode/merge of the periodic cross-side visibility sync
/// (<c>FM_S_HEAVY_SHIPS_UPDATE</c> 176 / <c>FM_S_LIGHT_SHIPS_UPDATE</c> 175).
/// A foreign faction's ship — whose identity (<c>HullId</c>/<c>SideId</c>/name)
/// arrives via the cross-side EXPORT broadcast (P346) — must have its <c>Latest</c>
/// pose/tick refreshed by the heavy update so the render/target path sees a moving
/// cross-side hull; identity fields must be left untouched by the update stream.
/// Pure-domain: messages are built, round-tripped through the real codec
/// (Encode→Decode), and fed through <see cref="ClientWorld.Apply"/>.
/// </summary>
public class ClientWorldShipUpdateTests
{
    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Orientation Yawed(float theta)
    {
        var o = Orientation.Identity;
        o.Yaw(theta);
        return o;
    }

    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    /// <summary>Feed a foreign (cross-side) ship's EXPORT so identity is known —
    /// the P346 cross-side identity half — then a heavy update refreshes its pose.</summary>
    [Fact]
    public void HeavyShipsUpdate_RefreshesForeignShipPose_KeepsIdentity()
    {
        var world = WorldWithMyShip(716);
        const short foreign = 881;
        const short foreignHull = 811;
        const short foreignSide = 904;

        // P346 identity: a foreign faction's ship EXPORTs to us with its real
        // HullId/SideId/name (no same-side gate).
        world.Apply(new ExportShip
        {
            ShipId = foreign,
            HullId = foreignHull,
            SideId = foreignSide,
            CharacterName = "grendel",
            PilotType = ExportShip.PilotTypePlayer,
        });
        Assert.Equal(foreignHull, world.Ships[foreign].HullId);
        Assert.Equal(foreignSide, world.Ships[foreign].SideId);

        // Periodic heavy sync: the foreign ship is in our cluster now, moving.
        var mover = new Ship(foreign, MakeHull(),
            new Vector3(100f, 0f, 871f), new Vector3(0f, 0f, 5f), Yawed(0.005f));
        const uint timeRef = 951;
        var posRef = new Vector3(0f, 0f, 801f);
        var heavy = HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(timeRef, posRef, new[] { mover }).Encode());

        world.Apply(heavy);

        var ent = world.Ships[foreign];
        Assert.NotNull(ent.Latest);
        Assert.Equal(foreignHull, ent.HullId);      // identity untouched by the sync
        Assert.Equal(foreignSide, ent.SideId);
        Assert.Equal("grendel", ent.CharacterName);
        // Pose from the heavy record (delta-recovered vs posRef), matching the
        // original ship position modulo compact-position quantization.
        Assert.InRange(ent.Latest!.Position.X, 70f, 130f);
        Assert.InRange(ent.Latest.Position.Z, 842f, 898f);
        Assert.Equal(timeRef, ent.LastUpdateTick);
    }

    /// <summary>A heavy update naming a ship we've never heard of still upserts a
    /// pose entity — but with NO identity (no HullId/SideId), since that can only
    /// come from EXPORT. It is therefore render-skipped (the render/target loops
    /// resolve a model via HullId, which stays 0).</summary>
    [Fact]
    public void HeavyShipsUpdate_WithoutPriorExport_GivesPoseButNoIdentity()
    {
        var world = WorldWithMyShip(706);
        const short unseen = 813;
        var mover = new Ship(unseen, MakeHull(),
            new Vector3(120f, 40f, 641f), Vector3.Zero, Orientation.Identity);

        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(700, new Vector3(0f, 0f, 661f), new[] { mover }).Encode()));

        Assert.True(world.Ships.ContainsKey(unseen));
        Assert.NotNull(world.Ships[unseen].Latest);
        Assert.Equal((short)0, world.Ships[unseen].HullId);   // no EXPORT → no identity
        Assert.Equal((short)0, world.Ships[unseen].SideId);
    }

    /// <summary>The cheap sync half: a light record carries only id/stateM/controls,
    /// so it establishes entity presence without disturbing any pose.</summary>
    [Fact]
    public void LightShipsUpdate_UpsertsEntity_LeavesPoseUntouched()
    {
        var world = WorldWithMyShip(711);
        const short foreign = 896;
        world.Apply(new ExportShip { ShipId = foreign, HullId = 817, SideId = 911 });

        var flyer = new Ship(foreign, MakeHull(),
            new Vector3(50f, 60f, 991f), Vector3.Zero, Orientation.Identity);
        // Establish pose via a heavy sync first.
        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(300, new Vector3(0f, 0f, 981f), new[] { flyer }).Encode()));
        var poseBefore = world.Ships[foreign].Latest!;

        // Now a light-only tick for that same ship arrives.
        world.Apply(LightShipsUpdate.Decode(
            LightShipsUpdate.FromShips(new[] { flyer }).Encode()));

        // Entity still present, pose unchanged (light has no kinematics).
        Assert.True(world.Ships.ContainsKey(foreign));
        Assert.Same(poseBefore, world.Ships[foreign].Latest);
    }

    /// <summary>Both multi-ship update messages are consumed in full — every carried
    /// record upserts, not just the first.</summary>
    [Fact]
    public void HeavyShipsUpdate_MultipleRecords_AllEnterTable()
    {
        var world = WorldWithMyShip(704);
        var s1 = new Ship(901, MakeHull(), new Vector3(10f, 0f, 821f), Vector3.Zero, Orientation.Identity);
        var s2 = new Ship(913, MakeHull(), new Vector3(20f, 0f, 831f), Vector3.Zero, Orientation.Identity);
        var s3 = new Ship(915, MakeHull(), new Vector3(30f, 0f, 841f), Vector3.Zero, Orientation.Identity);

        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(100, Vector3.Zero, new[] { s1, s2, s3 }).Encode()));

        Assert.Equal((short)901, world.Ships[901].Latest!.ShipId);
        Assert.Equal((short)913, world.Ships[913].Latest!.ShipId);
        Assert.Equal((short)915, world.Ships[915].Latest!.ShipId);
    }
}
