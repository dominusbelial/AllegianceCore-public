using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P708ShipUpdateTests
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

    private static Ship Place(Mission m, short id = 100, short side = 0, short sector = 0, float x = 0, float z = 0, byte pilot = Ship.PilotTypeWingman, bool npc = true)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 0, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = 0, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000, Signature = 1
        };
        var s = new Ship(id, hull, new(x, 0, z), Vector3.Zero, Orientation.Identity)
            { SideId = side, IsNpc = npc, PilotType = pilot };
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

    private static void Clock(Ship ship, double now) =>
        typeof(Allegiance.Core.Sim.Model).GetField("_lastUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, now);

    private static T Read<T>(Ship ship, string name) => (T)typeof(Ship).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ship)!;
    private static void Write(Ship ship, string name, object value) => typeof(Ship).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, value);

    [SkippableFact]
    public void Defend_scans_only_after_two_hundred_complete_passes_and_rearms_on_new_plan()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site); var s = Place(m); Gun(s);
        var charge = Place(m, 101, x: 100); var enemy = Place(m, 102, 1, z: -1200);
        s.DefendTarget = charge; Call(s, "UpdateWingmanDefend"); Assert.Same(enemy, s.AttackTarget);
        enemy.Position = new(0, 0, -4000);
        for (int i = 0; i < 200; i++) Call(s, "UpdateWingmanDefend");
        Assert.Same(enemy, s.AttackTarget);
        Call(s, "UpdateWingmanDefend"); Assert.Null(s.AttackTarget);
        Assert.Equal(200, Read<int>(s, "_wingmanCheckCooldown"));
        Assert.Contains(site.Calls, c => c.Contains("Returning to defend"));
        s.DefendTarget = Place(m, 103, x: 200); Assert.Equal(0, Read<int>(s, "_wingmanCheckCooldown"));
    }

    [SkippableTheory]
    [InlineData(1499f, Ship.PilotTypeWingman, true)]
    [InlineData(1500f, Ship.PilotTypeWingman, false)]
    [InlineData(1000f, Ship.PilotTypeMiner, false)]
    public void Defend_acquisition_uses_strict_radius_and_combat_pilots(float distance, byte pilot, bool acquire)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.DefendTarget = Place(m, 101);
        var enemy = Place(m, 102, 1, z: -distance, pilot: pilot);
        Call(s, "UpdateWingmanDefend"); Assert.Equal(acquire, s.AttackTarget == enemy);
    }

    [SkippableFact]
    public void Unseen_target_reduces_lead_then_coasts_at_strict_three_second_boundary()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s);
        s.ChangeHull(s.HullType with { ScannerRange = 0 });
        var target = Place(m, 101, 1, z: -500); target.Velocity = new(40, 0, 0); s.SetWingmanAttackOrder(target);
        Clock(s, 10); Call(s, "UpdateWingmanCombat", .05f);
        Clock(s, 10.05); Call(s, "UpdateWingmanCombat", .05f);
        var expected = Place(m, 102); Gun(expected); expected.SolveLead(target, expected.Weapons[0], .5f, out var direction);
        Call(expected, "TurnWingmanToFace", direction, .05f, .75f);
        Assert.Equal(expected.Controls.Yaw, s.Controls.Yaw, 5);
        Clock(s, 13); Call(s, "UpdateWingmanCombat", .05f); Assert.NotEqual(0, s.Controls.Yaw);
        var controls = s.Controls; Write(s, "_wingmanControlsBeforePlot", controls);
        Clock(s, 13.001); Call(s, "UpdateWingmanCombat", .05f);
        Assert.Equal(controls, s.Controls); Assert.Equal(ShipControlState.ForwardButton, s.StateM);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void After_five_seconds_accepted_attack_investigates_unless_a_visible_enemy_is_near(bool enemyNear)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.ChangeHull(s.HullType with { ScannerRange = 0 });
        var target = Place(m, 101, 1, z: -500); s.SetWingmanAttackOrder(target);
        if (enemyNear) { var other = Place(m, 102, 1, x: 300); other.SideVisibility.SetSideVisibility(0, true); }
        Clock(s, 10); Call(s, "UpdateWingmanCombat", .05f);
        Clock(s, 15); Call(s, "UpdateWingmanCombat", .05f); Assert.Same(target, s.AttackTarget);
        Clock(s, 15.001); Call(s, "UpdateWingmanCombat", .05f); Assert.Null(s.AttackTarget);
        Assert.Equal(!enemyNear, s.GotoWaypoint is not null);
        if (!enemyNear) { Assert.Equal(target.Position, s.GotoWaypoint!.Value.Position); Assert.Equal(0x80, s.WingmanBehaviour & 0x80); }
    }

    [SkippableFact]
    public void Plan_attack_loss_restores_interrupted_goto_and_new_target_resets_memory()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.ChangeHull(s.HullType with { ScannerRange = 0 });
        var target = Place(m, 101, 1, z: -500); s.AttackTarget = target;
        var route = new GotoOrder(GotoKind.Waypoint, 2, new(100, 0, 100)); Write(s, "_wingmanInterruptedGoto", route);
        Clock(s, 10); Call(s, "UpdateWingmanCombat", .05f);
        Clock(s, 16); Call(s, "UpdateWingmanCombat", .05f);
        Assert.Null(s.AttackTarget); Assert.Equal(route, s.GotoWaypoint);
        s.AttackTarget = Place(m, 102, 1, z: -600);
        Assert.Null(Read<double?>(s, "_targetFirstNotSeen"));
    }

    private static int Range(Ship s, Allegiance.Core.Sim.Model target, float t, float life, int previous = 0, double now = 0) =>
        (int)typeof(Ship).GetMethod("WingmanRangeState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s, [target, t, life, previous, now])!;

    [SkippableTheory]
    [InlineData(4.499f, -21f, ShipControlState.ForwardButton)]
    [InlineData(4.499f, 21f, ShipControlState.BackwardButton)]
    [InlineData(4.5f, 51f, ShipControlState.BackwardButton)]
    [InlineData(4.5f, 50f, 0)]
    [InlineData(8.999f, -31f, ShipControlState.ForwardButton | ShipControlState.AfterburnerButton)]
    [InlineData(9f, 121f, ShipControlState.BackwardButton)]
    [InlineData(9f, 80f, 0)]
    [InlineData(9f, 19f, ShipControlState.ForwardButton | ShipControlState.AfterburnerButton)]
    [InlineData(15f, 0f, ShipControlState.AfterburnerButton)]
    public void Range_bands_use_native_strict_time_and_closing_speed_thresholds(float t, float relative, int expected)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var target = Place(m, 101, 1, z: -500);
        s.Velocity = new(0, 0, -relative); Assert.Equal(expected, Range(s, target, t, 10));
    }

    [SkippableTheory]
    [InlineData(-16f, 0, false)] [InlineData(-16f, ShipControlState.AfterburnerButton, true)]
    [InlineData(-15f, ShipControlState.AfterburnerButton, false)]
    public void Midrange_boost_has_native_hysteresis(float relative, int previous, bool boost)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); var target = Place(m, 101, 1, z: -500);
        s.Velocity = new(0, 0, -relative);
        Assert.Equal(boost, (Range(s, target, 5, 10, previous) & ShipControlState.AfterburnerButton) != 0);
    }

    [SkippableFact]
    public void Combat_preserves_dodge_for_one_hundred_passes_and_never_reuses_weapon_bits()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.AttackTarget = Place(m, 101, 1, z: -500);
        Write(s, "_wingmanDodgedThisTick", true); Write(s, "_wingmanDodgeState", ShipControlState.LeftButton);
        Call(s, "UpdateWingmanCombat", .05f); Assert.Equal(100, Read<int>(s, "_wingmanDodgeCooldown"));
        Write(s, "_wingmanDodgedThisTick", false);
        for (int i = 0; i < 100; i++) { Call(s, "UpdateWingmanCombat", .05f); Assert.NotEqual(0, s.StateM & ShipControlState.LeftButton); }
        Call(s, "UpdateWingmanCombat", .05f); Assert.Equal(0, s.StateM & ShipControlState.LeftButton);
    }

    [SkippableTheory]
    [InlineData(0, 1f, 0f, false)] [InlineData(2, .999f, 0f, false)]
    [InlineData(2, 1f, 49.99f, true)] [InlineData(2, 1f, 50f, false)]
    public void Missile_selection_requires_behavior_full_lock_and_strict_transverse_speed(int behavior, float quality, float transverse, bool fire)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.WingmanBehaviour = behavior;
        var type = m.Core.MissileTypes.Values.First() with { MaxLock = 1 };
        ((IDictionary<short, DataMissileTypeIGC>)m.Core.MissileTypes)[type.ExpendableTypeId] = type;
        s.MountPart(Part(new DataPartTypeIGC.MissileLauncher(type.ExpendableTypeId)), 2, 0, 240);
        var target = Place(m, 101, 1, z: -500); target.Velocity = new(transverse, 0, 0); s.AttackTarget = target;
        typeof(Ship).GetProperty("MissileLock")!.SetValue(s, quality); Call(s, "UpdateWingmanCombat", .05f);
        Assert.Equal(fire, (s.StateM & ShipControlState.MissileFire) != 0);
        if (fire) Assert.Same(target, s.CommandTarget);
    }

    [SkippableFact]
    public void Goto_opportunism_waits_ten_seconds_and_one_hundred_passes_then_resumes_route()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.WingmanBehaviour = 0x40;
        var route = new GotoOrder(GotoKind.Waypoint, 0, new(1000, 0, 0)); s.SetGoto(route);
        var target = Place(m, 101, 1, z: -300);
        Call(s, "UpdateWingmanTravel", 9.999); Assert.Null(s.AttackTarget);
        for (int i = 0; i < 100; i++) Call(s, "UpdateWingmanTravel", 10.0);
        Assert.Null(s.AttackTarget); Call(s, "UpdateWingmanTravel", 10.0);
        Assert.Same(target, s.AttackTarget); Assert.Null(s.GotoWaypoint);
        target.SetFraction(0); Call(s, "UpdateWingmanCombat", .05f); Assert.Equal(route, s.GotoWaypoint);
    }

    [SkippableTheory]
    [InlineData(799f, 101f, true)] [InlineData(800f, 101f, false)] [InlineData(799f, 100f, false)]
    public void Cross_sector_chase_requires_accepted_order_destination_mouth_and_scanner(float distance, float scanner, bool follow)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.ChangeHull(s.HullType with { ScannerRange = scanner });
        var dest = m.Sectors[1];
        var warp = DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with { WarpId = 1, ClusterId = 1, Position = [0, 0, 0] };
        ((List<ResolvedWarp>)dest.Warps).Add(new(warp, m.Sectors[0]));
        var target = Place(m, 101, 1, sector: 1, z: -distance); s.SetWingmanAttackOrder(target);
        Call(s, "UpdateWingmanTravel", 1.0);
        Assert.Null(s.AttackTarget); Assert.Equal(follow, s.GotoWaypoint is not null);
        if (follow) Assert.Equal(new GotoOrder(GotoKind.Cluster, 1, Vector3.Zero), s.GotoWaypoint);
    }

    [SkippableFact]
    public void Goto_reacquires_visible_accepted_attack_and_temporary_aggression_is_consumed()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); var target = Place(m, 101, 1, z: -300);
        s.SetWingmanAttackOrder(target); s.GotoWaypoint = new(GotoKind.Cluster, 1, Vector3.Zero); s.AttackTarget = null;
        Call(s, "UpdateWingmanTravel", 1.0); Assert.Same(target, s.AttackTarget); Assert.Null(s.GotoWaypoint);
        s.SetGoto(new(GotoKind.Waypoint, 0, new(1000, 0, 0))); s.WingmanBehaviour = 0x80;
        Write(s, "_acceptedWingmanAttackTarget", target); // accepted target invisible to the scanner below
        target.SideVisibility.SetSideVisibility(0, false); s.ChangeHull(s.HullType with { ScannerRange = 0 });
        var other = Place(m, 102, 1, z: -500); other.SideVisibility.SetSideVisibility(0, true);
        Call(s, "UpdateWingmanTravel", 20.0); Assert.Same(other, s.AttackTarget); Assert.Equal(0, s.WingmanBehaviour & 0x80);
    }

    [SkippableFact]
    public void Mining_controls_face_roll_kill_throttle_and_clear_all_motion_and_fire_inputs()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m, pilot: Ship.PilotTypeMiner);
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with { Position = [100, 0, 0] });
        s.StateM = ShipControlState.SelectedWeaponOne | ShipControlState.MiningMask | ShipControlState.WeaponsMask |
            ShipControlState.MoveButtonsMask | ShipControlState.AfterburnerButton | ShipControlState.MissileFire;
        Call(s, "ApplyMiningControls", rock, .05f);
        Assert.Equal(-1, s.Controls.Throttle); Assert.Equal(1, s.Controls.Roll); Assert.NotEqual(0, s.Controls.Yaw);
        Assert.Equal(ShipControlState.SelectedWeaponOne | ShipControlState.MiningMask, s.StateM);
    }

    [SkippableFact]
    public void Npc_tick_builds_lock_fires_real_missile_consumes_one_round_and_observes_reload()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s);
        var missile = m.Core.MissileTypes.Values.First() with { MaxLock = 1, LockTime = .1f, LockAngle = 1, Lifespan = 20, InitialSpeed = 1000, LoadTime = 10 };
        ((IDictionary<short, DataMissileTypeIGC>)m.Core.MissileTypes)[missile.ExpendableTypeId] = missile;
        var launcher = s.MountPart(Part(new DataPartTypeIGC.MissileLauncher(missile.ExpendableTypeId)), 2, 0, 240)!;
        var target = Place(m, 101, 1, z: -500); target.SideVisibility.SetSideVisibility(0, true);
        s.SetWingmanAttackOrder(target);
        for (int i = 1; i <= 60; i++) s.Update(i * .05);
        Assert.Single(s.Sector!.Models.OfType<Missile>());
        Assert.Equal(1, launcher.Amount);
        Assert.Same(target, s.CommandTarget);
    }

    [SkippableFact]
    public void Priority_target_search_uses_ship_order_before_probes_and_probes_are_real_combat_targets()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.WingmanBehaviour = 0x80;
        var probe = new Probe(200, s.Sector!, m.Core.ProbeTypes.Values.First() with { Signature = 1 }, new(0, 0, -200), 0, 1);
        s.Sector!.Add(probe); probe.SideVisibility.SetSideVisibility(0, true);
        var ship = Place(m, 101, 1, z: -500); ship.SideVisibility.SetSideVisibility(0, true);
        s.SetGoto(new(GotoKind.Waypoint, 0, new(1000, 0, 0)));
        Call(s, "UpdateWingmanTravel", 10.0); Assert.Same(ship, s.AttackTarget);
        s.SetGoto(new(GotoKind.Waypoint, 0, new(1000, 0, 0))); ship.SetFraction(0); s.WingmanBehaviour = 0x80;
        Call(s, "UpdateWingmanTravel", 20.0); Call(s, "UpdateWingmanCombat", .05f);
        Assert.Same(probe, s.CommandTarget); Assert.NotEqual(0, s.StateM & ShipControlState.AllWeapons);
        s.SetGoto(new(GotoKind.Waypoint, 0, new(0, 1000, 0)));
        Assert.Null(Read<Probe?>(s, "_attackProbeTarget"));
    }

    [SkippableTheory]
    [InlineData(9.999, false)] [InlineData(10.0, true)]
    public void Long_range_retarget_observes_ten_second_gate(double now, bool retarget)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s); s.WingmanBehaviour = 0x40;
        var distant = Place(m, 101, 1, z: -10000); s.AttackTarget = distant;
        var near = Place(m, 102, 1, z: -300);
        int state = Range(s, distant, 15, 10, now: now);
        Assert.Same(retarget ? near : distant, s.AttackTarget);
        Assert.Equal(retarget ? 0 : ShipControlState.AfterburnerButton, state);
    }

    [SkippableFact]
    public void Safe_ripcord_retry_waits_five_seconds_sets_indicator_stops_mining_and_clears_on_cancel()
    {
        Require.Artwork(); var site = new RecordingIgcSite(); var m = Domain(site); var s = Place(m);
        var type = m.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord));
        var data = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 1, 0, 200,
            type.StationTypeId, 1, 1, "Rip anchor");
        m.PlaceStation(new Station(data, type), m.Sectors[1]);
        s.Velocity = new(0, 0, -100);
        var hazard = Place(m, 101, z: -50, pilot: Ship.PilotTypePlayer);
        Call(s, "TryRipcordOrDefer", (short)1, 10.0, "test");
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("RequestRipcord"));
        hazard.Position = new(10000, 0, 0); s.Velocity = Vector3.Zero;
        s.StateM |= ShipControlState.MiningMask; s.IsMining = true;
        Call(s, "TryRipcordOrDefer", (short)1, 14.999, "test");
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("RequestRipcord"));
        Call(s, "TryRipcordOrDefer", (short)1, 15.0, "test");
        Assert.Single(site.Calls, c => c.StartsWith("RequestRipcord"));
        Assert.False(s.IsMining); Assert.Equal(ShipControlState.DroneRip, s.StateM & (ShipControlState.DroneRip | ShipControlState.MiningMask));
        s.SetRipcordModel(null); Assert.Equal(0, s.StateM & ShipControlState.DroneRip);
    }

    [SkippableTheory]
    [InlineData(true, false, false)] [InlineData(true, true, true)] [InlineData(false, false, false)]
    public void Ripcord_safety_respects_allied_drone_id_and_mining_right_of_way(bool friendly, bool mining, bool blocked)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m, id: 100); s.Velocity = new(0, 0, -100);
        var other = Place(m, 99, friendly ? (short)0 : (short)1, z: -50);
        if (mining) other.StateM |= ShipControlState.MiningMask;
        Assert.Equal(!blocked, s.IsSafeToRipcord());
    }

    [SkippableFact]
    public void Player_tick_keeps_wire_controls_and_weapon_inputs()
    {
        Require.Artwork(); var m = Domain(); var s = Place(m, pilot: Ship.PilotTypePlayer, npc: false);
        var controls = new ControlData(.1f, .2f, .3f, .4f); s.Controls = controls;
        s.StateM = ShipControlState.ForwardButton | ShipControlState.SelectedWeaponOne;
        s.Update(.05);
        Assert.Equal(controls, s.Controls);
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.SelectedWeaponOne, s.StateM);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Replacement_defend_or_station_order_forgets_the_previous_accepted_attack(bool station)
    {
        Require.Artwork(); var m = Domain(); var s = Place(m); Gun(s);
        var enemy = Place(m, 101, 1, z: -500); s.SetWingmanAttackOrder(enemy);
        if (station)
            s.PlanTarget = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 0, 0, 200,
                m.Core.StationTypes.Keys.First(), 1, 1, "Return order");
        else s.DefendTarget = Place(m, 102);
        Assert.Null(Read<Allegiance.Core.Sim.Model?>(s, "_acceptedWingmanAttackTarget"));
        Assert.Null(s.AttackTarget);
        s.ChangeHull(s.HullType with { ScannerRange = 0 });
        Clock(s, 10); Call(s, "UpdateWingmanCombat", .05f);
        Clock(s, 16); Call(s, "UpdateWingmanCombat", .05f);
        Assert.Null(s.GotoWaypoint); // old attack must not replace the new order with investigation
    }
}
