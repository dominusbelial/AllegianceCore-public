using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>P754 — remaining callbacks parity: server-side generic callbacks
/// and the ripcord-time constant now have production callers and host hooks.</summary>
public sealed class P754CallbackTests
{
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));

    private static Mission Domain(StaticCore? core = null, IIgcSite? site = null)
    {
        core ??= Core();
        var map = MissionMap.FromLists(
            [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }],
            [], [], []);
        var m = Mission.Assemble(core, map, [new SideSpec(0, "A", core.Civilizations.Keys.First())], site ?? new NullIgcSite());
        m.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship Ship(Mission m, DataHullTypeIGC hull, short id = 1, short side = 0)
    {
        var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(ship, 0);
        return ship;
    }

    private static void RipTick(Ship ship, float dt) =>
        typeof(Sector).GetMethod("ResolveRipcordCountdowns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ship.Sector, [dt]);

    private static void RescuePass(Sector sector) =>
        typeof(Sector).GetMethod("ResolveRescueEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sector, null);

    private static void StationRescuePass(Sector sector) =>
        typeof(Sector).GetMethod("ResolveStationRescueEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sector, null);

    private static DataTreasureIGC TreasureData(TreasureCode code, short treasureId, short amount = 100) =>
        DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]) with
        {
            TreasureId = treasureId,
            TreasureCode = code,
            Amount = amount,
            Lifespan = 100000f,
            P0 = [0, 0, 0],
            V0 = [0, 0, 0]
        };

    private static DataHullTypeIGC Hull(StaticCore core, string name, short capabilities)
    {
        var h = core.HullTypes.Values.First();
        return h with
        {
            Name = name,
            HabmCapabilities = capabilities,
            Mass = 100, Thrust = 1000, Speed = 100, MaxFuel = 1000,
            MaxEnergy = 100, RechargeRate = 0, HitPoints = 1000, Length = 10,
            RipcordSpeed = 1, MaxFixedWeapons = 0, MaxWeapons = 0, Hardpoints = []
        };
    }

    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "test", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0, Signature: 0, PartId: 32000,
        SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "", Type: subtype);

    /// <summary>SIM-0182 — <c>TerminateModelEvent</c> fires on real termination
    /// (<c>CmodelIGC::Terminate</c>, <c>Igc/modelIGC.cpp:55-61</c>) and NOT on a
    /// sector move, which only re-lists the model (<c>SetCluster</c>).</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void TerminateModelEvent_fires_on_termination_but_not_on_a_sector_move()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site: site);
        var ship = Ship(m, Hull(m.Core, "test", 0));

        // A sector move is exactly Remove-from-the-old-list + Add-to-the-new
        // (Server.Docking.MoveShipToSector, ripcord/teleport/respawn). Native
        // fires no event for it: the model is still alive, it only changed
        // clusters (SetCluster). This is the P754 misfire being fixed.
        var sector = m.Sectors[0];
        sector.Remove(ship);
        sector.Add(ship);
        Assert.DoesNotContain("TerminateModelEvent(Ship)", site.Calls);

        // Real termination destroys the model → the site hears it once.
        Assert.True(sector.Terminate(ship));
        Assert.Contains("TerminateModelEvent(Ship)", site.Calls);
        Assert.Single(site.Calls, c => c == "TerminateModelEvent(Ship)");

        // Idempotent: a second Terminate on an already-gone model raises nothing.
        Assert.False(sector.Terminate(ship));
        Assert.Single(site.Calls, c => c == "TerminateModelEvent(Ship)");
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void TerminateMissionEvent_reaches_site_when_invoked()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site: site);
        m.Site.TerminateMissionEvent(m);
        Assert.Contains("TerminateMissionEvent(Mission)", site.Calls);
    }

    /// <summary>SIM-0205 — the site's <c>LandOnCarrierEvent</c> is the native
    /// LANDING body (<c>FedSrv.CPP:12814-13031</c>), not a no-op: the production
    /// host routes it to the real relaunch and returns native's unconditional
    /// <c>true</c> (<c>FedSrv.CPP:13029</c>).</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void LandOnCarrierEvent_performs_the_native_landing_body_and_returns_true()
    {
        Require.Artwork();
        var host = new Allegiance.Server.ServerHost();
        var m = Domain(site: host);
        var carrier = Ship(m, Hull(m.Core, "carrier", DataHullTypeIGC.HabmCarrier));
        var lander = Ship(m, Hull(m.Core, "lander", DataHullTypeIGC.HabmLandOnCarrier), id: 2);

        var performed = new List<(Ship Carrier, Ship Lander)>();
        host.CarrierLanding = (c, l, _) => performed.Add((c, l));   // what Server.Lifecycle wires to Server.PerformRelaunch

        Assert.True(host.LandOnCarrierEvent(carrier, lander, 0.25f));
        var pair = Assert.Single(performed);
        Assert.Same(carrier, pair.Carrier);
        Assert.Same(lander, pair.Lander);
    }

    /// <summary>SIM-0206 — <c>RescueShipEvent</c>'s native return value is
    /// <c>pstation ? DockWithStationEvent(...) : false</c>
    /// (<c>FedSrv.CPP:13073-13075</c>): the rescue is only accepted when the
    /// rescuer's production friendly-station search finds a destination. The
    /// sector queues the rescue only on true.</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void RescueShipEvent_returns_the_native_station_destination_rule()
    {
        Require.Artwork();
        var host = new Allegiance.Server.ServerHost();
        var m = Domain(site: host);                      // the production host is this mission's site
        var pod = Ship(m, Hull(m.Core, "pod", DataHullTypeIGC.HabmLifepod));
        var rescuer = Ship(m, Hull(m.Core, "rescue", DataHullTypeIGC.HabmRescue), id: 2);

        // No friendly restart station anywhere → native returns false (FedSrv.CPP:13075).
        host.RescueDestination = _ => false;
        Assert.False(host.RescueShipEvent(pod, rescuer));
        Assert.False(host.RescueShipEvent(pod, null));
        RescuePass(m.Sectors[0]);          // the discovery pass must not queue a refused rescue
        StationRescuePass(m.Sectors[0]);
        Assert.Empty(m.Sectors[0].DrainStationRescues());
        Assert.Empty(m.Sectors[0].DrainRescues());
        Assert.Empty(m.Sectors[0].DrainStationRescues());

        // A destination exists (the production FindFriendlyStation answered yes) →
        // native docks it and returns true, so the sector queues the pair.
        host.RescueDestination = _ => true;
        Assert.True(host.RescueShipEvent(pod, rescuer));
        RescuePass(m.Sectors[0]);
        var queued = Assert.Single(m.Sectors[0].DrainRescues());
        Assert.Same(pod, queued.Rescuee);
        Assert.Same(rescuer, queued.Rescuer);
    }

    /// <summary>SIM-0209 — <c>HitTreasureEvent</c> carries the native flag refusal
    /// gates: a second flag, and a side's own flag, are refused
    /// (<c>FedSrv.CPP:13562-13568</c>). A refused pickup returns false, so the
    /// sector never queues it and the treasure survives.</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void HitTreasureEvent_refuses_the_native_flag_cases_and_the_sector_obeys()
    {
        Require.Artwork();
        var host = new Allegiance.Server.ServerHost();
        var m = Domain(site: host);
        var ship = Ship(m, Hull(m.Core, "rescue", DataHullTypeIGC.HabmRescue));
        var sector = m.Sectors[0];

        Treasure Flag(short flagSide) =>
            new(m.NextObjectId(), sector, TreasureData(TreasureCode.Flag, flagSide), 0.0, m);

        // Own side's flag (oid == ship->GetSide()->GetObjectID()) → refused.
        var own = Flag(0);
        sector.Add(own);
        Assert.False(host.HitTreasureEvent(0.0, ship, own));

        // Already carrying a flag (GetFlag() != NA) → a second flag is refused.
        var enemy = Flag(1);
        sector.Add(enemy);
        ship.Flag = 1;
        Assert.False(host.HitTreasureEvent(0.0, ship, enemy));
        ship.Flag = null;

        // An enemy flag with no flag carried → accepted (native falls through to
        // GAIN_FLAG + KillTreasureEvent).
        Assert.True(host.HitTreasureEvent(0.0, ship, enemy));

        // End-to-end through the sector: a carried flag means the overlapping
        // enemy flag is not even queued for the host.
        ship.Flag = 1;
        enemy.Position = ship.Position;                  // overlapping the picker
        for (int i = 0; i < 4; i++) sector.Update(0.1 * (i + 1));
        Assert.Empty(sector.DrainTreasurePickups());
        Assert.Contains(enemy, sector.Models);       // the refused flag is still there
    }

    /// <summary>SIM-0212 — <c>ContinueRipcord</c> is the native rule
    /// (<c>FedSrv.CPP:13379-13437</c>): flag carrier, side mismatch and the
    /// LtRipcord hull rule all abort, a legal anchor continues, and the sector
    /// consumes the callback's answer (abort queue + state clear).</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void ContinueRipcord_returns_the_native_rule_and_drives_the_sector()
    {
        Require.Artwork();
        var host = new Allegiance.Server.ServerHost();
        var m = Domain(site: host);
        var ripper = Ship(m, Hull(m.Core, "fighter", 0));
        var target = Ship(m, Hull(m.Core, "target", DataHullTypeIGC.HabmIsRipcordTarget), id: 2);
        var enemy = Ship(m, Hull(m.Core, "enemy", DataHullTypeIGC.HabmIsRipcordTarget), id: 3, side: 1);
        var sector = m.Sectors[0];

        // Legal anchor → true, countdown keeps running.
        ripper.SetRipcordModel(new((short)IgcObjectType.Ship, target.Id, sector.Id));
        ripper.ResetRipcordTimeLeft();
        Assert.True(host.ContinueRipcord(ripper, target));
        RipTick(ripper, 0.1f);
        Assert.True(ripper.IsRipcordActive);
        Assert.Empty(sector.DrainRipcordAborts());

        // Flag carrier → false and the sector aborts (RIPCORD_ABORTED side effects).
        ripper.Flag = 1;
        Assert.False(host.ContinueRipcord(ripper, target));
        RipTick(ripper, 0.1f);
        Assert.False(ripper.IsRipcordActive);
        Assert.Single(sector.DrainRipcordAborts());
        ripper.Flag = null;

        // Wrong side → false (native's GetSide() != pmodel->GetSide() leg).
        Assert.False(host.ContinueRipcord(ripper, enemy));

        // LtRipcord-only target: legal only for a CanLtRipcord ripper.
        var light = Ship(m, Hull(m.Core, "light", DataHullTypeIGC.HabmIsLtRipcordTarget), id: 4);
        Assert.False(host.ContinueRipcord(ripper, light));
        ripper.ChangeHull(ripper.HullType with { HabmCapabilities = DataHullTypeIGC.HabmCanLtRipcord });
        Assert.True(host.ContinueRipcord(ripper, light));

        // No anchor object at all → false (native pmodel->GetCluster() == NULL).
        Assert.False(host.ContinueRipcord(ripper, null));
    }

    /// <summary>SIM-0213 — <c>UseRipcord</c> is the native TELEPORT body
    /// (<c>FedSrv.CPP:13440-13504</c>) and always returns true (the attempt is
    /// consumed). The production host performs it through
    /// <c>Server.PerformRipcordTeleport</c> and the sector clears the state
    /// without deferring to a drain.</summary>
    [SkippableFact, Trait("Requires", "Artwork")]
    public void UseRipcord_performs_the_teleport_in_the_callback_and_consumes_the_attempt()
    {
        Require.Artwork();
        var host = new Allegiance.Server.ServerHost();
        var m = Domain(site: host);
        var ripper = Ship(m, Hull(m.Core, "fighter", 0));
        var target = Ship(m, Hull(m.Core, "target", DataHullTypeIGC.HabmIsRipcordTarget), id: 2);
        var sector = m.Sectors[0];

        var teleported = new List<Ship>();
        host.RipcordUsed = (ship, _) => teleported.Add(ship);   // what Server.Lifecycle wires to PerformRipcordTeleport

        ripper.SetRipcordModel(new((short)IgcObjectType.Ship, target.Id, sector.Id));
        ripper.ResetRipcordTimeLeft();
        Assert.True(host.UseRipcord(ripper, target));   // FedSrv.CPP:13504 — always consumed
        teleported.Clear();

        // Zero-cross through the sector: the host teleports inline and the sector
        // clears the ripcord; nothing is deferred to the teleport drain.
        ripper.SetRipcordModel(new((short)IgcObjectType.Ship, target.Id, sector.Id));
        ripper.ResetRipcordTimeLeft();
        ripper.AdvanceRipcordCountdown(999f);
        RipTick(ripper, 0.1f);
        Assert.Same(ripper, Assert.Single(teleported));
        Assert.False(ripper.IsRipcordActive);
        Assert.Null(ripper.RipcordModel);
        Assert.Empty(sector.DrainRipcordTeleports());
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void FireMissile_reaches_site_when_missile_fired()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site: site);
        var ship = Ship(m, Hull(m.Core, "fighter", 0));
        var mt = m.Core.MissileTypes.Values.First();
        ship.MountPart(Part(new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId)), 0, 0, 255);
        ship.SetPartAmount(ship.Parts[0], 10);
        ship.Update(1.0); // seed LastUpdate so TimeLoaded < now
        ship.CommandTarget = null;
        ship.TryFireMissile(1.0);
        Assert.Contains(site.Calls, c => c.StartsWith("FireMissile"));
    }

}
