using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P690EquipmentReadinessTests
{
    private static (Ship Ship, RecordingIgcSite Site) Flight(float rate = 1)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        core.Constants.FloatConstants[10] = rate;
        var site = new RecordingIgcSite();
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new SideSpec(0, "A", core.Civilizations.Keys.First())], site);
        mission.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        mission.Stage = MissionStage.Started;
        var hull = core.HullTypes.Values.First() with
        {
            HabmCapabilities = 0, Thrust = 0, Mass = 100, Speed = 100,
            MaxEnergy = 1000, MaxFuel = 1000, RechargeRate = 0, HitPoints = 1000
        };
        var ship = new Ship(100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, 0);
        return (ship, site);
    }

    private static MountedPart Mount(Ship ship, EquipmentType kind, byte fraction = 0, byte mount = 0)
    {
        DataPartTypeIGC.Subtype subtype = kind switch
        {
            EquipmentType.Cloak => new DataPartTypeIGC.Cloak(0, 0.8f, 10, 10, 0, 0),
            EquipmentType.Shield => new DataPartTypeIGC.Shield(10, 100, 0, 0, 0),
            EquipmentType.Afterburner => new DataPartTypeIGC.Afterburner(0, 100, 10, 10, 0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return ship.MountPart(new(0, 0, "", "", $"P690 {kind}", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype),
            1, mount, fraction)!;
    }

    private static void Call(Ship ship, string method, params object[] args) =>
        typeof(Ship).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);

    private static int Notifications(RecordingIgcSite site) => site.Calls.Count(c => c.StartsWith("WeaponReady("));

    // Igc/cloakigc.cpp:68-81: literal native first-update readiness, even at rate zero.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0f)] [InlineData(0.001f)] [InlineData(1000f)]
    public void Cloak_first_flight_update_readies_and_activates_once_even_with_zero_mount_rate(float rate)
    {
        Require.Artwork(); var (ship, site) = Flight(rate); var part = Mount(ship, EquipmentType.Cloak);
        ship.StateM = ShipControlState.CloakActive;
        ship.Update(0.01);
        Assert.Equal(1f, part.MountedFraction);
        Assert.True(ship.Cloaking < 1f);
        Assert.Equal(1, Notifications(site));
        ship.Update(0.02);
        Assert.Equal(1, Notifications(site));
    }

    // Igc/shieldIGC.cpp:67-88: preserve replicated fraction while waiting;
    // on the completing tick regenerate using the whole dt, as native does.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Shield_waits_for_float_boundary_preserves_received_fraction_and_then_regenerates()
    {
        Require.Artwork(); var (ship, site) = Flight(); var part = Mount(ship, EquipmentType.Shield);
        ship.SetShieldFraction(0.25f);
        ship.Update(0.999);
        Assert.InRange(part.MountedFraction, 0.998f, 0.9999f);
        Assert.Equal(0.25f, ship.ShieldFraction);
        Assert.Equal(0, Notifications(site));
        ship.Update(1.001);
        Assert.Equal(1f, part.MountedFraction);
        Assert.Equal(0.2502f, ship.ShieldFraction, 5);
        Assert.Equal(1, Notifications(site));
        ship.Update(1.101);
        Assert.Equal(0.2602f, ship.ShieldFraction, 5);
        Assert.Equal(1, Notifications(site));
    }

    // Igc/afterburnerIGC.cpp:111-125: only fuel-consuming pass mounts.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Afterburner_preserves_sub_byte_progress_through_quantity_edit_and_prediction_does_not_mount()
    {
        Require.Artwork(); var (ship, site) = Flight(0.001f); var part = Mount(ship, EquipmentType.Afterburner);
        Call(ship, "UpdateAfterburner", 0.5f, true);
        Assert.Equal(0.0005f, part.MountedFraction, 7);
        Assert.Equal((byte)0, part.FractionMounted);
        ship.SetPartAmount(part, 1);
        part = Assert.Single(ship.Parts);
        Assert.Equal(0.0005f, part.MountedFraction, 7);
        Call(ship, "UpdateAfterburner", 10000f, false);
        Assert.Equal(0.0005f, part.MountedFraction, 7);
        Assert.Equal(0, Notifications(site));
        Call(ship, "UpdateAfterburner", 1000f, true);
        Assert.Equal(1f, part.MountedFraction);
        Assert.Equal(1, Notifications(site));
        Call(ship, "UpdateAfterburner", 1000f, false);
        Assert.Equal(1, Notifications(site));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Afterburner)]
    public void Zero_mount_rate_keeps_shield_and_afterburner_unready_and_silent(EquipmentType kind)
    {
        Require.Artwork(); var (ship, site) = Flight(0); var part = Mount(ship, kind);
        ship.Update(10);
        Assert.Equal(0f, part.MountedFraction);
        Assert.Equal(0f, ship.ShieldFraction);
        Assert.Equal(0, Notifications(site));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(EquipmentType.Cloak)] [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Afterburner)]
    public void Readiness_is_exactly_once_at_completion_and_rearms_after_cargo_remount(EquipmentType kind)
    {
        Require.Artwork(); var (ship, site) = Flight(); Mount(ship, kind);
        ship.Update(1); // exact boundary, rather than overshoot
        ship.Update(2);
        Assert.Equal(1, Notifications(site));
        Assert.True(ship.TrySwapPartWithCargo(kind, 0, -1));
        ship.Update(3);
        Assert.Equal(1, Notifications(site));
        Assert.True(ship.TrySwapPartWithCargo(kind, -1, 0));
        ship.Update(4);
        Assert.Equal(1f, Assert.Single(ship.Parts).MountedFraction);
        Assert.Equal(2, Notifications(site));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(EquipmentType.Cloak)] [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Afterburner)]
    public void Docked_boarded_and_cargo_equipment_do_not_advance_or_notify(EquipmentType kind)
    {
        Require.Artwork(); var (ship, site) = Flight(); var part = Mount(ship, kind);
        ship.StationId = 200;
        ship.Update(1);
        ship.StationId = null;
        ship.ParentShip = new Ship(101, ship.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.Update(2);
        Assert.Equal(0f, part.MountedFraction);
        Assert.Equal(0, Notifications(site));
        ship.ParentShip = null;
        Assert.True(ship.TrySwapPartWithCargo(kind, 0, -1));
        ship.Update(3);
        Assert.Equal(0f, ship.CargoHold[Ship.CargoSlotForMount(-1)]!.MountedFraction);
        Assert.Equal(0, Notifications(site));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(EquipmentType.Cloak)] [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Afterburner)]
    public void Fully_mounted_spawn_equipment_does_not_emit_a_ready_notification(EquipmentType kind)
    {
        Require.Artwork(); var (ship, site) = Flight(); Mount(ship, kind, 240);
        ship.Update(1);
        Assert.Equal(0, Notifications(site));
    }
}
