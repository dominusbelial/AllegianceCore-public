using System.Buffers.Binary;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>Every class that touches the process-wide WeaponCoverage.Global
/// counters must share one xUnit collection so BeginRun resets never race a
/// concurrent reader (the live loopback tests read the same singletons).</summary>
[CollectionDefinition("P877Coverage")]
public sealed class P877CoverageCollection;

/// <summary>P877 — bot minelaying tactics: escape mining (a pursued bot shakes
/// a pursuer with mines dropped in its wake) and arrival-aleph mine fields.
/// Pure decision tests, real-core data checks, dispenser-law wiring tests and
/// the WeaponCoverage counter pipeline. The live loopback proof (a chased
/// bot's mine damaging its pursuer on the running server) lives in
/// <see cref="P877MineDamageLiveTests"/>.</summary>
public sealed class P877DecisionTests
{
    private static readonly Vector3 Forward = new(0, 0, -1);   // identity orientation nose

    // ── pure pursuit geometry ───────────────────────────────────────────────

    [Fact]
    public void PursuitOpeningSpeed_classifies_closing_holding_and_opening()
    {
        // Pursuer behind, flying toward the bot: gap closing (negative).
        float closing = Showcase.PursuitOpeningSpeed(
            new Vector3(0, 0, 1000), new Vector3(0, 0, -200),   // pursuer at +Z flying −Z
            Vector3.Zero, Vector3.Zero);
        Assert.Equal(-200f, closing, 2);

        // Matched speeds along the line: holding (zero).
        float holding = Showcase.PursuitOpeningSpeed(
            new Vector3(0, 0, 1000), new Vector3(0, 0, 100),
            Vector3.Zero, new Vector3(0, 0, 100));
        Assert.Equal(0f, holding, 3);

        // Pursuer flying away: gap opening (positive) — a wake drop is wasted.
        float opening = Showcase.PursuitOpeningSpeed(
            new Vector3(0, 0, 1000), new Vector3(0, 0, 200),
            Vector3.Zero, Vector3.Zero);
        Assert.Equal(200f, opening, 2);

        // Degenerate contact: not opening.
        Assert.Equal(0f, Showcase.PursuitOpeningSpeed(Vector3.Zero, new Vector3(9, 9, 9), Vector3.Zero, Vector3.Zero), 3);
    }

    [Fact]
    public void IsPursuitBehind_rear_hemisphere_only()
    {
        Assert.True(Showcase.IsPursuitBehind(Forward, new Vector3(0, 0, 100)));    // dead astern
        Assert.True(Showcase.IsPursuitBehind(Forward, new Vector3(50, 0, 100)));   // rear quarter
        Assert.False(Showcase.IsPursuitBehind(Forward, new Vector3(0, 0, -100)));  // dead ahead
        Assert.False(Showcase.IsPursuitBehind(Forward, new Vector3(100, 0, -50))); // forward quarter
        Assert.False(Showcase.IsPursuitBehind(Forward, new Vector3(100, 0, 0)));   // abeam is not the rear hemisphere
    }

    [Fact]
    public void ShouldEscapeMine_pursued_ahead_and_state_gates()
    {
        // The pursued case: behind, in range, closing, and in danger → drop.
        Assert.True(Showcase.ShouldEscapeMine(pursuerBehind: true, distance: 800, openingSpeed: -50,
            fleeing: true, lowHull: false, lowShield: false, outmatched: false));

        // An enemy AHEAD never qualifies, whatever its state.
        Assert.False(Showcase.ShouldEscapeMine(pursuerBehind: false, distance: 100, openingSpeed: -50,
            fleeing: true, lowHull: true, lowShield: true, outmatched: true));

        // Behind but out of mine-useful range: no drop.
        Assert.False(Showcase.ShouldEscapeMine(true, distance: Showcase.PursuitMineRange, openingSpeed: -50,
            fleeing: true, lowHull: false, lowShield: false, outmatched: false));

        // Behind, in range, but opening fast (the pursuer is falling behind): no drop.
        Assert.False(Showcase.ShouldEscapeMine(true, 800, Showcase.PursuitOpeningTolerance,
            fleeing: true, lowHull: false, lowShield: false, outmatched: false));
        Assert.True(Showcase.ShouldEscapeMine(true, 800, Showcase.PursuitOpeningTolerance - 0.001f,
            fleeing: true, lowHull: false, lowShield: false, outmatched: false));

        // Behind and closing, but a healthy bot with local parity: no drop.
        Assert.False(Showcase.ShouldEscapeMine(true, 800, -50,
            fleeing: false, lowHull: false, lowShield: false, outmatched: false));

        // Each danger signal qualifies on its own.
        Assert.True(Showcase.ShouldEscapeMine(true, 800, -50, fleeing: false, lowHull: true, lowShield: false, outmatched: false));
        Assert.True(Showcase.ShouldEscapeMine(true, 800, -50, fleeing: false, lowHull: false, lowShield: true, outmatched: false));
        Assert.True(Showcase.ShouldEscapeMine(true, 800, -50, fleeing: false, lowHull: false, lowShield: false, outmatched: true));
    }

