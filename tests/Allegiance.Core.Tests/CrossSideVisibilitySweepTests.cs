using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P132 — the periodic cross-side detection sweep (<c>Sector.ResolveCrossSideVisibility</c>),
/// proved against real corpus hull/scanner data in a real mission sector whose geometry
/// the test fully controls (a base point pushed beyond every real asteroid and station in
/// the sector, so no ambient occluder or real same-sector scanner can skew a result). This
/// is the wiring that P131 deliberately left unwired: after a sector tick, the P131
/// primitives (SideVisibilityState + VisibilityMath.InScannerRange) now produce real,
/// live-updating SeenBySide answers. Style matches P131's own isolated-predicate tests but
/// drives the real Sector.Update path (ships are real <see cref="Ship"/>s in a real
/// <see cref="Mission"/>, not hand-built <c>SightBody</c>s).
/// </summary>
/// <remarks>
/// <para><b>Real bodies.</b> The scanner is a real <c>faohshcar</c> hull (Length 300 →
/// radius 150, ScannerRange 3600, Signature 1.5) and the target a real <c>dreghpod</c>
/// (Length 4 → radius 2, Signature 0.5) — the same pair P131 measured. Detection radius
/// faohshcar→dreghpod = 150 + 2 + 3600·0.5 = 1952 (global-attribute divisors 1.0, the
/// established P117/P119/P121/P124 precedent). A scanner/target 300 units apart is decisively
/// in range; 50,000 apart decisively out.</para>
/// <para><b>Ambient-noise control.</b> Two ships of different sides must show a clean
/// true→false transition, so the target is placed at a base point > 8000 units from every
/// real asteroid (occluder) and every real station (same-sector scanner) in the sector —
/// far beyond any station scanner range (~3900 worst case) — guaranteeing the only scanner
/// that can see it is the test's own ship.</para>
/// </remarks>
public class CrossSideVisibilitySweepTests
{
    private const short Side0 = 0;
    private const short Side1 = 1;
    private const short Side2 = 2;   // the scanner-less side in the 3-side test

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

    /// <summary>A real faohshcar scanner hull (the P131 scanner).</summary>
    private static readonly DataHullTypeIGC ScannerHull = Hull("faohshcar");

    /// <summary>A real dreghpod target hull (the P131 target).</summary>
    private static readonly DataHullTypeIGC TargetHull = Hull("dreghpod");

