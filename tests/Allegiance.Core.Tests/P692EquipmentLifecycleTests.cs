using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

public sealed class P692EquipmentLifecycleTests
{
    private static (Mission Mission, Ship Ship, Host Host) World()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        core.Constants.FloatConstants[10] = 2;
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var host = new Host();
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            Enumerable.Range(0, 3).Select(i => new SideSpec((short)i, $"Side {i}", core.Civilizations.Keys.First())).ToArray(), host);
        mission.Stage = MissionStage.Started;
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        var hull = core.HullTypes.Values.First() with
        {
            HitPoints = 1000, Length = 10, Mass = 100, Thrust = 0, Speed = 1000, Signature = 1,
            ScannerRange = 10000, CapacityDispenser = 30000, CapacityChaffLauncher = 30000,
            MaxWeapons = 0, MaxFixedWeapons = 0, HabmCapabilities = 0
        };
        var ship = new Ship(100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, 0);
        return (mission, ship, host);
    }

    private static MountedPart Mount(Ship ship, string kind, byte fraction = 240, short amount = 3)
    {
        var core = ship.Mission!.Core;
        DataPartTypeIGC.Subtype subtype;
        if (kind == "chaff")
        {
            var type = core.ChaffTypes.Values.First() with { LoadTime = 2, Lifespan = 100, Radius = 3, Signature = 1 };
            ((IDictionary<short, DataChaffTypeIGC>)core.ChaffTypes)[type.ExpendableTypeId] = type;
            subtype = new DataPartTypeIGC.ChaffLauncher(type.ExpendableTypeId);
        }
        else if (kind == "mine")
        {
            var type = core.MineTypes.Values.First() with { LoadTime = 2, Lifespan = 100, Radius = 3, Signature = 1 };
            ((IDictionary<short, DataMineTypeIGC>)core.MineTypes)[type.ExpendableTypeId] = type;
            subtype = new DataPartTypeIGC.MineLauncher(type.ExpendableTypeId);
        }
        else
        {
            var type = core.ProbeTypes.Values.First() with { LoadTime = 2, Lifespan = 100, Radius = 3, Signature = 1, DtRipcord = -1, EabmCapabilities = 0x1000 };
            ((IDictionary<short, DataProbeTypeIGC>)core.ProbeTypes)[type.ExpendableTypeId] = type;
            subtype = new DataPartTypeIGC.ProbeLauncher(type.ExpendableTypeId);
        }
        return ship.MountPart(new(0, 0, "", "", "P692 dispenser", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype), amount, 0, fraction)!;
    }

    // Igc/dispenserigc.cpp:84-123, dispenserigc.h:83-87: continuous held
    // input, exact deadline, capacity decrement, and float mount completion.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("mine")] [InlineData("probe")] [InlineData("chaff")]
    public void Held_input_fires_only_after_mount_and_strict_load_deadline_and_repeats(string kind)
    {
        Require.Artwork(); var (_, ship, host) = World(); var part = Mount(ship, kind, 0);
        var calls = new List<double>();
        host.ExpendableFired = (s, p, time) => { calls.Add(time); s.FireDispenser(p, time); };
        ship.StateM = kind == "chaff" ? ShipControlState.ChaffFire : ShipControlState.MineFire;
        Assert.Equal(2.5, part.TimeLoaded);
        ship.Update(.49); Assert.True(part.MountedFraction < 1); Assert.Empty(calls);
        ship.Update(.5); Assert.Equal(1, part.MountedFraction); Assert.Empty(calls);
        ship.Update(2.5); Assert.Empty(calls);
        ship.Update(2.501); Assert.Single(calls); Assert.Equal(2, ship.Parts.Single().Amount);
        double next = ship.Parts.Single().TimeLoaded;
        ship.Update(next); Assert.Single(calls);
        ship.Update(next + .001); Assert.Equal(2, calls.Count); Assert.Equal(1, ship.Parts.Single().Amount);
    }

    // Igc/dispenserigc.cpp:111-115: chaff may fire during ripcord; mines/probes cannot.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("mine", false)] [InlineData("probe", false)] [InlineData("chaff", true)]
    public void Ripcord_blocks_dispenser_but_allows_chaff(string kind, bool fires)
    {
        Require.Artwork(); var (_, ship, _) = World(); Mount(ship, kind);
        ship.SetRipcordModel(new(1, 5, 0));
        ship.StateM = kind == "chaff" ? ShipControlState.ChaffFire : ShipControlState.MineFire;
        ship.Update(.1);
        Assert.Equal(fires ? 2 : 3, ship.Parts.Single().Amount);
        Assert.Equal(fires, ship.Sector!.Models.Any(m => m is Chaff));
    }

    // Igc/dispenserigc.cpp:103-115; shipIGC.cpp:266-271.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("empty")] [InlineData("docked")] [InlineData("boarded")] [InlineData("wrong-bit")] [InlineData("cargo")]
    public void Ineligible_dispenser_does_not_invoke_callback(string gate)
    {
        Require.Artwork(); var (_, ship, host) = World(); Mount(ship, "mine", amount: (short)(gate == "empty" ? 0 : 1));
        int count = 0; host.ExpendableFired = (_, _, _) => count++;
        ship.StateM = gate == "wrong-bit" ? ShipControlState.ChaffFire : ShipControlState.MineFire;
        if (gate == "docked") ship.StationId = 200;
        if (gate == "boarded") ship.ParentShip = new Ship(101, ship.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        if (gate == "cargo") Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Dispenser, 0, -1));
        ship.Update(.1); Assert.Equal(0, count);
    }

    // Igc/dispenserigc.cpp:89-100: one mountedSound + ready text per completion.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("mine")] [InlineData("probe")] [InlineData("chaff")]
    public void Dispenser_ready_callback_is_once_and_rearms_after_cargo_remount(string kind)
    {
        Require.Artwork(); var (_, ship, host) = World(); var part = Mount(ship, kind, 0);
        int count = 0; host.WeaponReadyNotification = (_, name) => { Assert.Equal(part.Type.Name, name); count++; };
        ship.Update(.49); Assert.Equal(0, count);
        ship.Update(.5); ship.Update(.6); Assert.Equal(1, count);
        Assert.True(ship.TrySwapPartWithCargo(part.EquipmentType, 0, -1));
        Assert.True(ship.TrySwapPartWithCargo(part.EquipmentType, -1, 0));
        var remounted = ship.Parts.Single(); Assert.Equal(3.1, remounted.TimeLoaded, 6);
        ship.Update(1.1); ship.Update(1.2); Assert.Equal(2, count);
    }

    // FedSrv/FedSrv.CPP:5713-5774: callback geometry, epochs, current target.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("mine", false)] [InlineData("mine", true)] [InlineData("probe", false)] [InlineData("probe", true)]
    public void Concrete_host_callback_deploys_behind_velocity_or_hull_and_preserves_epoch(string kind, bool moving)
    {
        Require.Artwork(); var (mission, ship, host) = World(); var part = Mount(ship, kind);
        ship.InitClock(10); ship.Position = new(100, 200, 300); ship.Velocity = moving ? new(10, 0, 0) : Vector3.Zero;
        ship.CommandTarget = ship;
        ((IIgcSite)host).FireExpendable(ship, part, 10.1);
        var deployed = Assert.Single(ship.Sector!.Models, m => m is Mine or Probe);
        float radius = deployed is Mine mine ? mine.MineType.MineRadius : ((Probe)deployed).ProbeType.Radius;
        float offset = radius + 10;
        var expected = ship.Position + (moving ? new Vector3(-offset, 0, 0) : ship.Orientation.GetBackward() * offset);
        Assert.Equal(expected, deployed.Position); Assert.Equal(2, ship.Parts.Single().Amount);
        if (deployed is Mine field) Assert.Equal(13, field.DeployTime);
        if (deployed is Probe probe) { Assert.Equal(10, probe.DeployTime); Assert.Same(ship, probe.Target); }
        Assert.Equal(10.1, deployed.LastUpdate);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Real_mine_drop_uses_native_derived_radius_and_loaded_lifetime(bool moving)
    {
        Require.Artwork(); var (mission, ship, _) = World();
        var type = mission.Core.MineTypes.Values.First(t => t.Radius < 0 && t.MineRadius > 0);
        var launcher = new ResearchTree(mission.Core, mission.Core.Civilizations.Keys.First()).Parts
            .First(p => p.Type is DataPartTypeIGC.MineLauncher m && m.ExpendableTypeId == type.ExpendableTypeId);
        var part = ship.MountPart(launcher, 2, 0, 240)!;
        ship.InitClock(10); ship.Position = new(100, 200, 300);
        ship.Velocity = moving ? new(10, 0, 0) : Vector3.Zero;
        var deployment = ship.FireDispenser(part, 10.1)!;
        var mine = Assert.IsType<Mine>(deployment.Model);
        float offset = type.MineRadius + ship.HullType.Length * .5f + 5;
        var expected = ship.Position + (moving ? new Vector3(-offset, 0, 0) : ship.Orientation.GetBackward() * offset);
        Assert.Equal(expected, mine.Position);
        Assert.Equal(ship.LastUpdate + 3 + type.Lifespan, mine.TimeExpire, 5);
        Assert.Equal(10.1 + type.LoadTime, part.TimeLoaded, 5);
        Assert.Equal(1, ship.Parts.Single().Amount);
    }

    // Igc/probeigc.cpp:444-465: no acquisition means no reveal; scanner
    // presence, rather than range, reveals on firing; a late scanner is retried.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Probe_first_shot_reveals_to_scanner_sides_and_retries_late_arrivals()
    {
        Require.Artwork(); var (mission, ship, _) = World(); var sector = ship.Sector!;
        var projectile = mission.Core.ProjectileTypes.Values.First() with { Speed = 1000, Lifespan = 10, BlastRadius = 0 };
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        var type = mission.Core.ProbeTypes.Values.First() with
        { DtRipcord = -1, Lifespan = 100, Radius = 2, Signature = 1, ProjectileTypeId = projectile.ProjectileTypeId, EabmCapabilities = 0x48, Ammo = 0, DtimeBurst = 1, Accuracy = 1, ScannerRange = 10000 };
        var probe = new Probe(200, sector, type, Vector3.Zero, 0, 0, ship); sector.Add(probe);
        var farScanner = new Ship(201, ship.HullType with { ScannerRange = 0 }, new(100000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(farScanner, sector);
        probe.Update(5.1); Assert.False(probe.SeenBySide(1)); // no target in weapon range
        farScanner.Position = new(100, 0, 0);
        probe.Update(5.2); Assert.True(probe.SeenBySide(1)); Assert.False(probe.SeenBySide(2));
        var late = new Ship(202, ship.HullType, new(100000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 2 };
        mission.PlaceShip(late, sector);
        probe.Update(6.3); Assert.True(probe.SeenBySide(2));
        sector.Remove(late);
        // A subsequent visibility sweep may lose the eye, but the predictable
        // firing contact retains its known position (FedSrv.CPP:11120-11126).
        probe.SideVisibility.SetSideVisibility(2, false);
        Assert.True(probe.SeenBySide(2)); Assert.False(probe.GetCurrentEye(2));
    }

    // Igc/probeigc.cpp:459: allied visualization can reveal without a scanner.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Firing_reveal_obeys_allied_visualization_policy(bool enabled)
    {
        Require.Artwork(); var (mission, ship, _) = World(); mission.AllowAlliedViz = enabled;
        mission.AlliedSidePairs.Add((0, 2));
        var projectile = mission.Core.ProjectileTypes.Values.First() with { Speed = 1000, Lifespan = 10 };
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = -1, Lifespan = 100, Radius = 2, Signature = 1, ProjectileTypeId = projectile.ProjectileTypeId,
            EabmCapabilities = 0x48, Ammo = 0, DtimeBurst = 1, Accuracy = 1, ScannerRange = 10000 };
        var target = new Ship(201, ship.HullType, new(100, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(target, ship.Sector!);
        var probe = new Probe(200, ship.Sector!, type, Vector3.Zero, 0, 0, ship); ship.Sector!.Add(probe);
        probe.Update(5.1); Assert.Equal(enabled, probe.SeenBySide(2));
    }

    // Igc/probeigc.cpp:151-154,222-230,333-349. Real production host events,
    // expiry wins a simultaneous activation; non-teleports never notify.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0, 100, 1)] [InlineData(2, 100, 1)] [InlineData(2, 2, 0)] [InlineData(-1, 2, 0)]
    public void Teleport_activation_and_termination_have_native_boundaries_and_are_once(float delay, float lifespan, int activations)
    {
        Require.Artwork(); var (mission, ship, host) = World(); int active = 0, destroyed = 0;
        host.TeleportProbeActivated = _ => active++; host.TeleportProbeDestroyed = _ => destroyed++;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = delay, Lifespan = lifespan, ProjectileTypeId = -1 };
        var probe = ship.DeployProbe(type, 0)!;
        probe.Update(1.99); Assert.Equal(delay == 0 ? 1 : 0, active);
        probe.Update(2); probe.Update(2); Assert.Equal(activations, active);
        probe.Update(100); probe.Update(101); ship.Sector!.Remove(probe);
        Assert.Equal(delay >= 0 ? 1 : 0, destroyed);
    }

    // Igc/probeigc.cpp:226-230: termination also covers damage, ammunition
    // exhaustion and direct removal, including a never-activated teleport.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("damage")] [InlineData("remove")]
    public void Teleport_loss_notifies_once_even_before_activation(string cause)
    {
        Require.Artwork(); var (mission, ship, host) = World(); int count = 0;
        host.TeleportProbeDestroyed = _ => count++;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = 50, Lifespan = 100, HitPoints = 1, ProjectileTypeId = -1 };
        var probe = ship.DeployProbe(type, 0)!;
        if (cause == "damage") probe.ReceiveDamage(0, 10000, 1, probe.Position, Vector3.Zero, null);
        else ship.Sector!.Remove(probe);
        ship.Sector!.Remove(probe); probe.Update(101);
        Assert.Equal(1, count);
    }
    // Igc/probeigc.cpp:151-154: a delayed/imported creation must activate
    // during initialization; seeding LastUpdate to creation avoids losing it.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Backdated_probe_creation_activates_immediately_and_does_not_repeat()
    {
        Require.Artwork(); var (mission, ship, host) = World(); int count = 0;
        host.TeleportProbeActivated = _ => count++;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = 2, Lifespan = 100, ProjectileTypeId = -1 };
        var probe = new Probe(200, ship.Sector!, type, Vector3.Zero, 1, 0, ship, createdAt: 5);
        ship.Sector!.Add(probe); Assert.Equal(1, count); Assert.Equal(5, probe.LastUpdate);
        probe.Update(6); Assert.Equal(1, count);
    }

    // FedSrv/FedSrv.CPP:5656-5657; Igc/hullTypeIGC.cpp:83-86.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Chaff_callback_uses_scaled_hull_emission_and_native_upward_kick()
    {
        Require.Artwork(); var (_, ship, host) = World(); var part = Mount(ship, "chaff");
        ship.Hull = new CvhFile { Radius = 4, EllipseEquation = new(4, 4, 4), EllipseRadiusMultiplier = 4,
            Hulls = [], Frames = [new("chaff", new(2, 0, 0), new(0, 0, -1))] };
        ship.Position = new(10, 20, 30); ship.Velocity = new(1, 2, 3);
        ((IIgcSite)host).FireExpendable(ship, part, .1);
        var chaff = Assert.Single(ship.Sector!.Models.OfType<Chaff>());
        Assert.Equal(new Vector3(7.5f, 20, 30), chaff.Position);
        Assert.Equal(new Vector3(1, 7, 3), chaff.Velocity);
    }

    // Igc/probeigc.cpp:444-465,513-521,226-230: reveal precedes the shot,
    // and ammunition exhaustion terminates the teleport target exactly once.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Firing_reveal_precedes_projectile_callback_and_ammo_exhaustion_notifies_loss()
    {
        Require.Artwork(); var (mission, ship, host) = World(); int shots = 0, losses = 0;
        var projectile = mission.Core.ProjectileTypes.Values.First() with { Speed = 1000, Lifespan = 10 };
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = 0, Lifespan = 100, Radius = 2, Signature = 1,
            ProjectileTypeId = projectile.ProjectileTypeId, EabmCapabilities = 0x48, Ammo = 1, DtimeBurst = 1, Accuracy = 1, ScannerRange = 10000 };
        var target = new Ship(201, ship.HullType, new(100, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(target, ship.Sector!);
        var probe = new Probe(200, ship.Sector!, type, Vector3.Zero, 0, 0, ship); ship.Sector!.Add(probe);
        host.ProjectileCreated = _ => { shots++; Assert.True(probe.SeenBySide(1)); };
        host.TeleportProbeDestroyed = _ => losses++;
        probe.Update(5.1); probe.Update(6.1); Assert.Equal(1, shots); Assert.Equal(1, losses);
        Assert.DoesNotContain(probe, ship.Sector.Models);
    }

    // Igc/clusterIGC.h:126,145-148,219: only registered scanner kinds
    // reveal a firing probe. Managed docked/boarded ships may still be in Models.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("ship", true)] [InlineData("docked", false)] [InlineData("boarded", false)]
    [InlineData("mine", false)] [InlineData("probe", true)] [InlineData("station", true)]
    public void Firing_reveal_counts_registered_scanners_and_excludes_docked_or_boarded_ships(string kind, bool reveals)
    {
        Require.Artwork(); var (mission, ship, _) = World(); var sector = ship.Sector!;
        var projectile = mission.Core.ProjectileTypes.Values.First() with { Speed = 1000, Lifespan = 10 };
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        var type = mission.Core.ProbeTypes.Values.First() with { DtRipcord = -1, Lifespan = 100, Radius = 2, Signature = 1,
            ProjectileTypeId = projectile.ProjectileTypeId, EabmCapabilities = 0x1048, Ammo = 0, DtimeBurst = 1, Accuracy = 1, ScannerRange = 10000 };
        var target = new Ship(201, ship.HullType, new(100, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(target, sector);
        var position = new Vector3(100000, 0, 0);
        if (kind == "probe") sector.Add(new Probe(202, sector, type with { ProjectileTypeId = -1 }, position, 0, 2));
        else if (kind == "mine")
        {
            var launcher = new Ship(203, ship.HullType, position, Vector3.Zero, Orientation.Identity) { SideId = 2 };
            sector.Add(new Mine(202, sector, mission.Core.MineTypes.Values.First(), position, 0, launcher));
        }
        else if (kind == "station")
            mission.PlaceStation(new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
                { StationId = 202, SideId = 2, ClusterId = sector.Id, Position = [100000, 0, 0] },
                mission.Core.StationTypes.Values.First()), sector);
        else
        {
            var scanner = new Ship(202, ship.HullType, position, Vector3.Zero, Orientation.Identity) { SideId = 2 };
            mission.PlaceShip(scanner, sector);
            if (kind == "docked") scanner.StationId = 300;
            if (kind == "boarded") scanner.ParentShip = ship;
        }
        var probe = new Probe(200, sector, type, Vector3.Zero, 0, 0, ship) { Target = target }; sector.Add(probe);
        probe.Update(5.1); Assert.Contains(sector.Models, m => m is Projectile);
        Assert.Equal(reveals, probe.SeenBySide(2));
    }

}