    [Fact]
    public void PreLayMineAtArrivalAleph_lifespan_thresholds()
    {
        // Field-grade: the 600 s Caltrop outlives the wait; the 21 s prox does not.
        Assert.True(Showcase.PreLayMineAtArrivalAleph(atArrivalAleph: true, threatenedOrContested: true, mineLifespan: 600f));
        Assert.False(Showcase.PreLayMineAtArrivalAleph(true, true, 21f));
        Assert.False(Showcase.PreLayMineAtArrivalAleph(true, true, Showcase.PreLaidMineMinLifespan - 0.001f));
        // Gate prerequisites.
        Assert.False(Showcase.PreLayMineAtArrivalAleph(atArrivalAleph: false, threatenedOrContested: true, 600f));
        Assert.False(Showcase.PreLayMineAtArrivalAleph(true, threatenedOrContested: false, 600f));
    }

    // ── real-core data: the P877 mine table + field-grade launcher ─────────

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Mine_data_table_and_field_grade_launcher_resolution()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        // The verified PCore001 table (identical in every artwork core): the
        // port's lifespan decode was correct — native consumes the value as
        // seconds (mineigc.cpp:113, zlib/ztime.h:149-153).
        var byName = core.MineTypes.Values.ToDictionary(m => m.LauncherName);
        Assert.Equal(5f, byName["Mine Pack 1"].Lifespan);
        Assert.Equal(5f, byName["Mine Pack 2"].Lifespan);
        Assert.Equal(15f, byName["Prox Mine 1"].Lifespan);
        Assert.Equal(18f, byName["Prox Mine 2"].Lifespan);
        Assert.Equal(21f, byName["Prox Mine 3"].Lifespan);
        Assert.Equal(18f, byName["EMP Mine 1"].Lifespan);
        Assert.Equal(21f, byName["EMP Mine 2"].Lifespan);
        Assert.Equal(600f, byName["Caltrop Mine"].Lifespan);
        Assert.Equal((short)10, byName["Caltrop Mine"].LauncherExpendableSize);
        Assert.Equal(200f, byName["Caltrop Mine"].MineRadius);
        Assert.Equal(400f, byName["Caltrop Mine"].Power);
        Assert.Equal(75f, byName["Mine Pack 1"].MineRadius);

        // Exactly one field-grade launcher on this core: the Caltrop (part 830).
        var fieldGrade = core.Launchers.Values.Where(l => Showcase.IsFieldGradeMine(core, l.PartId))
            .Select(l => l.PartId).OrderBy(id => id).ToList();
        Assert.Equal([830], fieldGrade);

        // The 60 s floor cleanly separates the data: nothing between 21 s and 600 s.
        Assert.DoesNotContain(core.MineTypes.Values, m => m.Lifespan is > 21f and < 600f);

        // A side whose base techs carry the caltrop bits can buy the field mine
        // (PickAlephMineLauncher reads core data only — never a civ name/id);
        // sides that cannot stay dormant. Every civ's base-tech set is checked.
        var caltrop = byName["Caltrop Mine"];
        var caltropBits = TechTreeBitMask.FromBytes(caltrop.LauncherTtbmRequired);
        var fieldCivs = core.Civilizations.Values
            .Where(c => caltropBits.IsSubsetOf(TechTreeBitMask.FromBytes(c.TtbmBaseTechs))).ToList();
        Assert.NotEmpty(fieldCivs);
        foreach (var civ in fieldCivs)
            Assert.Equal((short)830, TechLoadoutSelector.PickAlephMineLauncher(core, TechTreeBitMask.FromBytes(civ.TtbmBaseTechs)));
        Assert.True(core.Civilizations.Values.Any(c =>
            TechLoadoutSelector.PickAlephMineLauncher(core, TechTreeBitMask.FromBytes(c.TtbmBaseTechs)) is null),
            "at least one civ cannot buy a field-grade mine (duty stays dormant)");
    }
}

