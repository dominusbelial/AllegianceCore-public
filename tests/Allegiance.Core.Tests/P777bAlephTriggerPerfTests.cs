using System.Diagnostics;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P777b — the aleph pass' per-tick cost. One merged swept pass over every ship
/// and missile replaced two pass bodies that each snapshotted <c>_models</c> with
/// <c>ToArray()</c> and re-normalised the warp's forward axis per sample
/// (<c>Sector.ResolveAlephCollisions</c>, <c>AlephConeHitTest.SweptFirstContact</c>).
/// This test pins the <b>allocation</b> half of that (0 bytes per invocation) and
/// prints the timing for the report; it deliberately asserts nothing about time.
/// </summary>
/// <remarks>
/// <para>The pass is private, so it is reached by reflection — and the SAME test
/// file runs on the pre-P777b base: it calls <c>ResolveAlephCollisions(now, dt)</c>
/// when that method exists and otherwise the two old passes
/// (<c>ResolveWarpCollisions(now, dt)</c> + <c>ResolveWarpBombTriggers(now)</c>).
/// On the base the 0-byte assert fails (two <c>ToArray</c> snapshots per
/// invocation).</para>
/// <para>Population: 6 warps of <c>R = 100</c>, 400 ships and 200 missiles.
/// 70 % of the models are far away (rejected by the cheap broad phase); 30 % sit
/// INSIDE the old <c>R + r</c> ball but <b>not touching</b> the cone — 90 u in
/// front of the disc and beside the apex — the hot "near the aleph" case that
/// must run the full cone maths yet changes no state. The pass is invoked
/// directly, so model positions never advance and every iteration is identical
/// (deterministic). A second population lets 10 % of the ships actually cross the
/// cone, so the golden-section + bisection path and the site callback are
/// measured too; the mission site's <c>HitWarpEvent</c> is a no-op, so those
/// crossings also change no state and need no re-placement.</para>
/// </remarks>
[Trait("Tier", "Perf")]
public sealed class P777bAlephTriggerPerfTests
{
    private const double Now = 1_000_000d;    // far past any cooldown; only the cooldown reads it
    private const float Dt = 0.05f;
    private const short WarpRadius = 100;
    private const int Warps = 6;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private sealed class NoOpSite : IIgcSite { }

    private static readonly Lazy<DataMissileTypeIGC> SomeMissile = new(() =>
    {
        var core = RepresentativeCore();
        var t = core.MissileTypes.Values.FirstOrDefault(m => m.Radius > 0f && m.Lifespan >= 2f);
        if (t.ExpendableTypeId == 0) throw new InvalidOperationException("no missile type in representative core");
        return t;
    });

