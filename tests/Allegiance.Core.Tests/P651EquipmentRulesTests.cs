using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P651EquipmentRulesTests
{
    private static Mission Domain()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        core.Constants.FloatConstants[10] = 0.2f;
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var station = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 300, ClusterId = 0, SideId = 1, StationTypeId = core.StationTypes.Keys.First(),
            Position = [0, 0, -500], Forward = [0, 0, -1], Up = [0, 1, 0]
        };
        var warp = DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with { WarpId = 301, ClusterId = 0, Position = [0, 0, -500], Radius = 10 };
        var map = MissionMap.FromLists([cluster], [warp], [station], []);
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, map, [new SideSpec(0, "A", civ), new SideSpec(1, "B", civ)], new NullIgcSite());
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship Place(Mission m, short id = 100, short side = 0, float z = 0)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            HabmCapabilities = 0, Mass = 100, Thrust = 0, Speed = 1000, MaxFuel = 1000,
            MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0, HitPoints = 1000, Length = 10,
            MaxFixedWeapons = 2, MaxWeapons = 2, Hardpoints = [], ScannerRange = 10000, Signature = 1, Ecm = 1
        };
        var s = new Ship(id, hull, new(0, 0, z), Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(s, 0); return s;
    }

    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype, float signature = 3) => new(
        0, 0, "", "", "P651", "", 0, [], [], 0, signature, 32000, -1, 0, "", subtype);

    private static DataMissileTypeIGC MissileType(Mission m, bool warp = false)
    {
        var mt = m.Core.MissileTypes.Values.First() with
        {
            LoadTime = 2, LockTime = 2, LockAngle = 1, MaxLock = 0.75f,
            ReadyTime = 0, InitialSpeed = 100, Acceleration = 10, Lifespan = 10,
            EabmCapabilities = (short)(warp ? 2 : 0), BlastPower = 0
        };
        ((IDictionary<short, DataMissileTypeIGC>)m.Core.MissileTypes)[mt.ExpendableTypeId] = mt;
        return mt;
    }

    private static MountedPart Launcher(Ship s, DataMissileTypeIGC mt, byte fraction = 255) =>
        s.MountPart(Part(new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId)), 10, 0, fraction)!;

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Magazine)]
    [InlineData(EquipmentType.Dispenser)] [InlineData(EquipmentType.ChaffLauncher)]
    [InlineData(EquipmentType.Weapon)] [InlineData(EquipmentType.Afterburner)]
    public void Each_active_part_adds_signature_and_unmount_removes_it(EquipmentType kind)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var mt = MissileType(m);
        DataPartTypeIGC.Subtype type = kind switch
        {
            EquipmentType.Shield => new DataPartTypeIGC.Shield(1, 100, 0, 0, 0),
            EquipmentType.Magazine => new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId),
            EquipmentType.Dispenser => new DataPartTypeIGC.MineLauncher(m.Core.MineTypes.Keys.First()),
            EquipmentType.ChaffLauncher => new DataPartTypeIGC.ChaffLauncher(m.Core.ChaffTypes.Keys.First()),
            EquipmentType.Weapon => new DataPartTypeIGC.Weapon(0, 0.1f, 0, 0, 0, m.Core.ProjectileTypes.Keys.First(), 0, 0, 0),
            _ => new DataPartTypeIGC.Afterburner(0.01f, 100, 1, 1, 0, 0)
        };
        float before = s.EffectiveSignature;
        var part = s.MountPart(Part(type), 1, 0, 255)!;
        if (kind == EquipmentType.Weapon) part.FunctionalWeapon!.Activate();
        if (kind == EquipmentType.Afterburner) { s.StateM = ShipControlState.AfterburnerButton; s.Update(0.1); }
        Assert.Equal(before + 3, s.EffectiveSignature, 4);
        Assert.True(s.TrySwapPartWithCargo(kind, 0, -1));
        s.StateM = 0; s.Update(0.2);
        Assert.Equal(before, s.EffectiveSignature, 4);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0f)] [InlineData(0.5f)]
    public void Zero_and_partial_lock_fire_and_scale_native_max_lock(float fraction)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var t = Place(m, 101, 1, -100);
        s.CommandTarget = t; var mt = MissileType(m); Launcher(s, mt);
        s.UpdateMissileLock(mt, fraction * mt.LockTime);
        Assert.Equal(fraction, s.MissileLock, 4);
        Assert.True(s.TryFireMissile(0.01));
        var missile = Assert.Single(s.Sector!.Models.OfType<Missile>());
        Assert.Equal(fraction * mt.MaxLock, missile.LockFraction, 4);
        Assert.False(missile.IsDud); Assert.Same(t, missile.Target);
        Assert.Equal(9, s.Parts.Single().Amount); Assert.Equal(0, s.MissileLock);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Load_time_gates_firing_and_lock_buildup_with_a_strict_deadline()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); s.CommandTarget = Place(m, 101, 1, -100);
        var mt = MissileType(m); Launcher(s, mt);
        Assert.True(s.TryFireMissile(0.01));
        Assert.False(s.TryFireMissile(2.01));
        s.Update(1); Assert.Equal(0, s.MissileLock);
        Assert.True(s.TryFireMissile(2.02));
        Assert.Equal(8, s.Parts.Single().Amount);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(1.49, true)] [InlineData(1.5, false)]
    public void Early_player_fire_consumes_a_round_and_is_a_dud_outside_half_second_tolerance(double elapsed, bool dud)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var mt = MissileType(m); Launcher(s, mt);
        Assert.True(s.TryFireMissile(0.01));
        Assert.True(s.TryFireMissile(0.01 + elapsed, clientRequest: true, requestedLock: 0.4f));
        var missile = s.Sector!.Models.OfType<Missile>().Last();
        Assert.Equal(dud, missile.IsDud); Assert.Equal(0.3f, missile.LockFraction, 4);
        Assert.Equal(8, s.Parts.Single().Amount);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(IgcObjectType.Station, false)] [InlineData(IgcObjectType.Warp, true)]
    public void Station_and_warp_targets_build_lock_and_remain_missile_targets(IgcObjectType type, bool bomb)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var mt = MissileType(m, bomb);
        var target = EquipmentTarget.Resolve(s.Sector, type, (short)(bomb ? 301 : 300));
        Assert.NotNull(target); s.CommandTarget = target; Launcher(s, mt);
        s.UpdateMissileLock(mt, 1);
        Assert.Equal(0.5f, s.MissileLock, 4); // no hull ECM on a static target
        Assert.True(s.TryFireMissile(0.01));
        var missile = Assert.Single(s.Sector!.Models.OfType<Missile>());
        missile.Update(0.02); Assert.Same(target, missile.Target);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Cargo_swap_and_ammo_reload_delay_guns_until_mount_fraction_reaches_one()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m);
        var wp = new DataPartTypeIGC.Weapon(0, 0.01f, 0, 0, 1, m.Core.ProjectileTypes.Keys.First(), 0, 0, 0);
        s.MountPart(Part(wp), 1, 0, 255);
        Assert.True(s.TrySwapPartWithCargo(EquipmentType.Weapon, 0, -1));
        Assert.True(s.TrySwapPartWithCargo(EquipmentType.Weapon, -1, 0));
        s.StateM = ShipControlState.AllWeapons;
        for (int i = 1; i <= 499; i++) s.Update(i * 0.01);
        Assert.Empty(s.Sector!.Models.OfType<Projectile>());
        Assert.InRange(s.Parts.Single().MountedFraction, 0.99f, 0.99999f);
        s.Update(5.02); Assert.NotEmpty(s.Sector.Models.OfType<Projectile>());
        var pack = s.MountPart(Part(new DataPartTypeIGC.Pack(0, 10)), 10, 0, 255)!;
        Assert.True(s.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));
        Assert.True(s.TryApplyReloadEntry(-1, -1));
        var gun = s.Weapons.Single(); Assert.Equal(0, gun.MountedFraction);
        s.Update(5.03); Assert.False(gun.FiringShot);
        s.Update(10.04); Assert.True(gun.FiringShot);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Magazine_swap_waits_mount_duration_plus_load_time()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var mt = MissileType(m); Launcher(s, mt);
        s.TrySwapPartWithCargo(EquipmentType.Magazine, 0, -1);
        s.TrySwapPartWithCargo(EquipmentType.Magazine, -1, 0);
        Assert.Equal(7, s.Parts.Single().TimeLoaded, 3);
        s.Update(5.1); Assert.False(s.TryFireMissile(5.1));
        s.Update(7.01); Assert.True(s.TryFireMissile(7.01));
    }
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Target_change_clears_old_lock_including_same_id_of_another_object_type()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var mt = MissileType(m);
        var target = Place(m, 300, 1, -100); s.CommandTarget = target;
        s.UpdateMissileLock(mt, 1); Assert.Equal(0.5f, s.MissileLock, 4);
        s.CommandTarget = target; Assert.Equal(0.5f, s.MissileLock, 4);
        s.CommandTarget = EquipmentTarget.Resolve(s.Sector, IgcObjectType.Station, 300);
        Assert.Equal(0, s.MissileLock);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Zero_mount_rate_keeps_swapped_guns_unready()
    {
        Require.Artwork(); var m = Domain(); m.Core.Constants.FloatConstants[10] = 0;
        var s = Place(m); var type = new DataPartTypeIGC.Weapon(0, 0.1f, 0, 0, 0, m.Core.ProjectileTypes.Keys.First(), 0, 0, 0);
        s.MountPart(Part(type), 1, 0, 0); s.StateM = ShipControlState.AllWeapons;
        s.Update(10); Assert.Equal(0, s.Parts.Single().MountedFraction);
        Assert.Empty(s.Sector!.Models.OfType<Projectile>());
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Dud_passes_through_contact_without_damage_or_termination()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var target = Place(m, 101, 1, -100);
        var mt = MissileType(m) with { Power = 10000, Acceleration = 0, Radius = 1, ReadyTime = 0, Lifespan = 10 };
        var dud = s.FireMissile(mt, 0, target, 1, dud: true);
        for (int i = 1; i <= 30; i++) m.Update(i * 0.05);
        Assert.Equal(1, target.Fraction); Assert.Contains(dud, s.Sector!.Models);
        Assert.True(dud.Position.Z < target.Position.Z);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Shield_mount_starts_discharged_and_only_regenerates_after_ready()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m);
        s.MountPart(Part(new DataPartTypeIGC.Shield(1, 100, 0, 0, 0)), 1, 0, 0);
        Assert.Equal(0, s.ShieldFraction);
        s.Update(4.9); Assert.Equal(0, s.ShieldFraction);
        s.Update(5.1); Assert.InRange(s.ShieldFraction, 0.001f, 0.003f);
    }

}
