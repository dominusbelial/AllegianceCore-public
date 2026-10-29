using System.Numerics;
using Allegiance.Core;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;
using Coll = Allegiance.Core.Collision;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P880b — framework batch 2 (the P880b queue rows): GetShipHasCargoCondition through
/// SetRadarLODAction. Pure framework tests run against <see cref="FakeTrainingSite"/>; the
/// production-binding tests drive the real embedded <see cref="TrainingHost"/> through
/// <see cref="TrainingHostSite"/> (the same host the client's <see cref="ClientTrainingSite"/>
/// forwards to), so divergence from native fails the binding.
/// </summary>
[Collection("Training")]
public sealed class P880bFrameworkBindingTests
{
    // ── framework primitives (fake site) ─────────────────────────────────────

    [Fact]
    public void GetShipHasCargoCondition_MatchesMountedEquipmentType()
    {
        // getshiphascargocondition.cpp:29-36 — any mounted part with GetEquipmentType() == part.
        var site = new FakeTrainingSite();
        site.MountedEquipmentTypes[7] = new HashSet<EquipmentType> { EquipmentType.Weapon };
        Assert.True(new GetShipHasCargoCondition(site, new TargetRef(ObjectType.Ship, 7), EquipmentType.Weapon).Evaluate());
        Assert.False(new GetShipHasCargoCondition(site, new TargetRef(ObjectType.Ship, 7), EquipmentType.Shield).Evaluate());
    }

    [Fact]
    public void ReleaseConsumerAction_ReleasesTheBuoy()
    {
        // releaseconsumeraction.cpp:25-28 — m_buoy->ReleaseConsumer().
        var site = new FakeTrainingSite();
        new ReleaseConsumerAction(site, 5).Execute();
        Assert.Contains(5, site.ReleasedConsumers);
    }

    [Fact]
    public void ResetShipAction_SnapshotsOnceThenRestores()
    {
        // resetshipaction.cpp:22-36 — first Execute captures, later Executes restore.
        var site = new FakeTrainingSite();
        var ship = site.AddObject(ObjectType.Ship, 7, new Vector3(1f, 2f, 3f), Vector3.UnitX);
        var action = new ResetShipAction(site, new TargetRef(ObjectType.Ship, 7));
        action.Execute();                       // capture at (1,2,3)
        ship.Position = new Vector3(90f, 90f, 90f);
        action.Execute();                       // restore to (1,2,3)
        Assert.Equal(new Vector3(1f, 2f, 3f), ship.Position);
    }

    [Fact]
    public void SetRadarLODAction_SetsTheRadarMode()
    {
        // setradarlodaction.cpp:31-36 — SetRadarMode(m_radarLOD) when it differs.
        var site = new FakeTrainingSite { RadarLod = 1 };
        new SetRadarLODAction(site, 3).Execute();
        Assert.Equal(3, site.RadarLod);
    }

    [Fact]
    public void SetCargoNanAction_AppliesToTheShip()
    {
        // setcargonanaction.cpp:22-52 — the nanite cargo recipe on the chosen ship.
        var site = new FakeTrainingSite();
        new SetCargoNanAction(site, new TargetRef(ObjectType.Ship, 7)).Execute();
        Assert.Contains(7, site.CargoNanApplied);
    }

