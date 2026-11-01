using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using V = Allegiance.Core.Collision.Vector3;
using N = System.Numerics.Vector3;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P911TrainingArenaTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public P911TrainingArenaTests(Xunit.Abstractions.ITestOutputHelper output)
    { _output = output; TrainingClock.ResetForTests(); }
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }
    private static TrainingMission Lesson(TrainingHost host, bool free)
    {
        TrainingMission lesson = free ? new Mission10(new TrainingHostSite(host)) : new Mission6(new TrainingHostSite(host));
        host.SetActiveMission(lesson);
        return lesson;
    }
    private static Ship Drone(TrainingHost host, int id, int side, V position, int pilot = 2, int hull = 315)
    {
        host.CreateDrone("arena test", id, hull, side, pilot, 0, -1, 1);
        host.PlaceDrone(id, 2080, new N(position.X, position.Y, position.Z), N.UnitX, N.UnitZ, null, 0);
        var ship = host.Drones[id];
        ship.SideVisibility.SetSideVisibility(0, true);
        ship.SideVisibility.SetSideVisibility(1, true);
        return ship;
    }
    private static Allegiance.Server.Server Server(TrainingHost host) =>
        (Allegiance.Server.Server)typeof(TrainingHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Native_selection_close_attack_and_armed_assistance_precede_ordinary_default(bool free)
    {
        Require.Artwork(); using var host = TrainingHost.Start(free ? TrainingHost.FreeFlight : TrainingHost.PracticeArena);
        var lesson = Lesson(host, free);
        host.PlayerShip.Position = new V(0, 0, 5000);
        var attacker = Drone(host, 7000, 1, new V(0, 0, 5000));
        var support = Drone(host, 7001, 1, new V(0, 50, 5000));
        var miner = Drone(host, 7002, 0, new V(800, 0, 5000), 0, 438);
        // Until >2 credits the nearer player must be ignored. A miner <1000 is close.
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(attacker));
        Assert.Same(miner, attacker.AttackTarget);
        Assert.Same(miner, support.AttackTarget);
        Assert.Equal(OrderChange.CidAttack, attacker.AcceptedShipCommandId);
        Assert.NotEmpty(attacker.Weapons);
        // The kill threshold itself is unit-tested here; live completion below injects no credits.
        for (int i = 0; i < 3; i++) lesson.ShipKilled(true);
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(attacker));
        Assert.Same(host.PlayerShip, attacker.AttackTarget);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Inherit_attack_or_distant_goto_and_temporary_aggression(bool free)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        Lesson(host, free);
        var actor = Drone(host, 7010, 1, new V(0, 0, 5000));
        var ally = Drone(host, 7011, 1, new V(100, 0, 5000));
        var foe = Drone(host, 7012, 0, new V(3000, 0, 5000));
        ally.SetWingmanAttackOrder(foe);
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        Assert.Same(foe, actor.AttackTarget);
        host.CreateWaypoint(7100, new N(2000, 0, 5000), 2080, DataBuoyIGC.CBuoyWaypoint, false);
        host.IssueCommand(ally.Id, TrainingMissionIds.CommandAccepted, null, 7100, null, OrderChange.CidGoto);
        ally.WingmanBehaviour |= 0x80;
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        Assert.Equal(OrderChange.CidGoto, actor.AcceptedShipCommandId);
        Assert.Same(host.Mission.GetBuoy(7100), actor.AcceptedShipTarget);
        Assert.Equal(0x80, actor.WingmanBehaviour & 0x80);
        // A navigation-created plan buoy can differ from the retained accepted order.
        ally.SetGoto(new GotoOrder(GotoKind.Waypoint, 2080, new V(2500, 0, 5000)));
        var actualPlan = ally.GetNpcPlan().Target;
        Assert.IsType<Buoy>(actualPlan);
        Assert.NotSame(ally.AcceptedShipTarget, actualPlan);
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        Assert.Same(actualPlan, actor.AcceptedShipTarget);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Sector_restrictions_hidden_pursuit_and_local_patrol(bool free)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        Lesson(host, free);
        var actor = Drone(host, 7020, 1, new V(0, 0, 5000));
        var foe = Drone(host, 7021, 0, new V(4000, 0, 5000));
        host.PlaceDrone(actor.Id, 2081, new N(0, 0, 5000), N.UnitX, N.UnitZ, null, 0);
        host.PlaceDrone(foe.Id, 2081, new N(4000, 0, 5000), N.UnitX, N.UnitZ, null, 0);
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        var buoy = Assert.IsType<Buoy>(actor.AcceptedShipTarget);
        Assert.False(buoy.Data.Visible); Assert.Equal(DataBuoyIGC.CBuoyCluster, buoy.Data.Type); Assert.Equal(2080, buoy.Data.ClusterId);
        // Empty home: now Mars attack is legal, even for a distant target.
        foreach (var ship in host.Mission.Sectors.First(s => s.Id == 2080).Models.OfType<Ship>().ToArray())
        { ship.Sector!.Remove(ship); host.Mission.PlaceShip(ship, host.Mission.Sectors.First(s => s.Id == 2082)); }
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor)); Assert.Same(foe, actor.AttackTarget);
        host.DestroyDrone(foe.Id);
        host.PlayerShip.StationId = host.HomeStationId; // no player cluster: patrol current sector
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        buoy = Assert.IsType<Buoy>(actor.AcceptedShipTarget);
        Assert.Equal(2081, buoy.Data.ClusterId); Assert.Equal(DataBuoyIGC.CBuoyWaypoint, buoy.Data.Type); Assert.False(buoy.Data.Visible);
        Assert.Equal(0x80, actor.WingmanBehaviour & 0x80);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Respawn_registry_mounts_mutated_recipes_retains_buoy_and_removes_recipes()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        var site = new TrainingHostSite(host); var lesson = Lesson(host, false);
        host.CreateWaypoint(7200, new N(3000, 0, 5000), 2080, DataBuoyIGC.CBuoyWaypoint, false);
        var recipe = new CreateDroneAction(site, "wave", 7201, 310, 1, 2);
        recipe.SetCreatedLocation(2080, new N(0, 0, 5000));
        recipe.SetCreatedCommand(7200, ObjectType.Buoy, TrainingMissionIds.CmdGoto);
        recipe.SetCreatedTechLevel(2); recipe.SetCreatedHullType(315);
        // Start is required to run the real registry; use a separate empty mission graph.
        var registry = new RegistryMission(site); registry.Start();
        registry.AddRespawn(recipe, 1, -1, false);
        Assert.Equal(1, host.BuoyConsumerCount(7200));
        for (int i = 0; i < 60; i++) { host.StepLight(); registry.Frame(); }
        var spawned = host.Drones[7201]; Assert.Equal(315, spawned.HullType.HullId);
        Assert.NotEmpty(spawned.Weapons); Assert.Contains(spawned.Parts, p => p.PartId == 71);
        Assert.Equal(2, host.BuoyConsumerCount(7200)); // recipe hold + accepted slot
        registry.RemoveRespawn(7201); Assert.Equal(1, host.BuoyConsumerCount(7200));
        host.DestroyDrone(7201);
        for (int i = 0; i < 60; i++) { host.StepLight(); registry.Frame(); }
        Assert.False(host.TrainingShipExists(7201));
    }
    private sealed class RegistryMission(ITrainingSite site) : TrainingMission(site)
    {
        public override int GetMissionID() => 6;
        public override int GetStartSectorId() => 2080;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new FalseCondition();
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Arena_loadout_tech_landing_and_restore(bool free)
    {
        Require.Artwork(); using var host = TrainingHost.Start(free ? TrainingHost.FreeFlight : TrainingHost.PracticeArena);
        var lesson = Lesson(host, free);
        var ship = host.PlayerShip;
        Assert.Equal(488, ship.HullType.HullId);
        Assert.NotEqual(0, ship.StateM & ShipControlState.AllWeapons);
        var magazine = Assert.Single(ship.Parts, p => p.EquipmentType == EquipmentType.Magazine);
        Assert.Equal(154, magazine.PartId); Assert.Equal(5, magazine.Amount);
        Assert.All(ship.CargoHold.Where(p => p?.EquipmentType == EquipmentType.Magazine), p =>
        { Assert.Equal(154, p!.PartId); Assert.Equal(5, p.Amount); });
        Assert.All(host.Mission.Sectors.SelectMany(s => s.Warps), w => Assert.True(w.SeenBySide(1)));
        var techs = host.Mission.Sides[0].DevelopmentTechs;
        foreach (short id in new short[] { 154, 104, 151, 14, 58, 232, 174, 39 })
        {
            var part = host.Mission.Core.PartTypes.TryGetValue(id, out var p) ? p
                : host.Mission.Core.TryBuildExpendableLauncher(id, out p) ? p : throw new InvalidOperationException();
            Assert.True(TechTreeBitMask.FromBytes(part.TtbmEffects).IsSubsetOf(techs));
            if (id is not (154 or 151)) Assert.True(TechTreeBitMask.FromBytes(part.TtbmRequired).IsSubsetOf(techs));
        }
        lesson.Start(); Assert.True(lesson.ShipLanded()); Assert.False(lesson.Frame());
        Assert.False(lesson.RestoreShip()); Assert.True(lesson.Frame());
    }



    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Arena_enemy_uses_revealed_warps_and_real_flight_to_reach_home()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        Lesson(host, false);
        Assert.All(host.Mission.Sectors.SelectMany(s => s.Warps), w => Assert.True(w.SeenBySide(1)));
        var actor = Drone(host, 7050, 1, new V(0, 0, 0));
        host.PlaceDrone(actor.Id, 2082, N.Zero, N.UnitX, N.UnitZ, null, 0);
        for (int i = 0; i < 6000 && actor.Sector?.Id != 2080 && !actor.Dead; i++) host.StepLight();
        Assert.False(actor.Dead);
        Assert.Equal((short?)2080, actor.Sector?.Id);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void No_training_site_has_no_policy_allocation_and_ordinary_default_still_acquires()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        var actor = Drone(host, 7030, 1, new V(0, 0, 500));
        var foe = Drone(host, 7031, 0, new V(300, 0, 500));
        Server(host).Host.TrainingDefaultOrder = null;
        // This is the exact production site, with its ordinary null callback.
        Assert.False(host.Mission.Site.HandlePickDefaultOrder(actor));
        host.StepLight(); Assert.Same(foe, actor.AttackTarget);
        var site = host.Mission.Site;
        for (int i = 0; i < 100; i++) site.HandlePickDefaultOrder(actor); // JIT warmup
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 10000; i++) site.HandlePickDefaultOrder(actor);
        double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / 10000;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        _output.WriteLine($"unbound production dispatch ns/call={ns:F2} allocated={allocated}; no policy scan/list/closure");
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Negative_tech_support_mounts_nanite_and_repairs_via_real_shots()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        Lesson(host, false);
        var target = Drone(host, 7040, 1, new V(300, 0, 700), hull: 304);
        target.SetFraction(0.5f);
        var recipe = new CreateDroneAction(new TrainingHostSite(host), "support", 7041, 385, 1, 2);
        recipe.SetCreatedLocation(2080, new N(0, 0, 700));
        recipe.SetCreatedTechLevel(-2); recipe.SetCreatedCommand(target.Id, ObjectType.Ship, TrainingMissionIds.CmdRepair);
        recipe.Execute(); var support = host.Drones[7041];
        Assert.Equal(190, support.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0).PartId);
        Assert.Same(target, support.RepairTarget);
        Assert.All(support.CargoHold, p => Assert.Equal(58, p!.PartId));
        for (int i = 0; i < 200 && target.Fraction <= 0.5f; i++)
        {
            target.Position = new V(300, 0, 700); target.Velocity = V.Zero;
            support.Position = new V(0, 0, 700); support.Velocity = V.Zero;
            var orientation = Orientation.Identity; orientation.Set(new V(1, 0, 0), new V(0, 0, 1));
            support.SetOrientation(orientation); host.StepLight();
        }
        Assert.True(target.Fraction > 0.5f, $"nanite target fraction {target.Fraction}");
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(false)] [InlineData(true)]
    public void Real_projectiles_kill_every_respawn_wave_through_49_credits(bool free)
    {
        CompleteArenaWithRealCombat(free, _output.WriteLine);
    }

    internal static void CompleteArenaWithRealCombat(bool free, Action<string>? write = null)
    {
        Require.Artwork(); using var host = TrainingHost.Start(free ? TrainingHost.FreeFlight : TrainingHost.PracticeArena);
        var lesson = Lesson(host, free);
        TrainingController.BeginMission(); lesson.Start();
        int shots = 0, hits = 0, deaths = 0, resupplies = 0;
        Server(host).Host.ProjectileCreated += _ => shots++;
        Server(host).Host.ProjectileHit += (_, _, _, _, _, _, _) => hits++;
        Server(host).KillCreditGranted += (_, _) => deaths++;
        var recipes = new HashSet<string>(); bool completed = false;
        // A deterministic firing-range driver supplies player aim and positioning only.
        // Hulls, shields, weapons, projectiles, damage, deaths and respawn recipes are real.
        // Existing StepLight advances fixed 50ms frames; no new speed/damage multiplier.
        for (int step = 0; step < 100000 && !completed; step++)
        {
            Assert.True(host.PlayerShip.Sector is not null,
                $"trainee died step={step} shots={shots} hits={hits} credits={lesson.GetKillCount()} goal={lesson.ActiveGoalPath}");
            foreach (var enemy in host.Drones.Values.Where(s => !s.Dead && s.SideId == 1 && s.PilotType == 2))
                recipes.Add($"{enemy.Id}:{enemy.HullType.HullId}:{string.Join(',', enemy.Parts.Select(p => p.PartId))}:{enemy.WingmanBehaviour}");
            if (P911ArenaFiringRange.PrepareFrame(host)) resupplies++;
            host.StepLight(); completed = lesson.Frame();
        }
        Console.WriteLine($"P911 free={free} shots={shots} hits={hits} deaths={deaths} credits={lesson.GetKillCount()} recipes={recipes.Count} goal={lesson.ActiveGoalPath}");
        var diagnostic = $"free={free} resupplies={resupplies} shots={shots} hits={hits} deaths={deaths} credits={lesson.GetKillCount()} recipes={recipes.Count} goal={lesson.ActiveGoalPath} ammo={host.PlayerShip.Ammo} weapons={string.Join(',', host.PlayerShip.Parts.Select(p => p.PartId))} enemies={string.Join(';', host.Drones.Values.Where(s => !s.Dead && s.SideId == 1).Select(s => $"{s.Id}:{s.Fraction}:{s.ShieldFraction}"))}";
        write?.Invoke(diagnostic);
        Assert.True(shots > 49 && hits >= 49 && deaths >= 49, diagnostic);
        Assert.Equal(49, lesson.GetKillCount()); Assert.True(completed, lesson.ActiveGoalPath);
        Assert.True(recipes.Count >= 15, $"Only {recipes.Count} recipe variants observed");
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Miner_preference_preserves_native_raw_against_previous_weighted_comparison()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        Lesson(host, false); host.PlayerShip.StationId = host.HomeStationId;
        var actor = Drone(host, 7080, 1, new V(0, 0, 1000));
        Drone(host, 7081, 0, new V(900, 0, 1000));
        var farther = Drone(host, 7082, 0, new V(1200, 0, 1000));
        // Native compares 1200 raw against the previous 900*1.5, then stores 1800.
        // A conventional weighted-minimum or unweighted-nearest rewrite fails this.
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        Assert.Same(farther, actor.AttackTarget);
        var miner = Drone(host, 7083, 0, new V(1300, 0, 1000), 0, 438);
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        Assert.Same(miner, actor.AttackTarget); // 1300 < previous weighted 1800
    }

}
