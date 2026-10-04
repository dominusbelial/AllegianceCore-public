using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P540 — the client-world half of <c>FM_S_EJECT(77)</c> (native
/// appmsg.cpp:170-242): decoding and applying the message swaps the hull to the
/// side's lifepod, clears the ship's parts, takes the pose and roll spin from
/// the message, and — for our OWN ship — adopts the cookie and latches
/// <see cref="ShipEntity.HasPendingEject"/> for the MonoGame host. This is the
/// pure-world layer, tested without a server or the Game1 autopilot (whose
/// target pick + engage live in the MonoGame tests and the live proof).
/// </summary>
public class ClientWorldEjectTests
{
    private const short LifepodHull = 123;   // the resolver's answer, whatever the side

    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 500, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    /// <summary>A world whose own ship (id 42, side 5) is seated and flying
    /// free (not boarded) with a bought loadout.</summary>
    private static (ClientWorld world, short me) SeatedOwnShip()
    {
        var world = new ClientWorld();
        world.LifepodHullResolver = _ => LifepodHull;   // "the side's lifepod"

        const short me = 42;
        var spawn = MakeShip(me, new Vector3(1000f, -2000f, 3000f));
        world.Apply(new SetCluster
        {
            SectorId = 7,
            Ship = ServerShipUpdate.FromModel(spawn.Id, spawn, 99u),
            Cookie = 0x2A,
        });
        // Give it an identity (side + hull + a bought loadout) so the eject's
        // part-clearing and hull swap have real pre-state.
        world.Apply(new ShipStatusMessage
        {
            ShipId = me, HullId = 501, SectorId = 7, StationId = -1,
            ParentId = -1, State = ShipStatusMessage.StateFlying,
        });
        world.Apply(new BuyLoadoutAck
        {
            HullId = 501,
            Parts = new System.Collections.Generic.List<ExpandedPartData>
            {
                new(PartId: 1, Amount: 1, MountId: 0, FractionMounted: 255),
            },
        });
        // world.Apply(new JoinSide { ... }) would set SideId; the resolver here
        // ignores it, so the side is exercised indirectly through the resolver.
        return (world, me);
    }

    private static Eject OwnEject(short me) => new()
    {
        ShipId = me,
        Position = new Vector3(400f, 500f, 600f),
        Velocity = new Vector3(-1f, 2f, -3f),
        Forward = new Vector3(0f, 0f, -1f),
        Cookie = 0x77,
    };

    [Fact]
    public void DecodeAndApplyEject_SwapsHullClearsPartsAndSetsPoseAndSpin()
    {
        var (world, me) = SeatedOwnShip();
        Assert.Equal((short)501, world.Ships[me].HullId);
        Assert.NotNull(world.Ships[me].Loadout);          // parts present before eject

        var e = OwnEject(me);
        world.Apply(Eject.Decode(e.Encode()));            // decode then apply (the real path)

        var ship = world.Ships[me];
        // Hull swapped to the side's lifepod; parts cleared (terminated).
        Assert.Equal(LifepodHull, ship.HullId);
        Assert.Null(ship.Loadout);
        // Pose comes from the message. Position is stored raw-exact; velocity and
        // orientation go through the same compact quantizers every ServerShipUpdate
        // uses (CompactVelocity / CompactOrientation), so assert within their noise.
        Assert.NotNull(ship.Latest);
        Assert.Equal(e.Position, ship.Latest!.Position);
        // Velocity is compact-quantized (CompactVelocity, ≤~0.1 unit/component at
        // this magnitude); a uniform 0.15 tolerance covers the codec noise while
        // still distinguishing the stored velocity from a different/zero vector.
        Assert.Equal(e.Velocity.X, ship.Latest.Velocity.X, 0.15f);
        Assert.Equal(e.Velocity.Y, ship.Latest.Velocity.Y, 0.15f);
        Assert.Equal(e.Velocity.Z, ship.Latest.Velocity.Z, 0.15f);
        // Roll spin from the message: native SetCurrentTurnRate(c_axisRoll, pi*2)
        // → TurnRates[2] ≈ 2π (compact-quantized).
        Assert.Equal(2f * MathF.PI, ship.Latest.TurnRates[2], 0.003f);
        // Orientation forward comes from the message (quantized ~).
        var fwd = ship.Latest.Orientation.GetForward();
        Assert.Equal(e.Forward.X, fwd.X, 0.002f);
        Assert.Equal(e.Forward.Z, fwd.Z, 0.002f);
        // The pod is alive and present (server never sends KILL_SHIP for eject).
        Assert.False(ship.IsDead);
        Assert.False(ship.IsGone);
    }

    [Fact]
    public void OwnShipEject_AdoptsCookieAndLatchesPendingEject()
    {
        var (world, me) = SeatedOwnShip();
        Assert.False(world.Ships[me].HasPendingEject);
        Assert.Equal((byte)0x2A, world.Ships[me].Cookie);

        world.Apply(Eject.Decode(OwnEject(me).Encode()));

        Assert.Equal((byte)0x77, world.Ships[me].Cookie);
        Assert.True(world.Ships[me].HasPendingEject);
    }

    [Fact]
    public void RemoteShipEject_SwapsHullButDoesNotLatchAutopilot()
    {
        // A second, remote ship (not mine) ejects: hull/pose still update, but
        // HasPendingEject stays false (native only autopilots m_ship). Seeded
        // via SINGLE_SHIP_UPDATE so it is never assigned as the own ship.
        var world = new ClientWorld();
        world.LifepodHullResolver = _ => LifepodHull;
        const short remote = 99;
        world.Apply(new SingleShipUpdate
        {
            OidTarget = remote,
            Ship = ServerShipUpdate.FromModel(remote, MakeShip(remote, new Vector3(0f, 0f, 0f)), 1u),
        });

        world.Apply(Eject.Decode(new Eject
        {
            ShipId = remote,
            Position = new Vector3(1f, 2f, 3f),
            Velocity = new Vector3(0f, 0f, 0f),
            Forward = new Vector3(0f, 0f, -1f),
            Cookie = 0,
        }.Encode()));

        Assert.Equal(LifepodHull, world.Ships[remote].HullId);
        Assert.Equal(new Vector3(1f, 2f, 3f), world.Ships[remote].Latest!.Position);
        Assert.False(world.Ships[remote].HasPendingEject);
        Assert.Null(world.MyShipId);          // never assigned as mine
    }

    [Fact]
    public void BoardedShipEject_UnboardsInsteadOfClearingParts()
    {
        var (world, me) = SeatedOwnShip();
        // Boarded: parent set (the ejected crew is riding someone else's hull).
        world.Apply(new ShipStatusMessage { ShipId = me, HullId = 501, ParentId = 700, StationId = -1, State = ShipStatusMessage.StateFlying });
        Assert.Equal((short)700, world.Ships[me].ParentId);

        world.Apply(Eject.Decode(OwnEject(me).Encode()));

        // Unboarded (SetParentShip(NULL) + OnBoardShip(pShip, NULL)); parts kept.
        Assert.Equal((short)-1, world.Ships[me].ParentId);
        Assert.NotNull(world.Ships[me].Loadout);
    }
}
