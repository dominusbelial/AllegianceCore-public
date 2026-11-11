using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P996 — the client half of the lifepod respawn loop: the WORLD must remember
/// the player's last real loadout across the eject → pod death → docked-pod
/// cycle, so the hangar can restore it the way native does (the
/// <c>appmsg.cpp:2022-2023</c> gate —
/// <c>pod hull &amp;&amp; GetStation()</c> → <c>RestoreLoadout(station)</c>,
/// <c>clintlib.h:1876-1886</c>) instead of offering/launching the pod.
/// <para>Native's source of truth is the cached loadout list
/// (<c>m_loadouts</c>, <c>clintlib.h:821</c>), saved on every
/// <c>FM_S_BUY_LOADOUT_ACK</c> (<c>appmsg.cpp:3185</c> <c>SaveLoadout()</c>). The
/// port records the same snapshot on the own ship's purchase ACK / LOADOUT_CHANGE
/// / real-id EXPORT and never lets a lifepod hull overwrite it.</para>
/// </summary>
public class P996LifepodSavedLoadoutTests
{
    private const short Lifepod = 744;      // side's pod hull (the rig's Iron Coalition pod)
    private const short RealHull = 501;
    private static readonly ExpandedPartData Gun = new(PartId: 7, Amount: 40, MountId: 0, FractionMounted: 255);
    private static readonly ExpandedPartData Shield = new(PartId: 9, Amount: 1, MountId: 0, FractionMounted: 255);

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

    private static Ship MakeShip(short id) =>
        new(id, TestHull(RealHull), new Vector3(1000f, 0f, 0f), Vector3.Zero, Orientation.Identity);

    /// <summary>A world whose own ship (id 42) is seated, identified and DOCKED.</summary>
    private static (ClientWorld world, short me) Seated() => Seated(new ClientWorld());

    private static (ClientWorld world, short me) Seated(ClientWorld world)
    {
        world.LifepodHullResolver = _ => Lifepod;
        const short me = 42;
        var spawn = MakeShip(me);
        world.Apply(new SetCluster { SectorId = 7, Ship = ServerShipUpdate.FromModel(me, spawn, 99u), Cookie = 0x2A });
        world.Apply(new ShipStatusMessage
        {
            ShipId = me, HullId = RealHull, SectorId = 7, StationId = 1234,
            ParentId = -1, State = ShipStatusMessage.StateDocked,
        });
        return (world, me);
    }

    private static void BuyRealLoadout(ClientWorld world) =>
        world.Apply(new BuyLoadoutAck { HullId = RealHull, Parts = [Gun, Shield], FBoughtEverything = true, FLaunch = false });

    private static void SendEject(ClientWorld world, short me) =>
        world.Apply(Allegiance.Core.Wire.Eject.Decode(new Allegiance.Core.Wire.Eject
        {
            ShipId = me, Position = new Vector3(1f, 2f, 3f), Velocity = Vector3.Zero,
            Forward = new Vector3(0f, 0f, -1f), Cookie = 0x31,
        }.Encode()));

