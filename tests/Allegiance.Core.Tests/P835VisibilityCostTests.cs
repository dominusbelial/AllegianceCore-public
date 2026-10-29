using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P835 — tests for the two clash-peak visibility-sweep changes:
/// <list type="bullet">
/// <item>the conservative occlusion pre-filter inside
/// <see cref="VisibilityMath.LineOfSightExist"/> (default-on, exact-result-preserving —
/// proved differentially against the unfiltered reference algorithm on randomized
/// scenes, including boundary-adjacent geometry), and</item>
/// <item>the native last-spotter short-circuit in
/// <see cref="Sector.ResolveCrossSideVisibility"/> — UNCONDITIONAL since P858
/// removed the rejected <c>sim.visibilitySpotterCache</c> flag (the rig A/B
/// showed no gain, but the identity rule is native behaviour:
/// FedSrv.CPP:10896-10903): seen flags identical to the full loop for every
/// input, spotter identity the native sticky one (FedSrv.CPP:11026) instead of
/// the pre-P835 re-pick of the first scanner in enumeration order.</item>
/// </list>
/// </summary>
public class P835VisibilityCostTests
{
    // ── 1. the LOS pre-filter is exact-result-preserving ─────────────────────

    /// <summary>
    /// The unfiltered reference: the pre-P835 <see cref="VisibilityMath.LineOfSightExist"/>
    /// body, verbatim (common.cpp:2324-2398) — no pre-filter. The shipped version must
    /// return the same boolean for every scene.
    /// </summary>
    private static bool ReferenceLineOfSight(Vector3 eye, VisibilityMath.SightBody target,
        IReadOnlyList<VisibilityMath.SightBody> asteroids)
    {
        Vector3 v12 = target.Position - eye;
        float lenSq12 = v12.LengthSquared();
        float overLen12 = 1.0f / MathF.Sqrt(lenSq12);
        Vector3 v12n = v12 * overLen12;
        float visibleAngle = MathF.Asin(target.Radius * overLen12);

        for (int i = 0; i < asteroids.Count; i++)
        {
            var obstacle = asteroids[i];
            Vector3 v13 = obstacle.Position - eye;
            float lenSq13 = v13.LengthSquared();
            if (lenSq13 < lenSq12)
            {
                float dot = v12n * v13;
                if (dot > 0.0f)
                {
                    float overLen13 = 1.0f / MathF.Sqrt(lenSq13);
                    float cosSep = dot * overLen13;
                    float radius3 = obstacle.Radius * 0.5f;
                    float covered = MathF.Asin(radius3 * overLen13);
                    float sep = MathF.Acos(cosSep);
                    float maxSep = sep + visibleAngle;
                    if (maxSep < covered)
                        return false;
                }
            }
        }
        return true;
    }