/// <summary>P877 — the dispenser law wiring: escape drops replace the native
/// undirected "enemy within 2500 u" trigger (an enemy merely AHEAD gets no
/// mines), and the coverage counters attribute mine damage to the escape /
/// aleph-field buckets.</summary>
[Collection("P877Coverage")]
public sealed class P877DispenserTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type BotType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;

    private static void Set(object obj, string member, object? value)
    {
        var t = obj.GetType();
        t.GetField(member, Fields)!.SetValue(obj, value);
    }
    private static T Get<T>(object obj, string member) => (T)obj.GetType().GetField(member, Fields)!.GetValue(obj)!;
    private static void SetProp(object obj, string name, object? value) =>
        obj.GetType().GetProperty(name, Fields)!.SetValue(obj, value);
    private static object? Call(string method, object? instance, params object?[] args)
    {
        var m = typeof(BotManager).GetMethod(method, BindingFlags.Static | BindingFlags.Instance | Fields)!;
        var parameters = m.GetParameters();
        instance ??= System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
        return m.Invoke(instance, args.Concat(parameters.Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
    }

    private static ServerShipUpdate Pose(short id, Vector3 pos, Vector3 vel, float hull = 0.2f, Orientation? orient = null)
        => ServerShipUpdate.FromValues(id, 0, new ControlData(0, 0, 0, 0), 100u,
            pos, orient ?? Orientation.Identity, vel, [0f, 0f, 0f],
            hull, 0.5f, 1f, 1f, 1f, 0f, 1f);

    private static void Seat(ClientWorld world, short id, short sector, Vector3 pos, Vector3 vel, float hull = 0.2f)
    {
        world.Apply(new ExportShip { ShipId = id, HullId = 410, SideId = 0, CharacterName = "p877" });
        world.Apply(new SetCluster { Ship = Pose(id, pos, vel, hull), SectorId = sector });
    }

    private static void Enemy(ClientWorld world, short id, Vector3 pos, Vector3 vel, float hull = 1f)
    {
        world.Apply(new ExportShip { ShipId = id, SideId = 1, HullId = 410 });
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, pos, vel, hull) });
        world.Apply(new ShipStatusMessage { ShipId = id, HullId = 410, SectorId = 1, StationId = -1 });
    }

    private static (object bot, ConnectionSession cs) MineBot(short partId = 59)
    {
        var cs = new ConnectionSession();
        SetProp(cs.World, "MyShipId", (short)1);
        Seat(cs.World, 1, 1, Vector3.Zero, Vector3.Zero);
        cs.World.Apply(new EnterGame());
        cs.World.Apply(new BuyLoadoutAck { HullId = 410, Parts = [new ExpandedPartData(partId, 5, 0, ExpandedPartData.FullyMounted)] });
        cs.World.PartTypeResolver = id => Catalog.RepresentativeCore().TryBuildExpendableLauncher(id, out var p) ? p : null;
        var bot = Activator.CreateInstance(BotType, Fields, null,
            ["p877", (short)410, "fighter", (short)0, (short)0, "combat", 0.0], null)!;
        Set(bot, "Cs", cs);
        Set(bot, "ShipId", (short)1);
        Set(bot, "SpawnPending", false);
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        return (bot, cs);
    }

    private static void Use(object bot, ConnectionSession cs, double now) =>
        Call("MaybeUseDispensers", null, bot, cs, now);

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Escape_mine_drops_behind_but_not_ahead_opening_or_out_of_range()
    {
        Require.Artwork();
        // Identity orientation faces (0,0,-1): an enemy at +Z is dead astern.
        var (bot, cs) = MineBot();
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));

        // AHEAD (+X, the nose points −Z): the native undirected trigger is gone.
        Enemy(cs.World, 9, new Vector3(2499, 0, 0), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));

        // Out of mine-useful range astern: no drop.
        Enemy(cs.World, 9, new Vector3(0, 0, 2500), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));

        // Behind, in range, holding (matched zero velocity): the wake drop.
        Enemy(cs.World, 9, new Vector3(0, 0, 2000), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(10, Get<double>(bot, "LastMineAt"));

        // Load-time pacing holds even under active pursuit.
        Use(bot, cs, 10.5);
        Assert.Equal(10, Get<double>(bot, "LastMineAt"));

        // Behind but OPENING fast (pursuer flying away): no further drop.
        var (bot2, cs2) = MineBot();
        Enemy(cs2.World, 9, new Vector3(0, 0, 2000), new Vector3(0, 0, 500));
        Use(bot2, cs2, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot2, "LastMineAt"));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Healthy_bot_with_parity_ignores_a_pursuer_that_the_low_hull_bot_mines()
    {
        Require.Artwork();
        // Same geometry, but the bot is healthy (hull 1.0) and not outmatched:
        // the danger gate fails → no drop.
        var (bot, cs) = MineBot();
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        cs.World.Apply(new SingleShipUpdate { OidTarget = 1, Ship = Pose(1, Vector3.Zero, Vector3.Zero, hull: 1f) });
        Enemy(cs.World, 9, new Vector3(0, 0, 2000), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));

        // Outmatched (a second enemy in sector) qualifies the same healthy bot.
        Enemy(cs.World, 10, new Vector3(100, 0, 2100), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(10, Get<double>(bot, "LastMineAt"));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Wingman_flag_and_coverage_still_gate_escape_drops()
    {
        Require.Artwork();
        var (bot, cs) = MineBot();
        Set(bot, "WingmanBehaviour", (short)0);   // native UseMines off
        Enemy(cs.World, 9, new Vector3(0, 0, 2000), Vector3.Zero);
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));

        // Own-side coverage right on top of us suppresses the drop too.
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        cs.World.Apply(new ExportMine { Mine = new() { MineId = 91, SideId = 0, ClusterId = 1, P0 = Vector3.Zero } });
        Use(bot, cs, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Coverage_counters_attribute_escape_and_aleph_mine_damage()
    {
        Require.Artwork();
        var coverage = WeaponCoverage.Global;
        coverage.BeginRun();

        // Mine blast damage raises NO wire event (only the projectile path
        // raises VICTIM_DAMAGED, Sector.cs:2401), so hits are observed on the
        // LAUNCHER's own world: its cross-side HEAVY_SHIPS_UPDATE stream
        // carries the victims' hull fractions. The launcher's world here sees
        // its pursuer (ship 1, side 1) at full health, then damaged.
        var launcherCs = new ConnectionSession();
        SetProp(launcherCs.World, "MyShipId", (short)7);
        Seat(launcherCs.World, 7, 1, Vector3.Zero, Vector3.Zero);
        launcherCs.World.Apply(new EnterGame());
        launcherCs.World.Apply(new ExportShip { ShipId = 1, SideId = 1, HullId = 410 });
        launcherCs.World.Apply(new SingleShipUpdate { OidTarget = 1, Ship = Pose(1, new Vector3(0, 0, 2000), Vector3.Zero, hull: 1f) });
        launcherCs.World.Apply(new ShipStatusMessage { ShipId = 1, HullId = 410, SectorId = 1, StationId = -1 });

        coverage.RecordEscapeMineDrop(launcherShipId: 7, pursuerShipId: 1,
            launcherPartId: 59, mineTypeId: 11, mineLifespan: 15f);
        Assert.Equal(1, coverage.EscapeMineDrops);
        Assert.Equal(0, coverage.AlephFieldDrops);

        coverage.SweepBot(launcherCs);          // first sight: baseline fraction
        Assert.Equal(0, coverage.EscapeMineHits);

        // The pursuer flies through the wake mine: its streamed hull drops.
        launcherCs.World.Apply(new SingleShipUpdate { OidTarget = 1, Ship = Pose(1, new Vector3(0, 0, 1500), Vector3.Zero, hull: 0.6f) });
        coverage.SweepBot(launcherCs);
        Assert.Equal(1, coverage.EscapeMineHits);

        // A kill of that pursuer inside the window counts once.
        launcherCs.World.Apply(new KillShip { ShipId = 1, BDeathCredit = true });
        coverage.SweepBot(launcherCs);
        Assert.Equal(1, coverage.EscapeMineKills);

        // Aleph-field drops take the other bucket: every enemy in the sector
        // is watched, so an exiting ship's hull decrease counts there.
        coverage.RecordAlephFieldDrop(launcherShipId: 7, launcherPartId: 830, mineTypeId: 1,
            mineLifespan: 600f, sector: 1, enemyShipIds: [2]);
        launcherCs.World.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(100, 0, 2100), Vector3.Zero, hull: 1f) });
        launcherCs.World.Apply(new ShipStatusMessage { ShipId = 2, HullId = 410, SectorId = 1, StationId = -1 });
        coverage.SweepBot(launcherCs);          // baseline for the second victim
        launcherCs.World.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(100, 0, 2050), Vector3.Zero, hull: 0.4f) });
        coverage.SweepBot(launcherCs);
        Assert.Equal(1, coverage.AlephFieldDrops);
        Assert.Equal(1, coverage.AlephFieldHits);
        Assert.Equal(1, coverage.EscapeMineKills);   // unchanged

        var lines = coverage.P877Lines();
        Assert.Contains("escape", lines[0]);
        Assert.Contains("aleph", lines[1]);
    }
}