    private static void LoadoutChange(ClientWorld world, short hullId, params ExpandedPartData[] parts) =>
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = world.MyShipId ?? -1, HullId = hullId, Parts = parts, Passengers = [],
        });

    /// <summary>⭐ The purchase ACK records the real loadout — native
    /// <c>SaveLoadout()</c> in the <c>FM_S_BUY_LOADOUT_ACK</c> handler
    /// (<c>appmsg.cpp:3185</c>).</summary>
    [Fact]
    public void Purchase_ack_records_the_own_loadout()
    {
        var (world, _) = Seated();
        Assert.Null(world.OwnSavedLoadout);

        BuyRealLoadout(world);

        var saved = Assert.IsType<ClientWorld.SavedLoadout>(world.OwnSavedLoadout);
        Assert.Equal(RealHull, saved.HullId);
        Assert.Equal([Gun, Shield], saved.Parts);
    }

    /// <summary>⭐⭐ The rig's cycle: real ship → eject (pod hull, parts cleared) →
    /// LOADOUT_CHANGE(pod) → docked in the pod. The saved real loadout survives and
    /// the world reports "docked in a lifepod", which is exactly native's
    /// <c>appmsg.cpp:2022-2023</c> RestoreLoadout trigger.</summary>
    [Fact]
    public void Pod_cycle_keeps_the_last_real_loadout_and_reports_docked_lifepod()
    {
        var (world, me) = Seated();
        BuyRealLoadout(world);

        SendEject(world, me);                       // FM_S_EJECT(77): hull → pod, parts cleared
        Assert.Equal(Lifepod, world.Ships[me].HullId);
        Assert.Null(world.Ships[me].Loadout);
        LoadoutChange(world, Lifepod);          // the pod's own LOADOUT_CHANGE(96)

        var saved = Assert.IsType<ClientWorld.SavedLoadout>(world.OwnSavedLoadout);
        Assert.Equal(RealHull, saved.HullId);   // a pod hull NEVER overwrites the saved ship
        Assert.Equal([Gun, Shield], saved.Parts);

        // The pod respawn docks the player (FedSrv.CPP:12064-12065).
        world.Apply(new ShipStatusMessage
        {
            ShipId = me, HullId = Lifepod, SectorId = 7, StationId = 4321,
            ParentId = -1, State = ShipStatusMessage.StateDocked,
        });
        Assert.True(world.OwnShipDockedInLifepod, "berthed pod must be recognised (native pod hull && GetStation())");
    }

    /// <summary>A pod-hull ACK (a client that buys the pod, the P996 loop) must not
    /// become "the last loadout" either.</summary>
    [Fact]
    public void Lifepod_purchase_ack_does_not_overwrite_the_saved_loadout()
    {
        var (world, _) = Seated();
        BuyRealLoadout(world);
        world.Apply(new BuyLoadoutAck { HullId = Lifepod, Parts = [], FBoughtEverything = false, FLaunch = true });

        var saved = Assert.IsType<ClientWorld.SavedLoadout>(world.OwnSavedLoadout);
        Assert.Equal(RealHull, saved.HullId);
        Assert.Equal([Gun, Shield], saved.Parts);
    }

    /// <summary>The relaunch LOADOUT_CHANGE to a real hull refreshes the snapshot
    /// (native re-saves on the next purchase; the port mirrors the newest real
    /// loadout), so a second death restores the newer ship.</summary>
    [Fact]
    public void Relaunch_loadout_change_refreshes_the_saved_loadout()
    {
        var (world, me) = Seated();
        BuyRealLoadout(world);
        SendEject(world, me);
        LoadoutChange(world, Lifepod);

        LoadoutChange(world, hullId: 517, Gun);   // a different real hull after the relaunch

        var saved = Assert.IsType<ClientWorld.SavedLoadout>(world.OwnSavedLoadout);
        Assert.Equal((short)517, saved.HullId);
        Assert.Single(saved.Parts);
        Assert.Equal(Gun, saved.Parts[0]);
    }

    /// <summary>A player who has never flown a real ship has no saved loadout, so
    /// the hangar takes native's free-default fallback
    /// (<c>clintlib.h:1850-1868</c>) instead of restoring nothing.</summary>
    [Fact]
    public void No_saved_loadout_when_nothing_real_was_ever_owned()
    {
        var (world, me) = Seated();
        SendEject(world, me);
        LoadoutChange(world, Lifepod);
        world.Apply(new ShipStatusMessage
        {
            ShipId = me, HullId = Lifepod, SectorId = 7, StationId = 4321,
            ParentId = -1, State = ShipStatusMessage.StateDocked,
        });

        Assert.Null(world.OwnSavedLoadout);
        Assert.True(world.OwnShipDockedInLifepod);
    }

    /// <summary>A berthed real ship is never "docked in a lifepod" (the gate needs
    /// the pod hull, not just the berth).</summary>
    [Fact]
    public void Docked_real_ship_is_not_a_docked_lifepod()
    {
        var (world, _) = Seated();
        BuyRealLoadout(world);
        Assert.False(world.OwnShipDockedInLifepod);
    }
}
