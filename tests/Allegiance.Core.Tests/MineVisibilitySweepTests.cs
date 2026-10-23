using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P453 — mines are side-visible static objects: they join the per-side
/// cross-side visibility sweep (<see cref="Sector.ResolveCrossSideVisibility"/>,
/// P132/P134) as <b>targets</b>, exactly the way native mines take part in
/// <c>UpdateSideVisibility</c> (they are created with
/// <c>c_mtSeenBySide | c_mtPredictable | c_mtHitable | c_mtStatic</c>,
/// mineigc.cpp:103-107). These are pure-domain proofs (real mission sector, real
/// scanner hull, a real <see cref="Mine"/> model from the corpus core) that a
/// mine gets <c>SeenBySide(enemySide) == true</c> only when an enemy scanner is
/// in range with line of sight — and that a mine is NEVER a scanner itself
/// (native mines carry no <c>c_mtScanner</c>).
/// <para>Style mirrors <see cref="CrossSideVisibilitySweepTests"/> (real
/// <c>faohshcar</c> scanner + the same <c>CleanBase</c> ambient-noise control),
/// but the target is a real <see cref="Mine"/> instead of a ship, laid by a
/// side-1 launcher. Because a mine is a target with an owner side, the own-side
/// rule applies (the owner side always sees its own field, as today) and the
/// enemy side sees it only via its scanner net.</para>
/// </summary>
public class MineVisibilitySweepTests
{
    private const short Side0 = 0;
    private const short Side1 = 1;

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

    /// <summary>A real dreghpod hull — used for the side-1 launcher / a target ship.</summary>
    private static readonly DataHullTypeIGC TargetHull = Hull("dreghpod");

    /// <summary>A real mine type from the representative core.</summary>
    private static DataMineTypeIGC MineType(StaticCore core) => core.MineTypes.Values.First();