    private static (Vector3 eye, VisibilityMath.SightBody target, List<VisibilityMath.SightBody> asteroids) RandomScene(
        Random rng)
    {
        Vector3 eye = new(rng.NextSingle() * 4000 - 2000, rng.NextSingle() * 4000 - 2000, rng.NextSingle() * 2000 - 1000);
        // Target 10..6000 units out in a random direction.
        var dir = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1).Normalize();
        var target = new VisibilityMath.SightBody(eye + dir * (10f + rng.NextSingle() * 5990f),
            2f + rng.NextSingle() * 300f);
        var asteroids = new List<VisibilityMath.SightBody>();
        int n = rng.Next(0, 12);
        for (int k = 0; k < n; k++)
        {
            // Mix of random cloud positions and positions clustered near the
            // eye→target ray (the boundary-adjacent cases the pre-filter must
            // leave to the exact comparison).
            Vector3 pos;
            if (k % 3 == 0)
            {
                var along = 0.05f + rng.NextSingle() * 0.95f;   // between eye and target
                var off = (rng.NextSingle() * 2 - 1) * target.Radius * 3f;
                var side = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1).Normalize();
                pos = eye + dir * ((target.Position - eye).Length() * along) + side * off;
            }
            else
            {
                pos = eye + new Vector3(rng.NextSingle() * 8000 - 4000, rng.NextSingle() * 8000 - 4000,
                    rng.NextSingle() * 4000 - 2000);
            }
            asteroids.Add(new VisibilityMath.SightBody(pos, rng.NextSingle() * 900f));
        }
        return (eye, target, asteroids);
    }

    [Fact]
    public void LineOfSightExist_pre_filter_is_exact_result_preserving()
    {
        var rng = new Random(835835);
        int seenBlocked = 0, seenClear = 0;
        for (int i = 0; i < 6000; i++)
        {
            var (eye, target, asteroids) = RandomScene(rng);
            bool reference = ReferenceLineOfSight(eye, target, asteroids);
            bool actual = VisibilityMath.LineOfSightExist(eye, target, asteroids);
            if (reference != actual)
                Assert.Fail($"scene {i}: reference={reference} actual={actual} " +
                            $"eye={eye} target={target.Position}/{target.Radius}");
            if (!reference) seenBlocked++; else seenClear++;
        }
        // The corpus must exercise both outcomes (an all-clear or all-blocked
        // corpus would prove nothing about the filter's boundary handling).
        Assert.True(seenBlocked > 100 && seenClear > 100,
            $"degenerate corpus: blocked={seenBlocked} clear={seenClear}");
        Console.WriteLine($"P835-LOS: 6000 random scenes — pre-filter result == unfiltered reference " +
                          $"(blocked={seenBlocked}, clear={seenClear}).");
    }

    /// <summary>Adversarial: an asteroid sat exactly on the occlusion boundary (the
    /// covered angle equal to the separation+extent to within float rounding) must be
    /// decided by the exact comparison, never by the conservative pre-filter.</summary>
    [Fact]
    public void LineOfSightExist_boundary_case_matches_reference()
    {
        var rng = new Random(4242);
        int boundary = 0;
        for (int i = 0; i < 3000; i++)
        {
            var eye = new Vector3(0f, 0f, 0f);
            var dir = new Vector3(1f, 0f, 0f);
            float dist = 1000f;
            var target = new VisibilityMath.SightBody(eye + dir * dist, 20f);
            // Walk the asteroid across the occlusion boundary in ~1e-3-angle steps:
            // offset perpendicular to the ray around the covered-angle silhouette.
            float radius3 = 100f;
            float covered = MathF.Asin(radius3 / dist);
            float visibleAngle = MathF.Asin(target.Radius / dist);
            // separation angle at the boundary: sep = covered - visibleAngle (± jitter)
            float sep = covered - visibleAngle + (rng.NextSingle() * 2 - 1) * 2e-5f;
            float off = MathF.Tan(sep) * dist;
            var rock = new VisibilityMath.SightBody(eye + dir * (dist * (0.3f + 0.4f * rng.NextSingle()))
                                                    + new Vector3(0f, off, 0f), radius3 * 2f);
            bool reference = ReferenceLineOfSight(eye, target, new[] { rock });
            bool actual = VisibilityMath.LineOfSightExist(eye, target, new[] { rock });
            if (reference != actual) Assert.Fail($"scene {i}: reference={reference} actual={actual}");
            boundary++;
        }
        Assert.Equal(3000, boundary);
    }

    // ── 2. the (unconditional) last-spotter short-circuit ─────────────────────────

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    private static readonly DataHullTypeIGC ScannerHull = Hull("faohshcar");   // range 3600, sig 1.5
    private static readonly DataHullTypeIGC TargetHull = Hull("dreghpod");     // sig 0.5

    private static Mission NewStartedMission()
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(2).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value,
            new SideSpec[] { new(0, "Alpha", civs[0].CivilizationId), new(1, "Bravo", civs[1].CivilizationId) },
            new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>Pick a sector + base point 8000+ units beyond every real asteroid and
    /// station in it (the CrossSideVisibilitySweepTests convention): the test's own
    /// ships are the only scanners that can reach the scene.</summary>
    private static (Sector Sector, Vector3 Base) CleanBase(Mission m, float clearance = 8000f)
    {
        foreach (var sector in m.Sectors)
        {
            var content = new List<Vector3>();
            foreach (var a in sector.Asteroids)
                if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.Stations)
                if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
            float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
            float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
            return (sector, new Vector3(maxX + clearance, cy, cz));
        }
        throw new InvalidOperationException("no sector available in corpus map");
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, short side, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    private static void ReSweep(Mission m, double start)
    {
        for (int i = 0; i < Sector.c_nPassesPerUpdate; i++)
            m.Update(start + (i + 1) * 0.5);
    }

    /// <summary>
    /// Build the two-scanner scene (identical every call): scanner B ("B", side 0)
    /// is placed FIRST (so it precedes A in the sector's model order = the
    /// full loop's scan order), scanner A second; the side-1 target starts where ONLY
    /// A sees it (B is beyond its 1952 detection radius), then moves to where BOTH see.
    /// Runs one sweep per stage and returns the per-stage (seen, spotterId) pairs.
    /// </summary>
    private static List<(bool Seen, short SpotterId)> RunTwoScannerScene()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        // B first (earlier in the sweep's scanner list), then A.
        var b = NewShip(9601, ScannerHull, 0, basePos);                    // at +0
        var a = NewShip(9602, ScannerHull, 0, basePos + new Vector3(700f, 0f, 0f)); // at +700
        mission.PlaceShip(b, sector);
        mission.PlaceShip(a, sector);
        var target = NewShip(9603, TargetHull, 1, basePos + new Vector3(2000f, 0f, 0f));
        mission.PlaceShip(target, sector);
        // Detection radius faohshcar→dreghpod = 150 + 2 + 3600·0.5·gaScanRange
        // (CrossSideVisibilitySweepTests' derivation, ga divisors ≤ 1 → r in
        // [1500, 1952]). Stage 1: target 2000 from B (beyond any ga-scaled
        // radius, unseen by B), 1300 from A (decisively seen by A).

        var stages = new List<(bool, short)>();
        mission.Update(0.25);   // stage-1 sweep
        stages.Add((target.SeenBySide(0), target.SideVisibility.LastSpotterFor(0).ObjectId));

        target.Position = basePos + new Vector3(1400f, 0f, 0f);   // both see it now (≤ any ga-scaled radius)
        ReSweep(mission, 1.0);
        stages.Add((target.SeenBySide(0), target.SideVisibility.LastSpotterFor(0).ObjectId));

        target.Position = basePos + new Vector3(50_000f, 0f, 0f); // nobody sees it
        ReSweep(mission, 6.0);
        stages.Add((target.SeenBySide(0), target.SideVisibility.LastSpotterFor(0).ObjectId));

        return stages;
    }

    /// <summary>
    /// The short-circuit can only answer "seen" when the full loop would too
    /// (FedSrv.CPP:10896-10903) — it re-tests the recorded spotter, then falls
    /// back to the full scanner loop. Both observations of the same scene must
    /// agree with the analytic expectation at every stage: seen when a scanner
    /// is in range (stages 1 and 2), unseen when none is (stage 3).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spotter_short_circuit_seen_flags_match_the_full_loop()
    {
        Require.Artwork();
        var stages = RunTwoScannerScene();
        Assert.True(stages[0].Seen, "stage 1: only A is in range — seen");
        Assert.True(stages[1].Seen, "stage 2: both scanners in range — seen");
        Assert.False(stages[2].Seen, "stage 3: nobody in range — unseen");
        Console.WriteLine("P835-SPOTTER: seen flags match the analytic full-loop outcome at all three stages.");
    }

    /// <summary>
    /// Spotter identity — the native sticky rule (FedSrv.CPP:10896-10903,
    /// :11026 "Keep track of who spotted it"), UNCONDITIONAL since P858: once a
    /// scanner is recorded as the side's last spotter, the side KEEPS it while
    /// it still sees the target, instead of re-picking the first scanner in
    /// enumeration order every sweep. Stage 1: only A sees → A recorded.
    /// Stage 2: both see → A kept (sticky; B precedes A in scan order, so a
    /// re-pick would return B). Stage 3: nobody sees → cleared.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spotter_identity_is_sticky_like_native()
    {
        Require.Artwork();
        var stages = RunTwoScannerScene();
        Assert.True(stages[0].Seen);
        Assert.Equal(9602, stages[0].SpotterId);   // only A sees → A
        Assert.True(stages[1].Seen);
        Assert.Equal(9602, stages[1].SpotterId);   // sticky: A kept even though B is first in scan order
        Assert.False(stages[2].Seen);
        Assert.Equal(-1, stages[2].SpotterId);     // nobody sees → cleared

        Console.WriteLine($"P835-SPOTTER: stage1 spotter {stages[0].SpotterId}; stage2 sticky {stages[1].SpotterId} (B=9601 is first in scan order); stage3 cleared.");
    }
}
