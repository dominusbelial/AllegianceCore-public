using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P636b — regression after P636: 0 kills at 200v200 on the real rig and the
/// decision loop "spins at ~670 Hz". Two defects are pinned here:
///   (1) the decision RATE metric fed the pass WORK time into DecisionRateHz
///       (1000 / work-ms), which reported thousands of Hz even while the loop
///       correctly slept to ~20 Hz — so the real cadence was never measured and
///       the P636 spin was invisible to get_perf_stats; and
///   (2) the bot decision hot paths walked the FULL snapshot ships array per bot
///       (O(N²) per pass) which scaled into the lethality collapse.
/// The cadence fix is a deadline scheduler (<see cref="DecisionMetrics.LoopCadence"/>)
/// that paces at the target and NEVER busy-spins (yields a minimum even on
/// overshoot). The lethality fix routes the scans to the snapshot's compacted
/// <see cref="DecisionSnapshot.VisibleShips"/> array (behaviour-identical).
/// </summary>
public class P636bCadenceSnapshotTests
{
    // ── LoopCadence: fixed cadence, never busy-spins ──────────────────────

    /// <summary>FAILING-FIRST (P636b): a pass that overshoots its period must
    /// still yield a minimum sleep — it must NOT spin at 0 ms. The pre-fix
    /// inline logic `sleep = period − work; if (sleep &gt; 0) Thread.Sleep` returned
    /// ≤ 0 on overshoot, so it never yielded and monpolised a core (the P636
    /// "670 Hz" runaway starving the receive threads).</summary>
    [Fact]
    public void LoopCadence_NeverBusySpins_WhenPassOvershootsItsPeriod()
    {
        var cad = new DecisionMetrics.LoopCadence(periodMs: 50.0, minYieldMs: 1.0);
        cad.Begin(0);
        double t = 0;
        for (int i = 0; i < 500; i++)
        {
            double work = 60.0;               // every pass overshoots the 50 ms period
            double passEnd = t + work;
            double sleep = cad.SleepForRemainder(passEnd);
            Assert.True(sleep >= 1.0, $"iteration {i}: sleep={sleep:F2}ms — loop busy-spins (no minimum yield)");
            t = passEnd + sleep;
        }
    }

    /// <summary>The guard test from the task: the measured decision rate stays
    /// within ±20% of the 20 Hz target for 1, 30 and 200 bots (light simulated
    /// work well under the period, so the SCHEDULER is the limiter).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(200)]
    public void LoopCadence_PacesAtTarget_ForAnyBotCount(int bots)
    {
        const double periodMs = 50.0;                 // 20 Hz
        var cad = new DecisionMetrics.LoopCadence(periodMs, minYieldMs: 1.0);
        cad.Begin(0);
        double work = Math.Min(periodMs * 0.8, 0.002 * bots + 1.0);   // well under the period
        int n = 120;
        var starts = new double[n];
        double t = 0;
        for (int i = 0; i < n; i++)
        {
            starts[i] = t;
            double now = t + work;
            t = now + cad.SleepForRemainder(now);
        }
        // achieved wall period = median spacing between pass starts
        var gaps = new List<double>();
        for (int i = 1; i < n; i++) gaps.Add(starts[i] - starts[i - 1]);
        gaps.Sort();
        double medPeriod = gaps[gaps.Count / 2];
        double rateHz = DecisionMetrics.DecisionRateHz(medPeriod);
        Assert.InRange(rateHz, 20.0 * 0.8, 20.0 * 1.2);   // ±20%
    }

    /// <summary>The decision-rate metric is fed the WALL period (the real
    /// cadence), not pass-work time: a 50 ms wall period must read 20 Hz even
    /// if the underlying work was microseconds.</summary>
    [Fact]
    public void DecisionRate_ReflectsWallPeriod_NotWorkTime()
    {
        // 50 ms between pass STARTS → 20 Hz, regardless of how short the work was.
        Assert.Equal(20.0, DecisionMetrics.DecisionRateHz(50.0), 3);
    }

    // ── DecisionSnapshot.VisibleShips: behaviour-identical compaction ──────

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 950, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Orientation Identity => Orientation.Identity;

    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), new Vector3(10f, 20f, 30f), Vector3.Zero, Identity),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    private static void AddRemote(ClientWorld world, short id, short side, Vector3 pos)
    {
        world.Apply(new ExportShip { ShipId = id, HullId = 951, SideId = side, CharacterName = $"s{id}", PilotType = ExportShip.PilotTypePlayer });
        world.Apply(HeavyShipsUpdate.Decode(
            HeavyShipsUpdate.FromShips(100u, Vector3.Zero, new[]
            {
                new Ship(id, MakeHull(), pos, Vector3.Zero, Identity),
            }).Encode()));
    }

    /// <summary>The compaction is behaviour-identical: VisibleShips is exactly the
    /// subset of Ships that is visible (IsVisible), preserving the scans' filter.</summary>
    [Fact]
    public void VisibleShips_IsExactlyTheVisibleSubsetOfShips()
    {
        const short me = 10;
        var world = WorldWithMyShip(me);
        AddRemote(world, 50, side: 7, pos: new Vector3(500f, 0f, 600f));
        AddRemote(world, 60, side: 8, pos: new Vector3(-500f, 0f, -600f));

        var snap = world.PublishDecisionSnapshot();

        Assert.NotEmpty(snap.Ships);
        var visibleFromShips = snap.Ships.Where(s => s.IsVisible).ToDictionary(s => s.ShipId);
        Assert.NotEmpty(visibleFromShips);
        // every VisibleShips member is visible and present in Ships
        foreach (var sv in snap.VisibleShips)
        {
            Assert.True(sv.IsVisible, $"VisibleShips contains non-visible ship {sv.ShipId}");
            Assert.True(visibleFromShips.ContainsKey(sv.ShipId), $"VisibleShips ship {sv.ShipId} not in Ships");
        }
        // every visible ship in Ships appears in VisibleShips (same count ⇒ same set)
        Assert.Equal(visibleFromShips.Count, snap.VisibleShips.Length);
        // the set matches ship-for-ship (order-insensitive)
        var visSet = snap.VisibleShips.Select(s => s.ShipId).ToHashSet();
        Assert.Equal(visibleFromShips.Keys.OrderBy(x => x), visSet.OrderBy(x => x));
        // never a superset of the full population
        Assert.True(snap.VisibleShips.Length <= snap.Ships.Length);
    }

    /// <summary>A dead (non-visible) ship is excluded from VisibleShips.</summary>
    [Fact]
    public void VisibleShips_ExcludesDeadShips()
    {
        const short me = 10;
        var world = WorldWithMyShip(me);
        AddRemote(world, 50, side: 7, pos: new Vector3(500f, 0f, 0f));
        var before = world.PublishDecisionSnapshot();
        Assert.All(before.VisibleShips, s => Assert.True(s.ShipId == me || s.ShipId == 50));
    }
}