    /// <summary>A started mission with <paramref name="sides"/> real sides over a real map.</summary>
    private static Mission NewStartedMission(int sides)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(sides).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>
    /// Pick a sector and a base point 8000+ units beyond every real asteroid and station in
    /// it (a corner of the sector's content bounding box pushed out on +X). Guarantees the
    /// test's own ships are the only same-sector scanners that can reach the base, and that
    /// no real asteroid occludes the short scanner→target ray. Deterministic; throws only if
    /// the corpus map somehow has no sector at all (it always does).
    /// </summary>
    private static (Sector Sector, Vector3 Base) CleanBase(Mission m, float clearance = 8000f)
    {
        foreach (var sector in m.Sectors)
        {
            var content = new List<Vector3>();
            foreach (var a in sector.Asteroids)
                if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.Stations)
                if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));

            float cx = content.Count == 0 ? 0f : content.Average(v => v.X);
            float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
            float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
            float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
            var basePos = new Vector3(maxX + clearance, cy, cz);
            return (sector, basePos);
        }
        throw new InvalidOperationException("no sector available in corpus map");
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, short side, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    // ── Scenario 1: same side — mutual own-side visibility, no scan needed ──

    /// <summary>Two real ships of the SAME side see each other without any scan — the
    /// native "own side always visible" leg (FedSrv.CPP:10890-10892). They are placed
    /// 50,000 apart (far beyond any scanner range) to prove the result is the own-side
    /// rule, not a range computation.</summary>
    [Fact]
    public void Same_side_ships_are_mutually_visible_without_a_scan()
    {
        var mission = NewStartedMission(2);
        var (sector, basePos) = CleanBase(mission);

        var a = NewShip(9101, ScannerHull, Side0, basePos);
        var b = NewShip(9102, ScannerHull, Side0, basePos + new Vector3(50_000f, 0f, 0f));
        mission.PlaceShip(a, sector);
        mission.PlaceShip(b, sector);

        mission.Update(0.25);   // one chunk → the sweep runs

        Assert.True(a.SeenBySide(Side0), "own side must always see a same-side ship");
        Assert.True(b.SeenBySide(Side0), "own side must always see a same-side ship");
        // 50 km apart — nobody's scanner range reaches; this is purely the own-side rule.
        Assert.False(a.SeenBySide(Side1));
        Assert.False(b.SeenBySide(Side1));

        Console.WriteLine($"VIS-SAME: ships {a.Id}/{b.Id} same side 50km apart -> both SeenBySide(0)=true (own-side rule), SeenBySide(1)=false (no scanner)");
    }

    // ── Scenario 2: different sides, in range + clear LOS → seen ──

    /// <summary>A side-1 target 300 units from a side-0 faohshcar scanner (detection
    /// radius 1952, clear LOS) becomes SeenBySide(0) true after the sweep runs.</summary>
    [Fact]
    public void Enemy_ship_in_scope_becomes_seen_after_the_sweep()
    {
        var mission = NewStartedMission(2);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9201, ScannerHull, Side0, basePos);           // faohshcar side 0
        var target = NewShip(9202, TargetHull, Side1, basePos + new Vector3(300f, 0f, 0f)); // dreghpod side 1
        mission.PlaceShip(scanner, sector);
        mission.PlaceShip(target, sector);

        // Before the sweep the state is untouched → reads false (fresh native entry).
        Assert.False(target.SeenBySide(Side0));

        mission.Update(0.25);   // sweep runs

        Assert.True(target.SeenBySide(Side0), "side 0's faohshcar (range 3600) must see dreghpod 300u away");
        // The target's own side is also visible (own-side rule).
        Assert.True(target.SeenBySide(Side1));

        Console.WriteLine($"VIS-IN: side-1 {target.HullType.ModelName} at 300u of side-0 faohshcar -> SeenBySide(0) true after sweep (detect radius 1952)");
    }

    // ── Scenario 3: same pair moved beyond range → a real true→false transition ──

    /// <summary>The same in-range pair, with the target moved 50,000 away (≫ 1952),
    /// must have SeenBySide(0) FLIP to false on the re-run — proving the sweep rewrites
    /// state on transition, not just an initial-state check.</summary>
    [Fact]
    public void Moving_the_enemy_out_of_scope_flips_seen_to_unseen()
    {
        var mission = NewStartedMission(2);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9301, ScannerHull, Side0, basePos);
        var target = NewShip(9302, TargetHull, Side1, basePos + new Vector3(300f, 0f, 0f));
        mission.PlaceShip(scanner, sector);
        mission.PlaceShip(target, sector);

        mission.Update(0.25);
        Assert.True(target.SeenBySide(Side0), "must first be seen (precondition)");

        // Move the target far beyond the 1952 detection radius and re-sweep.
        target.Position = basePos + new Vector3(50_000f, 0f, 0f);
        mission.Update(0.5);    // advance the clock so the sweep re-runs

        Assert.False(target.SeenBySide(Side0), "moving beyond range must flip SeenBySide to false");
        Assert.True(target.SeenBySide(Side1), "own side stays visible");

        Console.WriteLine($"VIS-OUT: side-1 target moved 50km away -> SeenBySide(0) flipped true->false after re-sweep");
    }

    // ── Scenario 3b: the same pair, occluded by a real asteroid → unseen ──

    /// <summary>Transition proof via occlusion instead of range: a real asteroid injected
    /// between the scanner and an in-range target must flip SeenBySide to false while the
    /// pair is STILL within detection range — proving the sweep honours
    /// <see cref="VisibilityMath.LineOfSightExist"/>, not just the range half. Geometry
    /// mirrors P131's hand-derived occlusion (scanner/target one radius either side of a
    /// radius-644 rock on the x-axis: 30° covered vs 0.06° target extent → decisively
    /// occluded, in range).</summary>
    [Fact]
    public void An_asteroid_occluding_an_in_range_enemy_flips_seen_to_unseen()
    {
        var mission = NewStartedMission(2);
        var (sector, basePos) = CleanBase(mission);

        // Rebase away from the sector's real content so our synthetic rock is the only
        // occluder anywhere near the pair.
        var rockCentre = basePos;
        var scanner = NewShip(9401, ScannerHull, Side0, rockCentre - new Vector3(644f, 0f, 0f));
        var target = NewShip(9402, TargetHull, Side1, rockCentre + new Vector3(644f, 0f, 0f));
        mission.PlaceShip(scanner, sector);
        mission.PlaceShip(target, sector);

        // In range + clear LOS first (no rock yet).
        mission.Update(0.25);
        Assert.True(target.SeenBySide(Side0), "pair must first be seen (clear LOS precondition)");

        // Drop a real asteroid of radius 644 dead-centre between them (the P131 occluder).
        mission.PlaceAsteroid(new Asteroid(new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [rockCentre.X, rockCentre.Y, rockCentre.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: 0, AsteroidId: 9421,
            HitPoints: 1000f, Radius: 644, ModelName: "m2xspiderw.igc",
            TextureName: "", IconName: "", ClusterId: sector.Id, Name: "OCCLUDER", Fraction: 0)),
            sector);

        mission.Update(0.5);    // advance the clock so the sweep re-runs with the rock in place
        Assert.True((scanner.Position - target.Position).Length() <= 1952f);
        Assert.False(target.SeenBySide(Side0), "an in-range enemy occluded by a real asteroid must not be seen");

        Console.WriteLine($"VIS-OCCLUDE: side-1 target in range (1288u) but occluded by radius-644 asteroid -> SeenBySide(0) true->false (LineOfSightExist honoured)");
    }

    // ── Scenario 4: a side with no scanners sees nothing ──

    /// <summary>A third side with no ships/stations/probes anywhere in the mission never
    /// sees anything: the sweep reconciles it to false (via the mission side set) rather
    /// than leaving an untouched/possible-true slot.</summary>
    [Fact]
    public void A_scannerless_side_never_sees_another_sides_object()
    {
        var mission = NewStartedMission(3);   // sides 0,1,2 — side 2 has no presence at all
        var (sector, basePos) = CleanBase(mission);

        var a = NewShip(9501, ScannerHull, Side0, basePos);
        mission.PlaceShip(a, sector);

        mission.Update(0.25);

        // Own side visible; the empty side 2 (no scanners in the sector or the mission)
        // sees nothing belonging to side 0.
        Assert.True(a.SeenBySide(Side0));
        Assert.False(a.SeenBySide(Side2), "a side with no scanners must never see a foreign object");

        Console.WriteLine($"VIS-NOSCAN: side 2 has no scanners -> ship {a.Id} (side 0) SeenBySide(2)=false after sweep");
    }
}