/// <summary>P877 — the duty-bot arrival-field drop branch of the dispenser law:
/// a bot ON aleph-field duty carrying a field-grade mine (the 600 s Caltrop)
/// lays a field at its assigned arrival warp; the same geometry with a prox
/// mine (15 s) stays dormant, and so does the duty bot away from the warp.</summary>
[Collection("P877Coverage")]
public sealed class P877AlephFieldDropTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type BotType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;

    private static void Set(object obj, string field, object? value) =>
        obj.GetType().GetField(field, Fields)!.SetValue(obj, value);
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, Fields)!.GetValue(obj)!;
    private static object? Call(string method, params object?[] args)
    {
        var m = typeof(BotManager).GetMethod(method, BindingFlags.Static | BindingFlags.Instance | Fields)!;
        var parameters = m.GetParameters();
        return m.Invoke(null, args.Concat(parameters.Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
    }

    private static ServerShipUpdate Pose(short id, Vector3 pos, float hull = 1f)
        => ServerShipUpdate.FromValues(id, 0, new ControlData(0, 0, 0, 0), 100u,
            pos, Orientation.Identity, Vector3.Zero, [0f, 0f, 0f],
            hull, 0.5f, 1f, 1f, 1f, 0f, 1f);

    private static (object bot, ConnectionSession cs) DutyBot(short minePart)
    {
        var cs = new ConnectionSession();
        typeof(ClientWorld).GetProperty("MyShipId", Fields)!.SetValue(cs.World, (short)1);
        cs.World.Apply(new ExportShip { ShipId = 1, HullId = 410, SideId = 0, CharacterName = "p877f" });
        cs.World.Apply(new SetCluster { Ship = Pose(1, new Vector3(500, 0, 0)), SectorId = 1 });
        cs.World.Apply(new EnterGame());
        cs.World.Apply(new BuyLoadoutAck { HullId = 410, Parts = [new ExpandedPartData(minePart, 5, 0, ExpandedPartData.FullyMounted)] });
        cs.World.PartTypeResolver = id => Catalog.RepresentativeCore().TryBuildExpendableLauncher(id, out var p) ? p : null;
        cs.World.Apply(new ExportWarp { Warp = new(20, 21, 60, "", "", "gate", [500, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 1) });
        var bot = Activator.CreateInstance(BotType, Fields, null,
            ["p877-field", (short)410, "fighter", (short)0, (short)0, "combat", 0.0], null)!;
        Set(bot, "Cs", cs);
        Set(bot, "ShipId", (short)1);
        Set(bot, "SpawnPending", false);
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        Set(bot, "AlephFieldDuty", true);
        Set(bot, "AlephFieldWarpId", (short)20);
        Set(bot, "AlephFieldSector", (short)1);
        Set(bot, "MineDispenserPartId", (short)minePart);
        return (bot, cs);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Duty_bot_with_caltrop_lays_the_arrival_field_prox_stays_dormant()
    {
        Require.Artwork();
        // Caltrop (830, 600 s) at the assigned warp (the bot spawns 0 u from it):
        // the field drop fires and the aleph counter records it.
        var (bot, cs) = DutyBot(830);
        Call("MaybeUseDispensers", bot, cs, 10);
        Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        Assert.Equal(1, WeaponCoverage.Global.AlephFieldDrops);

        // Pacing holds while station-keeping on the field point.
        Call("MaybeUseDispensers", bot, cs, 10.5);
        Assert.Equal(10, Get<double>(bot, "LastMineAt"));

        // The identical geometry with the 15 s Prox Mine 1: the duty gate keeps
        // it dormant (only field-grade mines build arrival fields).
        var (prox, cs2) = DutyBot(59);
        Call("MaybeUseDispensers", prox, cs2, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(prox, "LastMineAt"));
        Assert.Equal(1, WeaponCoverage.Global.AlephFieldDrops);   // unchanged

        // Off duty (or assigned to another sector): no drop either.
        var (idle, cs3) = DutyBot(830);
        Set(idle, "AlephFieldDuty", false);
        Call("MaybeUseDispensers", idle, cs3, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(idle, "LastMineAt"));
        Set(idle, "AlephFieldDuty", true);
        Set(idle, "AlephFieldSector", (short)9);
        Call("MaybeUseDispensers", idle, cs3, 10);
        Assert.Equal(double.NegativeInfinity, Get<double>(idle, "LastMineAt"));
    }
}