    /// <summary>A started 2-side mission over a real map.</summary>
    private static Mission NewStartedMission()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, SomeMap.Value,
            [new SideSpec(Side0, "Alpha", civ), new SideSpec(Side1, "Bravo", civ)], new NullIgcSite());
        mission.Stage = MissionStage.Started;
        return mission;
    }

    /// <summary>A sector + a base point 8000+ units beyond every real asteroid
    /// and station in it (the CrossSideVisibilitySweepTests ambient-noise
    /// control), so the test's own scanner is the only one that can reach the
    /// base and no real asteroid occludes the short scanner→target ray.</summary>
    private static (Sector Sector, Vector3 Base) CleanBase(Mission m)
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
            return (sector, new Vector3(maxX + 8000f, cy, cz));
        }
        throw new InvalidOperationException("no sector available in corpus map");
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, short side, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    /// <summary>A real <see cref="Mine"/> of <paramref name="side"/> placed at
    /// <paramref name="pos"/> in <paramref name="sector"/>. The launcher is a
    /// real (unplaced) Ship of that side so the mine carries the owner side —
    /// the same derivation <c>Mine</c>'s constructor uses from a real layering
    /// ship. Added to the sector so the sweep scores it (mines live in
    /// <c>Sector.Models</c>).</summary>
    private static Mine PlaceMine(Sector sector, StaticCore core, short id, short side, Vector3 pos, double now)
    {
        var launcher = NewShip((short)(id + 500), TargetHull, side, pos);
        var mine = new Mine(id, sector, MineType(core), pos, now, launcher);
        sector.Add(mine);
        return mine;
    }

    /// <summary>Advance a full cadence window so the every-10 sweep re-runs
    /// (P324 faithful native cadence, <see cref="Sector.c_nPassesPerUpdate"/>).</summary>
    private static void ReSweep(Mission m, double start)
    {
        for (int i = 0; i < Sector.c_nPassesPerUpdate; i++)
            m.Update(start + (i + 1) * 0.5);
    }

    /// <summary>⭐ A side-0 scanner in range with clear LOS sees a side-1 mine
    /// (<c>SeenBySide(Side0)==true</c> after the sweep), while the mine's OWN
    /// side always sees it — the mine is a target with an owner side, unlike
    /// the sideless asteroids P134 added. The mine is never seen before the
    /// sweep runs.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_enemy_scanner_in_range_with_line_of_sight_spots_the_mine()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9701, ScannerHull, Side0, basePos);
        mission.PlaceShip(scanner, sector);
        var mine = PlaceMine(sector, mission.Core, 9702, Side1, basePos + new Vector3(300f, 0f, 0f), mission.LastUpdate);

        // Before the sweep the state is untouched → reads false (fresh native entry).
        Assert.False(mine.SeenBySide(Side0));

        mission.Update(0.25);   // first update runs the sweep

        Assert.True(mine.SeenBySide(Side0), "side 0's faohshcar (range 3600) must spot a side-1 mine 300u away");
        Assert.True(mine.SeenBySide(Side1), "the mine's own side always sees its own field");

        Console.WriteLine($"MINE-VIS-IN: side-1 mine at 300u of side-0 faohshcar -> SeenBySide(0) true after sweep (detect radius 1952); own side always true");
    }

    /// <summary>⭐ P797 — moving the enemy scanner out of range KEEPS the mine
    /// known (<c>SeenBySide</c> stays true) and only drops the current-eye flag.
    /// This is native's predictable latch: <c>if (m_bPredictable) //keep static
    /// objects visible (but not currentEyed) sv.fVisible(true);</c>
    /// (<c>FedSrv/FedSrv.CPP:11124-11126</c>), and a mine is created with
    /// <c>c_mtPredictable</c> (<c>mineigc.cpp:104</c>). Before P797 this test
    /// asserted the pre-latch port behaviour (true→false), which native does not
    /// do for any static object — see P797_MAP_DISCOVERY.md.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Moving_the_scanner_out_of_scope_keeps_the_mine_known_but_not_eyed()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9703, ScannerHull, Side0, basePos);
        mission.PlaceShip(scanner, sector);
        var mine = PlaceMine(sector, mission.Core, 9704, Side1, basePos + new Vector3(300f, 0f, 0f), mission.LastUpdate);

        mission.Update(0.25);
        Assert.True(mine.SeenBySide(Side0), "must first be seen (precondition)");

        // Move the scanner far beyond the 1952 detection radius and re-sweep.
        scanner.Position = basePos + new Vector3(50_000f, 0f, 0f);
        ReSweep(mission, 1.0);

        Assert.True(mine.SeenBySide(Side0), "a predictable static mine stays known after the scanner leaves (FedSrv.CPP:11124-11126)");
        Assert.False(mine.GetCurrentEye(Side0), "…but the side is no longer the eye on it");
        Assert.True(mine.SeenBySide(Side1), "own side stays visible");

        Console.WriteLine("MINE-VIS-OUT: side-0 scanner moved 50km away -> mine SeenBySide(0) latched true, GetCurrentEye(0) false (native predictable latch)");
    }

    /// <summary>⭐ P797 — a mine that has NEVER been seen and is occluded by a real
    /// asteroid stays unseen, while the pair is STILL within detection range —
    /// proving the sweep honours <see cref="VisibilityMath.LineOfSightExist"/>
    /// for mines, not just the range half (geometry mirrors the P131 occlusion
    /// test: one radius either side of a radius-644 rock). The occlusion is
    /// installed BEFORE any sweep runs, because a predictable object that was
    /// ever seen latches visible (FedSrv.CPP:11124-11126) and could no longer
    /// demonstrate the LOS rule.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_occluded_mine_that_was_never_seen_stays_unseen()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var rockCentre = basePos;
        // The representative mine is detected at D = 150 + MineRadius(100) +
        // ScannerRange·sig(0.15) = 790, so a 300-u pair is decisively in range.
        // Place the scanner and mine 300 apart and a radius-120 rock colinear
        // BETWEEN them at 150 from the scanner: the rock is closer than the
        // mine, outside the scanner, and covers the mine's extent — covered
        // angle asin(0.5·120/150) ≈ 23.6° > the mine's visibleAngle
        // asin(100/300) ≈ 19.5°, so LineOfSightExist must fail.
        var scanner = NewShip(9705, ScannerHull, Side0, rockCentre);
        mission.PlaceShip(scanner, sector);
        var mine = PlaceMine(sector, mission.Core, 9706, Side1, rockCentre + new Vector3(300f, 0f, 0f), mission.LastUpdate);

        // Drop the occluding rock BEFORE the first sweep: the pair is in range
        // but the rock blocks the sight line, so the mine must never be seen.
        // (Placing it after an earlier sighting could not prove the LOS rule any
        // more — the native predictable latch would keep the mine visible.)
        mission.PlaceAsteroid(new Asteroid(new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [rockCentre.X + 150f, rockCentre.Y, rockCentre.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: 0, AsteroidId: 9721,
            HitPoints: 1000f, Radius: 120, ModelName: "m2xspiderw.igc",
            TextureName: "", IconName: "", ClusterId: sector.Id, Name: "OCCLUDER", Fraction: 0)),
            sector);

        ReSweep(mission, 1.0);
        Assert.True((scanner.Position - mine.Position).Length() <= 790f);
        Assert.False(mine.SeenBySide(Side0), "an in-range mine occluded by a real asteroid must not be seen");

        Console.WriteLine("MINE-VIS-OCCLUDE: side-1 mine in range (300u, D=790) but occluded by radius-120 asteroid colinear at 150 -> SeenBySide(0) stays false (LineOfSightExist honoured)");
    }

    /// <summary>⭐ A mine is NEVER a scanner. Side 1's only presence in the
    /// sector is a mine (its launcher is unplaced); a side-0 ship right next to
    /// it must NOT become seen by side 1 — the mine contributes nothing to its
    /// side's scanner net (native mines aren't <c>c_mtScanner</c>). The same
    /// flip direction also holds the other way: the mine itself is still seen by
    /// side 0 (it is a target).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_mine_is_never_a_scanner_for_its_own_side()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        // The ONLY side-1 model in the sector is a mine at basePos.
        var mine = PlaceMine(sector, mission.Core, 9707, Side1, basePos, mission.LastUpdate);
        // A side-0 ship parked right beside the mine.
        var ship = NewShip(9708, TargetHull, Side0, basePos + new Vector3(50f, 0f, 0f));
        mission.PlaceShip(ship, sector);

        mission.Update(0.25);

        // Side 1 has NO scanner (only the mine, which is not a scanner), so the
        // adjacent side-0 ship is invisible to side 1. If the mine were wrongly
        // registered as a scanner this could be seen.
        Assert.False(ship.SeenBySide(Side1), "a mine must never act as a scanner for its side");
        // The side-0 ship's own side still sees it (own-side rule), and the mine
        // is still a valid target for side 0's scanner.
        Assert.True(ship.SeenBySide(Side0));
        Assert.True(mine.SeenBySide(Side0), "the mine remains a target for the side-0 scanner even though it is not itself a scanner");
        Assert.True(mine.SeenBySide(Side1), "the mine's own side always sees it");

        Console.WriteLine("MINE-VIS-NOSCAN: a side-1 mine reveals nothing to its side (adjacent side-0 ship SeenBySide(1) stays false) — mines are targets, not scanners");
    }
}
