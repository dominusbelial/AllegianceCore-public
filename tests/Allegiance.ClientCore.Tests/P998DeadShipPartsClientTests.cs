using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P998 — the CLIENT half of "a dying ship's parts die with it". The native
/// client never learns a dead ship's empty loadout from a server loadout message:
/// its own <c>FM_S_EJECT</c> handler terminates every part itself
/// (<c>clintlib/appmsg.cpp:193-201</c> — "Ship 'died' … while (plink = plist-&gt;first())
/// plink-&gt;data()-&gt;Terminate(); … SetAmmo(0); SetFuel(0.0f);") and its
/// <c>FM_S_ENTER_LIFEPOD</c> handler does the same before the pod hull swap
/// (<c>appmsg.cpp:1973-1981</c>). This port's client is a renderer fed by the wire,
/// so those two handlers are where the pod's parts are dropped — the missing
/// ENTER_LIFEPOD half is exactly what left the dead fighter's guns listed in the
/// hangar's CURRENT SHIP pane (the P996 finding).
///
/// <para>Also pinned here: the native <c>m_loadouts</c> head (P996's
/// <see cref="ClientWorld.OwnSavedLoadout"/>) must NOT be overwritten by a
/// hull-only refresh — native's own client ignores its own <c>LOADOUT_CHANGE</c>
/// part list (<c>appmsg.cpp:2028</c>) and only re-saves on a purchase ACK
/// (<c>appmsg.cpp:3185</c>), so the strip-triggered refresh that follows a death
/// can never cost the player the ship the hangar will restore
/// (<c>clintlib.h:1876-1886</c>).</para>
/// </summary>
public class P998DeadShipPartsClientTests
{
    private const short Lifepod = 744;      // the rig's Iron Coalition pod hull
    private const short RealHull = 501;
    private static readonly ExpandedPartData Gun = new(PartId: 7, Amount: 40, MountId: 0, FractionMounted: 255);
    private static readonly ExpandedPartData Shield = new(PartId: 9, Amount: 1, MountId: 1, FractionMounted: 255);
    private static readonly ExpandedPartData AmmoPack = new(PartId: 24, Amount: 3, MountId: 0xFF, FractionMounted: 255);

    private static DataHullTypeIGC TestHull(short hullId) => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: hullId, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static (ClientWorld world, short me) Seated()
    {
        var world = new ClientWorld { LifepodHullResolver = _ => Lifepod };
        const short me = 42;
        var spawn = new Ship(me, TestHull(RealHull), new Vector3(1000f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        world.Apply(new SetCluster { SectorId = 7, Ship = ServerShipUpdate.FromModel(me, spawn, 99u), Cookie = 0x2A });
        world.Apply(new ShipStatusMessage
        {
            ShipId = me, HullId = RealHull, SectorId = 7, StationId = -1,
            ParentId = -1, State = ShipStatusMessage.StateFlying,
        });
        world.Apply(new BuyLoadoutAck { HullId = RealHull, Parts = [Gun, Shield, AmmoPack], FBoughtEverything = true, FLaunch = true });
        return (world, me);
    }

    /// <summary>⭐ Native's ENTER_LIFEPOD handler terminates every part before the
    /// hull swap (<c>appmsg.cpp:1973-1981</c>) — the half the port was missing, and
    /// the reason the docked pod's CURRENT SHIP pane listed the dead ship's parts.</summary>
    [Fact]
    public void Entering_a_lifepod_clears_the_dead_ships_parts_and_cargo()
    {
        var (world, me) = Seated();
        Assert.Equal(2, world.Ships[me].Loadout!.Count);   // gun + shield (cargo split out)
        Assert.Single(world.Ships[me].Cargo!);

        world.Apply(EnterLifepod.ForPlayer(me));

        Assert.Equal(Lifepod, world.Ships[me].HullId);
        Assert.Null(world.Ships[me].Loadout);
        Assert.Null(world.Ships[me].Cargo);
        // …and the P996 saved loadout (native m_loadouts head) is untouched, so the
        // hangar can still restore the ship the player actually flew.
        Assert.NotNull(world.OwnSavedLoadout);
        Assert.Equal(RealHull, world.OwnSavedLoadout!.HullId);
        Assert.Equal(2, world.OwnSavedLoadout.Parts.Count);
    }

    /// <summary>Native's EJECT handler terminates the parts and zeroes ammo/fuel
    /// (<c>appmsg.cpp:193-201</c>); cargo parts are parts (<c>shipIGC.cpp:1516-1549</c>).</summary>
    [Fact]
    public void Ejecting_clears_the_dead_ships_parts_and_cargo()
    {
        var (world, me) = Seated();

        world.Apply(Allegiance.Core.Wire.Eject.Decode(new Allegiance.Core.Wire.Eject
        {
            ShipId = me, Position = new Vector3(1f, 2f, 3f), Velocity = Vector3.Zero,
            Forward = new Vector3(0f, 0f, -1f), Cookie = 0x31,
        }.Encode()));

        Assert.Equal(Lifepod, world.Ships[me].HullId);
        Assert.Null(world.Ships[me].Loadout);
        Assert.Null(world.Ships[me].Cargo);
        Assert.NotNull(world.OwnSavedLoadout);
        Assert.Equal(RealHull, world.OwnSavedLoadout!.HullId);
    }

    /// <summary>
    /// ⭐ The strip's own-ship <c>LOADOUT_CHANGE</c> (a hull-only refresh, e.g. the
    /// server's whole-loadout publish around a death) must not become "the last
    /// loadout": native's client never takes its parts from its own
    /// <c>LOADOUT_CHANGE</c> (<c>appmsg.cpp:2028</c>) and re-saves only on a
    /// purchase ACK (<c>appmsg.cpp:3185</c>), so the cached entry keeps the parts
    /// the hangar will restore (<c>clintlib.h:1876-1886</c>).
    /// </summary>
    [Fact]
    public void A_hull_only_own_loadout_change_does_not_clobber_the_saved_loadout()
    {
        var (world, me) = Seated();
        Assert.NotNull(world.OwnSavedLoadout);

        // The dead hull with every part terminated (the pre-hull-swap snapshot).
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = me, HullId = RealHull, Parts = [], Passengers = [],
        });

        var saved = Assert.IsType<ClientWorld.SavedLoadout>(world.OwnSavedLoadout);
        Assert.Equal(RealHull, saved.HullId);
        Assert.Equal(2, saved.Parts.Count);   // still the gun + shield the player bought
        Assert.Empty(world.Ships[me].Loadout!);   // …while the live ship really is bare
    }

    /// <summary>The pod-hull refresh (a normal death hull swap) never overwrites it
    /// either — the P996 rule, kept.</summary>
    [Fact]
    public void A_pod_hull_loadout_change_does_not_clobber_the_saved_loadout()
    {
        var (world, me) = Seated();
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = me, HullId = Lifepod, Parts = [], Passengers = [],
        });

        Assert.Equal(RealHull, world.OwnSavedLoadout!.HullId);
        Assert.Equal(2, world.OwnSavedLoadout.Parts.Count);
        Assert.Equal(Lifepod, world.Ships[me].HullId);
        Assert.Empty(world.Ships[me].Loadout!);
    }
}
