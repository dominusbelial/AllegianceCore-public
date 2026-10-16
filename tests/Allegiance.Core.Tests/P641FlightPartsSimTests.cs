using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public class P641FlightPartsSimTests
{
    private static Mission Mission()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var mission = global::Allegiance.Core.Model.Mission.Assemble(core, map, [new SideSpec(0, "A", 1), new SideSpec(1, "B", 3)], new NullIgcSite());
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        mission.Stage = MissionStage.Started;
        return mission;
    }
    private static Ship Ship(Mission m, short id, short side, Vector3 pos = default)
    {
        var hull = m.Core.HullTypes.Values.First(h => h.MaxFixedWeapons > 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { Mission = m, SideId = side };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(true, 5)] [InlineData(false, 30)]
    public void QuickReady_changes_only_initial_delay_and_requires_strictly_past_boundary(bool quick, int delay)
    {
        Require.Artwork();
        var m = Mission();
        var launcher = Ship(m, 100, 0);
        Ship(m, 101, 1, new(0, 0, 50));
        var type = m.Core.ProbeTypes[30] with { EabmCapabilities = (short)(0x40 | (quick ? 8 : 0)), Lifespan = 100 };
        var probe = new Probe(102, m.Sectors[0], type, default, 10, 0, launcher);
        m.Sectors[0].Add(probe);
        probe.Update(10 + delay - .001);
        Assert.Equal(type.Ammo, probe.Ammo);
        probe.Update(10 + delay);
        Assert.Equal(type.Ammo, probe.Ammo);
        probe.Update(10 + delay + .001);
        Assert.Equal(type.Ammo - 1, probe.Ammo);
        Assert.Single(m.Sectors[0].Models.OfType<Projectile>());
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("none")] [InlineData("other-sector")] [InlineData("removed")]
    [InlineData("friendly")] [InlineData("allied")] [InlineData("out-of-range")] [InlineData("dead")]
    public void ShootOnlyTarget_never_falls_back_to_another_enemy(string invalid)
    {
        Require.Artwork();
        var m = Mission();
        var launcher = Ship(m, 100, 0);
        var target = Ship(m, 101, invalid == "friendly" ? (short)0 : (short)1,
            new(0, 0, invalid == "out-of-range" ? 50000 : 50));
        Ship(m, 103, 1, new(0, 0, 30)); // tempting closer fallback
        var type = m.Core.ProbeTypes[30] with { EabmCapabilities = 0x1048 };
        var probe = new Probe(102, m.Sectors[0], type, default, 0, 0, launcher) { Target = invalid == "none" ? null : target };
        m.Sectors[0].Add(probe);
        if (invalid == "other-sector") { m.PlaceShip(target, m.Sectors[1]); }
        if (invalid == "removed") m.Sectors[0].Remove(target);
        if (invalid == "dead") target.SetFraction(0);
        if (invalid == "allied") m.AlliedSidePairs.Add((0, 1));
        probe.Update(5.01);
        Assert.Equal(type.Ammo, probe.Ammo);
        Assert.Empty(m.Sectors[0].Models.OfType<Projectile>());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShootOnlyTarget_aims_at_designated_enemy_even_with_a_closer_enemy()
    {
        Require.Artwork();
        var m = Mission();
        var launcher = Ship(m, 100, 0);
        var target = Ship(m, 101, 1, new(0, 0, 80));
        Ship(m, 103, 1, new(40, 0, 0));
        var type = m.Core.ProbeTypes[30] with { EabmCapabilities = 0x1048 };
        var probe = new Probe(102, m.Sectors[0], type, default, 0, 0, launcher) { Target = target };
        m.Sectors[0].Add(probe);
        probe.Update(5.01);
        var round = Assert.Single(m.Sectors[0].Models.OfType<Projectile>());
        Assert.True(round.Velocity.Z > 0 && Math.Abs(round.Velocity.X) < .01);
        Assert.Same(target, probe.Target);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShootOnlyTarget_resolves_a_boarded_target_to_its_carrier()
    {
        Require.Artwork();
        var m = Mission();
        var launcher = Ship(m, 100, 0);
        var carrier = Ship(m, 101, 1, new(0, 0, 50));
        var passenger = Ship(m, 103, 1, new(400, 0, 0));
        passenger.ParentShip = carrier;
        var type = m.Core.ProbeTypes[30] with { EabmCapabilities = 0x1048 };
        var probe = new Probe(102, m.Sectors[0], type, default, 0, 0, launcher) { Target = passenger };
        m.Sectors[0].Add(probe);
        probe.Update(5.01);
        var round = Assert.Single(m.Sectors[0].Models.OfType<Projectile>());
        Assert.True(round.Velocity.Z > 0 && Math.Abs(round.Velocity.X) < .01);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flight_swap_resets_readiness_and_cargo_is_inert_until_remounted()
    {
        Require.Artwork();
        var m = Mission();
        var ship = Ship(m, 100, 0);
        var weapon = m.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon && ship.HullType.CanMountPart(p));
        ship.MountPart(weapon, 1, 0, 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Weapon, 0, -1));
        Assert.Empty(ship.Weapons);
        Assert.Equal(0, ship.CargoHold[global::Allegiance.Core.Sim.Ship.CargoSlotForMount(-1)]!.FractionMounted);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Weapon, -1, 0));
        Assert.Equal(0, ship.Parts.Single().FractionMounted);
        ship.StateM = ShipControlState.AllWeapons;
        ship.Weapons.Single().Update(.01);
        Assert.False(ship.Weapons.Single().FiringShot);
        ship.Update(.1);
        Assert.InRange(ship.Parts.Single().FractionMounted, (byte)1, (byte)254);
        Assert.False(ship.TrySwapPartWithCargo(EquipmentType.Weapon, 0, -6));
        Assert.True(ship.TryRemovePart(EquipmentType.Weapon, 0, out _));
        Assert.Empty(ship.Weapons);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reload_resets_exactly_to_zero_then_mounts_again_only_in_flight(bool launcher)
    {
        Require.Artwork();
        var m = Mission();
        var ship = Ship(m, 100, 0);
        var chaff = m.Core.ChaffTypes.Values.First();
        if (launcher)
            // This test transfers ten real rounds; native launcher quantities
            // obey hull capacity / expendable size (launchertypeigc.h:130-145).
            ship.ChangeHull(ship.HullType with { CapacityChaffLauncher = checked((short)(10 * Math.Max(1, (int)chaff.LauncherExpendableSize))) });
        var weapon = m.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 }
            && InFlightPartRules.CanMount(ship.HullType, p, 0));
        var part = launcher ? weapon with { Type = new DataPartTypeIGC.ChaffLauncher(chaff.ExpendableTypeId), PartMask = 0 } : weapon;
        ship.MountPart(part, 2, 0, 255);
        var cargo = launcher ? part : weapon with { PartId = 904, Type = new DataPartTypeIGC.Pack(0, 10) };
        Assert.True(ship.TryMountCargoPart(cargo, 10, -1, 0));

        Assert.True(ship.TryApplyReloadEntry(-1, 3));
        Assert.Equal(0, ship.Parts.Single().FractionMounted); // FedSrv.CPP:6574-6581,6637
        Assert.Equal(7, ship.CargoHold[0]!.Amount);
        if (launcher) Assert.Equal(5, ship.Parts.Single().Amount);

        // A second reset must discard even a sub-byte mounting remainder.
        float rate = m.Core.Constants.FloatConstants[10]; // c_fcidMountRate
        double now = .4 / (ExpandedPartData.FullyMounted * rate);
        ship.Update(now);
        Assert.Equal(0, ship.Parts.Single().FractionMounted);
        Assert.True(ship.TryApplyReloadEntry(-1, 1));
        Assert.Equal(0, ship.Parts.Single().FractionMounted);
        now += .2 / (ExpandedPartData.FullyMounted * rate);
        ship.Update(now);
        Assert.Equal(0, ship.Parts.Single().FractionMounted); // stale .4 + .2 would round to 1
        ship.StationId = 123;
        ship.Update(now += .1);
        Assert.Equal(0, ship.Parts.Single().FractionMounted);
        ship.StationId = null;
        ship.ParentShip = Ship(m, 101, 0);
        ship.Update(now += .1);
        Assert.Equal(0, ship.Parts.Single().FractionMounted);
        ship.ParentShip = null;
        ship.Update(now += .1);
        Assert.InRange(ship.Parts.Single().FractionMounted, (byte)1, (byte)(ExpandedPartData.FullyMounted - 1));
        ship.Update(now + 10);
        Assert.Equal(ExpandedPartData.FullyMounted, ship.Parts.Single().FractionMounted);
    }
}