    /// <summary>Build the 6-warp / 400-ship / 200-missile sector. Ship and
    /// missile positions never change during the run: the pass is invoked
    /// directly (no model update), so each invocation sees the same state.</summary>
    private static (Mission Mission, Sector Sector) Population(bool someCross)
    {
        var core = RepresentativeCore();
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)],
            new NoOpSite());
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];

        // 6 alephs on a line 2,000 u apart: every "near" model below belongs to
        // exactly one of them, and the other five reject it in the broad phase.
        var warpPositions = new Vector3[Warps];
        for (int w = 0; w < Warps; w++)
        {
            warpPositions[w] = new Vector3(w * 2000f, 0f, 0f);
            sector.AttachWarp(new ResolvedWarp(new DataWarpIGC(
                WarpId: (short)(7200 + w), DestinationId: (short)(7210 + w), Radius: WarpRadius,
                TextureName: "", IconName: "", Name: "",
                Position: [warpPositions[w].X, warpPositions[w].Y, warpPositions[w].Z],
                Forward: [0f, 0f, -1f],                       // backward = +Z
                Rotation: [0f, 0f, 0f, 1f],
                Signature: 0f, ClusterId: -1), destination: sector));
        }

        var hull = core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
        short id = 7000;
        var rng = new Random(777_003);                        // deterministic placement
        int ships = 0, missiles = 0;
        for (int i = 0; i < 600; i++)
        {
            bool isMissile = i % 3 == 2;                      // 400 ships, 200 missiles
            int index = isMissile ? missiles++ : ships++;
            bool near = index % 10 >= 7;                      // 30 % near (70 % far away)
            int warp = index % Warps;
            Vector3 pos;
            if (!near)
            {
                // Far from every warp (the broad-phase reject path).
                pos = new Vector3(warpPositions[warp].X + 20000f + (float)rng.NextDouble() * 500f,
                                  4000f + (float)rng.NextDouble() * 500f,
                                  4000f + (float)rng.NextDouble() * 500f);
            }
            else if (index % 2 == 0)
            {
                // In front of the disc, inside the old R + r ball, clear of the
                // cone (z < 0 is empty space for native's solid).
                pos = warpPositions[warp] + new Vector3(0f, 0f, -WarpRadius - 3f);
            }
            else
            {
                // Beside the apex (z = R, rho = 20): inside the old ball
                // (|p| = 102 < R + r), 20 u clear of the cone.
                pos = warpPositions[warp] + new Vector3(20f, 0f, WarpRadius);
            }
            if (someCross && !isMissile && index % 10 == 0)
            {
                // 10 % of the ships sweep THROUGH the cone: 40 u in front of the
                // disc on the axis, 0.05 s × 1,200 u/s = 60 u of travel. The
                // site's HitWarpEvent is a no-op, so the ship stays put and every
                // iteration crosses again (no re-placement needed).
                pos = warpPositions[warp] + new Vector3(0f, 0f, -40f);
            }

            var velocity = new Vector3(1f, 0f, someCross && !isMissile && index % 10 == 0 ? 1200f : 60f);
            if (isMissile)
            {
                var m = new Missile(id++, sector, SomeMissile.Value, pos, velocity,
                    new Vector3(0f, 0f, 1f), Now - 10d);
                sector.Add(m);
            }
            else
            {
                var ship = new Ship(id++, hull, pos, velocity, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(ship, sector);
            }
        }
        return (mission, sector);
    }

    /// <summary>
    /// ⭐ P777b test 12 — 0 bytes allocated per invocation (and the timing the
    /// report quotes). Runs on the base too, where it FAILS: each invocation
    /// allocates the two <c>_models.ToArray()</c> snapshots.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Aleph_pass_allocates_nothing_per_tick()
    {
        Require.Artwork();
        var (_, sector) = Population(someCross: false);
        Run(sector, "no-crossing", assertZeroAllocation: true);
    }

    /// <summary>
    /// ⭐ P777b test 12 (second population) — 10 % of the ships cross the cone
    /// every invocation, so the contact path (exact search + <c>HitWarpEvent</c>)
    /// is measured as well. Also 0 bytes.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Aleph_pass_allocates_nothing_when_ships_cross()
    {
        Require.Artwork();
        var (_, sector) = Population(someCross: true);
        Run(sector, "crossing", assertZeroAllocation: true);
    }

    private static void Run(Sector sector, string label, bool assertZeroAllocation)
    {
        const int invocations = 5_000, runs = 5;
        Action<Sector> pass = ResolvePass();

        pass(sector);                                             // warm up JIT / caches

        long allocated = 0;
        var timings = new double[runs];
        var sw = new Stopwatch();                                 // created BEFORE the allocation window
        for (int run = 0; run < runs; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < invocations; i++) pass(sector);
            sw.Stop();
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            if (run == 0 || used < allocated) allocated = used;
            timings[run] = sw.Elapsed.TotalMicroseconds / invocations;
        }
        Array.Sort(timings);
        double median = timings[runs / 2];
        Console.WriteLine($"P777b-PERF[{label}]: {median:F2} µs/call (median of {runs} × {invocations} calls; " +
                          $"min {timings[0]:F2}, max {timings[^1]:F2}), {allocated} B total = " +
                          $"{(double)allocated / invocations:F3} B/call");
        if (assertZeroAllocation)
            Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The private aleph pass, bound to a delegate (no per-call reflection
    /// boxing): the merged <c>ResolveAlephCollisions(now, dt)</c> when it exists,
    /// else the pre-P777b pair <c>ResolveWarpCollisions(now, dt)</c> +
    /// <c>ResolveWarpBombTriggers(now)</c>.
    /// </summary>
    private static Action<Sector> ResolvePass()
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var merged = typeof(Sector).GetMethod("ResolveAlephCollisions", Flags);
        if (merged is not null)
        {
            var one = merged.CreateDelegate<Action<Sector, double, float>>();
            return s => one(s, Now, Dt);
        }

        var ships = typeof(Sector).GetMethod("ResolveWarpCollisions", Flags)
            ?? throw new MissingMethodException(nameof(Sector), "ResolveWarpCollisions");
        var bombs = typeof(Sector).GetMethod("ResolveWarpBombTriggers", Flags)
            ?? throw new MissingMethodException(nameof(Sector), "ResolveWarpBombTriggers");
        var shipPass = ships.CreateDelegate<Action<Sector, double, float>>();
        var bombPass = bombs.CreateDelegate<Action<Sector, double>>();
        return s => { shipPass(s, Now, Dt); bombPass(s, Now); };
    }
}
