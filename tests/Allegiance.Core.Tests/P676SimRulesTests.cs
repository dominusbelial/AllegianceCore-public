using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P676SimRulesTests
{
    private static Mission Domain(IIgcSite? site = null, int sideCount = 2)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 3).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], []),
            Enumerable.Range(0, sideCount).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static Ship Place(Mission m, short id = 100, short side = 0, short sector = 0, float x = 0, float z = 0, byte pilot = Ship.PilotTypeWingman)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 0, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = 0, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000
        };
        var s = new Ship(id, hull, new(x, 0, z), Vector3.Zero, Orientation.Identity)
            { SideId = side, IsNpc = true, PilotType = pilot };
        m.PlaceShip(s, sector); return s;
    }

    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype) =>
        new(0, 0, "", "", "Test nan", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype);

    private static MountedPart Gun(Ship ship, float power = 10, byte mounted = 240)
    {
        var core = ship.Mission!.Core;
        var projectile = core.ProjectileTypes.Values.First() with { Speed = 100, Lifespan = 10, Power = power, AbsoluteF = true };
        ((IDictionary<short, DataProjectileTypeIGC>)core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        return ship.MountPart(Part(new DataPartTypeIGC.Weapon(0, 0.1f, 0, 0, 0, projectile.ProjectileTypeId, 0, 0, 0)), 1, 0, mounted)!;
    }

    private static void Call(Ship ship, string method, params object[] args) =>
        typeof(Ship).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);

    private static void RipTick(Ship ship, float dt) =>
        typeof(Sector).GetMethod("ResolveRipcordCountdowns", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship.Sector, [dt]);

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0.09f, true)] [InlineData(0.11f, false)] [InlineData(0.4f, false)]
    public void Combat_uses_native_point_one_radian_lead_gate(float angle, bool fire)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s);
        s.AttackTarget = Place(m, 101, 1, x: 500 * MathF.Sin(angle), z: -500 * MathF.Cos(angle));
        Call(s, "UpdateWingmanCombat", 0.05f);
        Assert.Equal(fire, (s.StateM & ShipControlState.AllWeapons) != 0);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0f, true)] [InlineData(40f, false)] [InlineData(-40f, true)]
    public void Combat_range_gate_uses_intercept_time_and_relative_speed(float speedAway, bool fire)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s);
        var target = Place(m, 101, 1, z: -900); target.Velocity = new(0, 0, -speedAway); s.AttackTarget = target;
        Call(s, "UpdateWingmanCombat", 0.05f);
        Assert.Equal(fire, (s.StateM & ShipControlState.AllWeapons) != 0);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(-10f, 0.5f, 0.09f, true)] [InlineData(-10f, 1f, 0f, false)]
    [InlineData(10f, 0.5f, 0f, false)] [InlineData(-10f, 0.5f, 0.11f, false)]
    public void Repair_requires_nan_damage_missing_health_and_native_angle(float power, float health, float angle, bool fire)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s, power);
        var target = Place(m, 101, x: 500 * MathF.Sin(angle), z: -500 * MathF.Cos(angle)); target.SetFraction(health);
        s.SetRepairOrder(target); Call(s, "UpdateWingmanRepair", 0.1, 0.05f);
        Assert.Equal(fire, (s.StateM & ShipControlState.AllWeapons) != 0);
        Assert.True(s.HasRepairOrder); Assert.Null(s.AttackTarget);
        s.StationId = 31000;
        Call(s, "UpdateWingmanRepair", 0.2, 0.05f);
        Assert.Equal(0, s.StateM & (ShipControlState.AllWeapons | ShipControlState.MineFire));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(Ship.PilotTypeWingman, 2499f, true)] [InlineData(Ship.PilotTypePlayer, 2499f, true)]
    [InlineData(Ship.PilotTypeMiner, 1000f, false)] [InlineData(Ship.PilotTypeWingman, 2500f, false)]
    public void Repair_mine_decision_checks_pilot_distance_amount_and_load_time(byte pilot, float distance, bool deploy)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s, -10);
        var target = Place(m, 101, z: -500); target.SetFraction(0.5f); s.SetRepairOrder(target); s.WingmanBehaviour = 4;
        Place(m, 102, 1, x: distance, pilot: pilot);
        var type = m.Core.MineTypes.Values.First();
        var part = s.MountPart(Part(new DataPartTypeIGC.MineLauncher(type.ExpendableTypeId)), 2, 0, 240)!;
        Call(s, "UpdateWingmanRepair", 0.1, 0.05f);
        Assert.Equal(deploy, (s.StateM & ShipControlState.MineFire) != 0);
        Assert.Equal(deploy ? 1 : 2, s.Parts.Single(p => p.EquipmentType == EquipmentType.Dispenser).Amount);
        Call(s, "UpdateWingmanRepair", 0.1, 0.05f); // same time: no duplicate deployment
        Assert.Equal(deploy ? 1 : 0, s.Sector!.Models.OfType<Mine>().Count());
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Weapon_ready_notification_occurs_once_on_float_completion_and_again_after_remount()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site); var s = Place(m); var part = Gun(s, mounted: 0);
        m.Core.Constants.FloatConstants[10] = 1;
        Call(s, "AdvanceFlightMounts", 0.999f);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("WeaponReady("));
        Call(s, "AdvanceFlightMounts", 0.002f); Call(s, "AdvanceFlightMounts", 1f);
        Assert.Single(site.Calls, c => c.StartsWith("WeaponReady("));
        Call(s, "ResetEquippedFraction", EquipmentType.Weapon); Call(s, "AdvanceFlightMounts", 1f);
        Assert.Equal(2, site.Calls.Count(c => c.StartsWith("WeaponReady(")));
        Assert.Equal(1, part.MountedFraction);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(MissionStage.Starting, true, false)] [InlineData(MissionStage.Started, false, false)]
    [InlineData(MissionStage.Started, true, true)] [InlineData(MissionStage.Over, true, false)]
    public void Payday_only_accrues_and_pays_in_started_with_developments(MissionStage stage, bool developments, bool pays)
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        m.Core.Constants.FloatConstants[6] = 1;
        m.Update(0.1); // arm the cadence in Started
        m.Stage = stage; m.AllowDevelopments = developments; m.Update(1.1);
        Assert.Equal(pays ? m.Sides.Count : 0, site.Calls.Count(c => c.StartsWith("PaydayEvent(")));
        Assert.Equal(pays, m.Sides.Any(s => s.Money > 0));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, true, false)] [InlineData(true, true, true)] [InlineData(true, false, false)]
    public void Ripcord_revalidates_alliance_and_allow_allied_rip_each_tick(bool allow, bool allied, bool legal)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var anchor = Place(m, 101, 1, 1);
        anchor.ChangeHull(anchor.HullType with { HabmCapabilities = DataHullTypeIGC.HabmIsRipcordTarget });
        m.AllowAlliedRip = allow;
        if (allied) { m.Sides[0].AllyGroup = 0; m.Sides[1].AllyGroup = 0; }
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); s.ResetRipcordTimeLeft();
        RipTick(s, 0.1f); Assert.Equal(legal, s.IsRipcordActive);
        if (legal) { m.Sides[1].AllyGroup = null; RipTick(s, 0.1f); Assert.False(s.IsRipcordActive); }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(true)] [InlineData(false)]
    public void Ripcord_aborts_when_flag_is_acquired_or_anchor_hull_loses_capability(bool flag)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var anchor = Place(m, 101, sector: 1);
        anchor.ChangeHull(anchor.HullType with { HabmCapabilities = DataHullTypeIGC.HabmIsRipcordTarget });
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); s.ResetRipcordTimeLeft(); RipTick(s, 0.1f);
        Assert.True(s.IsRipcordActive);
        if (flag) s.Flag = 1; else anchor.ChangeHull(anchor.HullType with { HabmCapabilities = 0 });
        RipTick(s, 0.1f); Assert.False(s.IsRipcordActive); Assert.Single(s.Sector!.DrainRipcordAborts());
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0.75f, -0.075f)] [InlineData(1f, -0.1f)]
    public void Native_turn_law_applies_skill_to_stopping_drift(float skill, float expectedYaw)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m);
        s.ChangeHull(s.HullType with { MaxTurnRates = [1, 1, 1], TurnTorques = [1000, 1000, 1000] });
        s.SetTurnRate(ControlData.AxisYaw, 1);
        Call(s, "TurnWingmanToFace", new Vector3(0, 0, -1), 1f, skill);
        Assert.Equal(expectedYaw, s.Controls.Yaw, 5); Assert.Equal(0, s.Controls.Pitch); Assert.Equal(0, s.Controls.Roll);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Repair_station_uses_station_geometry_and_keeps_firing_at_full_station_health()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s, -10);
        var type = m.Core.StationTypes.Values.First();
        var data = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 31000, StationTypeId = type.StationTypeId, ClusterId = 0, SideId = 0, Position = [0, 0, -800] };
        var station = new Station(data, type); m.PlaceStation(station, m.Sectors[0]);
        s.SetRepairOrder(null, station); Call(s, "UpdateWingmanRepair", 0.1, 0.05f);
        Assert.True((s.StateM & ShipControlState.AllWeapons) != 0);
        m.Sectors[0].RemoveStation(station); Call(s, "UpdateWingmanRepair", 0.2, 0.05f);
        Assert.Equal(0, s.StateM & ShipControlState.AllWeapons);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Ripcord_follows_the_same_anchor_when_it_changes_sector()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var anchor = Place(m, 101, sector: 1);
        anchor.ChangeHull(anchor.HullType with { HabmCapabilities = DataHullTypeIGC.HabmIsRipcordTarget });
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); s.ResetRipcordTimeLeft();
        anchor.Sector!.Remove(anchor); m.PlaceShip(anchor, 2); RipTick(s, 0.1f);
        Assert.True(s.IsRipcordActive); Assert.Equal(2, s.RipcordModel!.Value.ClusterId);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(true, true)] [InlineData(false, false)]
    public void Reduced_light_anchor_is_valid_only_for_a_light_capable_ripper(bool lightRipper, bool legal)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var anchor = Place(m, 101, sector: 1);
        anchor.ChangeHull(anchor.HullType with { HabmCapabilities = DataHullTypeIGC.HabmIsLtRipcordTarget });
        if (lightRipper) s.ChangeHull(s.HullType with { HabmCapabilities = DataHullTypeIGC.HabmCanLtRipcord });
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); s.ResetRipcordTimeLeft();
        RipTick(s, 0.1f); Assert.Equal(legal, s.IsRipcordActive);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Repair_order_fires_real_projectiles_that_restore_friendly_hull()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s, -10);
        var target = Place(m, 101, z: -500); target.SetFraction(0.5f);
        s.SetRepairOrder(target);
        m.Update(8);
        Assert.True(target.Fraction > 0.5f, $"Nanite rounds must restore the target, got {target.Fraction}");
        target.SetFraction(1); Call(s, "UpdateWingmanRepair", 8.1, 0.05f);
        Assert.Equal(0, s.StateM & ShipControlState.AllWeapons);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Starting_does_not_arm_payday_and_disabled_developments_preserve_phase_without_backpay()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site);
        m.Core.Constants.FloatConstants[6] = 1;
        m.Stage = MissionStage.Starting; m.Update(0.1); m.Update(1.1);
        m.Stage = MissionStage.Started; m.Update(2.1); m.Update(2.9);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("PaydayEvent("));
        m.AllowDevelopments = false; m.Update(3.1);
        m.AllowDevelopments = true; m.Update(4.0);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("PaydayEvent("));
        m.Update(4.1);
        Assert.Equal(m.Sides.Count, site.Calls.Count(c => c.StartsWith("PaydayEvent(")));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Allied_neighbor_does_not_starve_default_or_defend_enemy_acquisition()
    {
        Require.Artwork(); var m = Domain(sideCount: 3); var s = Place(m); Gun(s);
        Place(m, 101, 1, z: -50);
        var enemy = Place(m, 102, 2, z: -500);
        m.Sides[0].AllyGroup = 7; m.Sides[1].AllyGroup = 7;
        Call(s, "DecideWingmanDefaultOrder"); Assert.Same(enemy, s.AttackTarget);
        s.AttackTarget = null;
        var charge = Place(m, 103, z: 100); s.DefendTarget = charge;
        Call(s, "UpdateWingmanDefend"); Assert.Same(enemy, s.AttackTarget);
    }

    private sealed class RipSite : IIgcSite
    {
        public bool Continue = true, Use;
        public int Uses;
        public Action<Ship>? OnUse;
        public bool ContinueRipcord(IShipIgc s, IModelIgc a) => Continue;
        public bool UseRipcord(IShipIgc s, IModelIgc a) { Uses++; OnUse?.Invoke((Ship)s); return Use; }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Alternate_ripcord_site_can_defer_at_zero_retry_and_consume_or_abort()
    {
        Require.Artwork(); var site = new RipSite(); var m = Domain(site); var s = Place(m);
        var anchor = Place(m, 101, sector: 1);
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); s.ResetRipcordTimeLeft();
        site.OnUse = ripper => ripper.ResetRipcordTimeLeft();
        RipTick(s, 2); Assert.True(s.IsRipcordActive); Assert.Equal(0, s.RipcordTimeLeft); Assert.Equal(1, site.Uses);
        Assert.Empty(s.Sector!.DrainRipcordTeleports());
        site.OnUse = ripper => { ripper.Sector!.Remove(ripper); m.PlaceShip(ripper, 2); };
        site.Use = true; RipTick(s, 0.1f); Assert.False(s.IsRipcordActive); Assert.Equal(2, site.Uses);
        s.SetRipcordModel(new((short)IgcObjectType.Ship, anchor.Id, 1)); site.Continue = false; RipTick(s, 0.1f);
        Assert.False(s.IsRipcordActive); Assert.Single(s.Sector.DrainRipcordAborts()); Assert.Equal(2, site.Uses);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(8000f, 0.05f)] [InlineData(16000f, 0.8f)]
    public void Generated_map_reads_loaded_universe_radius_and_asteroid_lens(float radius, float lens)
    {
        Require.Artwork(); var core = Domain().Core; core.Constants.FloatConstants[0] = lens; core.Constants.FloatConstants[1] = radius;
        var civ = core.Civilizations.Keys.First();
        var map = MissionMapGenerator.Generate(new() { Teams = 2, CivIds = [civ, civ] }, core, 0);
        Assert.NotEmpty(map.Warps); Assert.NotEmpty(map.Asteroids);
        foreach (var warp in map.Warps)
            Assert.Equal(radius * 0.6f, MathF.Sqrt(warp.Position[0] * warp.Position[0] + warp.Position[1] * warp.Position[1]), 2);
        foreach (var rock in map.Asteroids)
        {
            var p = rock.Position;
            float effective = MathF.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2] / (lens * lens));
            Assert.InRange(effective, 0, radius * 0.75001f);
        }
        Assert.Contains(map.Asteroids, a => MathF.Abs(a.Position[2]) > radius * lens * 0.1f);
    }
}