    // ── production bindings (real embedded host) ─────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void GetShipHasCargoCondition_ReadsRealMountedEquipment()
    {
        // getshiphascargocondition.cpp:29-36 — real mounted part list, by equipment type.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        int drone = host.CreateDrone("eq", -1, 412, 0, (int)Ship.PilotTypeWingman);
        Assert.True(host.Drones[drone].Parts.Count > 0, "default loadout mounts real parts");
        Assert.True(site.HasPartOfEquipmentType(new TargetRef(ObjectType.Ship, drone),
            host.Drones[drone].Parts[0].EquipmentType));

        // Native m_parts holds cargo parts too (shipIGC.cpp:1460-1464): a part only in the
        // cargo hold satisfies the condition.
        var ship = host.Drones[drone];
        // Default loadouts fill the hold; free slot 0 (mount -1) for the cargo-only part.
        Assert.True(ship.TryRemoveCargoPart(-1, out _));
        var cargoOnly = host.Mission.Core.PartTypes.Values.First(p =>
            !ship.Parts.Any(m => m.EquipmentType == p.EquipmentType)
            && !ship.CargoHold.Any(m => m?.EquipmentType == p.EquipmentType));
        var target = new TargetRef(ObjectType.Ship, drone);
        Assert.False(site.HasPartOfEquipmentType(target, cargoOnly.EquipmentType));
        Assert.NotNull(ship.MountPart(cargoOnly, 1, unchecked((byte)-1), Allegiance.Core.Wire.ExpandedPartData.FullyMounted));
        Assert.True(site.HasPartOfEquipmentType(target, cargoOnly.EquipmentType));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void GetShipHasMountedCondition_And_SetDroneNan_MountARealPart()
    {
        // getshiphasmountedcondition.cpp:36-45 + setdronenanaction.cpp:36-52 — a real mount.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        int drone = host.CreateDrone("nan", -1, 439, 0, (int)Ship.PilotTypeWingman);
        var r = new TargetRef(ObjectType.Ship, drone);
        // getshiphasmountedcondition.cpp:36-45 — the real mounted-part read: a part realised by the
        // default loadout (never in the scripted set) must be seen as mounted by HasPartMounted.
        var realPart = host.Drones[drone].Parts[0];
        Assert.True(site.HasPartMounted(r, realPart.PartId),
            "a real loadout-mounted part must be detected through the real parts list");
        Assert.False(site.HasPartMounted(r, SetDroneNanAction.NanitePartId));
        new SetDroneNanAction(site, r).Execute();
        Assert.True(site.HasPartMounted(r, SetDroneNanAction.NanitePartId),
            "SetDroneNanAction mounts the nanite weapon on a real ship");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void ResetShipAction_SnapshotsAndRestoresARealShip()
    {
        // resetshipaction.cpp:22-36 — real position/velocity snapshot + restore.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        var action = new ResetShipAction(site, new TargetRef(ObjectType.Ship, host.PlayerShipId));
        action.Execute();                               // capture current pose
        var before = host.PlayerShip.Position;
        host.PlayerShip.Position = new Coll.Vector3(500f, 0f, 0f);   // simulate drift
        action.Execute();                               // restore captured pose
        Assert.Equal(before.X, host.PlayerShip.Position.X);
        Assert.Equal(before.Z, host.PlayerShip.Position.Z);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void ReleaseConsumerAction_ReleasedBuoyStopsResolving()
    {
        // releaseconsumeraction.cpp:25-28 — a released buoy's consumer no longer resolves.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        host.CreateWaypoint(11, new Vector3(0f, 0f, 50f), 1010, 0, true);
        Assert.NotNull(site.Resolve(new TargetRef(ObjectType.Buoy, 11)));
        new ReleaseConsumerAction(site, 11).Execute();
        Assert.Null(site.Resolve(new TargetRef(ObjectType.Buoy, 11)));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void SetRadarLODAction_SetsTheRealRadarLod()
    {
        // setradarlodaction.cpp:31-36 — SetRadarMode(m_radarLOD).
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        Assert.Equal(0, site.GetRadarLod());
        new SetRadarLODAction(site, 2).Execute();
        Assert.Equal(2, site.GetRadarLod());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void RepairRearmAction_RestoresRealResources()
    {
        // repairrearraction.cpp:37-74 — real shield/hull/energy/ammo/fuel restore on the player ship.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        int pid = host.PlayerShipId;
        host.DamageShip(pid, 4000f);
        host.PlayerShip.Energy = 1f;
        host.PlayerShip.Ammo = 0;
        host.PlayerShip.SetFuel(1f);
        new RepairRearmAction(site).Execute();
        Assert.Equal(1f, host.PlayerShip.Fraction, 3);
        Assert.Equal(host.PlayerShip.EffectiveMaxEnergy(), host.PlayerShip.Energy, 3);
        Assert.Equal(host.PlayerShip.HullType.MaxAmmo, host.PlayerShip.Ammo);
        Assert.Equal(host.PlayerShip.HullType.MaxFuel, host.PlayerShip.Fuel, 3);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void RepairRearm_walks_the_cargo_hold_exactly_as_the_native_loops()
    {
        // repairrearmaction.cpp:51-68 — loops run i > -c_maxCargo (mount -5 never filled); the
        // first empty mount gets missiles (150); the second loop has no break, so every other
        // empty mount gets ammo (24) and the third loop starts exhausted.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var ship = host.PlayerShip;
        for (int mount = -1; mount >= -Ship.CargoCapacity; mount--) ship.TryRemoveCargoPart(mount, out _);
        host.RepairRearm(host.PlayerShipId);
        short?[] ids = ship.CargoHold.Select(p => p?.PartId).ToArray();
        Assert.Equal(new short?[] { 150, 24, 24, 24, null }, ids);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void GetShipIsDamagedCondition_ReadsTheRealShipFraction()
    {
        // getshipisdamagedcondition.cpp:49-59 — a damaged real ship's fraction differs from 1.0.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        int pid = host.PlayerShipId;
        host.DamageShip(pid, 4000f);
        Assert.True(new GetShipIsDamagedCondition(site, ObjectType.Ship, pid, 1.0f).Evaluate());
        host.RepairRearm(pid);
        Assert.False(new GetShipIsDamagedCondition(site, ObjectType.Ship, pid, 1.0f).Evaluate());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void GetTargetCondition_ReadsTheRealCommandTarget()
    {
        // gettargetcondition.cpp:50-53 — GetCommandTarget(c_cmdCurrent) == expected target.
        Require.Artwork();
        using var host = TrainingHost.Start();
        var site = new TrainingHostSite(host);
        host.CreateWaypoint(21, new Vector3(0f, 0f, 80f), 1010, 0, true);
        var tgt = new TargetRef(ObjectType.Buoy, 21);
        host.SetCommandTarget(host.PlayerShipId, tgt, CmdGoto);
        Assert.True(new GetTargetCondition(site, new TargetRef(ObjectType.Ship, host.PlayerShipId), tgt).Evaluate());
        host.SetCommandTarget(host.PlayerShipId, null, CmdGoto);
        Assert.False(new GetTargetCondition(site, new TargetRef(ObjectType.Ship, host.PlayerShipId), tgt).Evaluate());
    }
}
