using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P797 — alephs are per-side <b>seen</b> models, exactly as native creates them
/// (<c>Igc/warpIGC.cpp:35</c>: <c>c_mtStatic | c_mtHitable | c_mtSeenBySide |
/// c_mtPredictable</c>), scored by the cluster's periodic
/// <c>UpdateSeenBySide</c> sweep (<c>Igc/clusterIGC.cpp:347-357</c> →
/// <c>ThingSiteImpl::UpdateSideVisibility</c>, <c>FedSrv/FedSrv.CPP:10876-11129</c>)
/// and exported to a side only once that side has seen them
/// (<c>FedSrv/fsmission.cpp:4181-4186</c>).
/// <para>These are pure-domain proofs on a real mission: no server, no bot. They
/// pin the three rules the join export and the discovery sweep depend on —
/// (1) an aleph is seen only via a scanner of that side in range with line of
/// sight, (2) a predictable static object STAYS known to a side after its last
/// scanner leaves (<c>FedSrv.CPP:11124-11126</c>: “keep static objects visible
/// (but not currentEyed)”), while <c>GetCurrentEye</c> still decays, and (3)
/// seeing one aleph reveals its PARTNER to that side (“perform a hack here: if a
/// aleph becomes visible, make its corresponding aleph visible”,
/// <c>FedSrv.CPP:10786-10800</c>). Plus the two native mission options
/// (<c>bShowMap</c> / <c>bShowHomeSector</c>) and the serial-vs-parallel
/// determinism of the resulting visibility sets.</para>
/// </summary>
public class P797MapDiscoveryTests
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

    /// <summary>A real map that actually has alephs (the sweep needs warps to
    /// score, and the partner-reveal rule needs a resolved pair).</summary>
    private static readonly Lazy<MissionMap> WarpMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            if (map.Warps.Count < 2) continue;
            return map;
        }
        throw new InvalidOperationException("no map with warps found in corpus");
    });

    private static DataHullTypeIGC ScannerHull => PayloadDecodeTests.Hulls().First(h => h.V.ModelName == "faohshcar").V;

    private static Mission NewStartedMission(bool showMap = false, bool showHomeSector = false)
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, WarpMap.Value,
            [new SideSpec(Side0, "Alpha", civ), new SideSpec(Side1, "Bravo", civ)], new NullIgcSite());
        mission.Stage = MissionStage.Started;
        mission.ShowMap = showMap;
        mission.ShowHomeSector = showHomeSector;
        return mission;
    }

    /// <summary>A sector with alephs and no station (so no ambient station scanner
    /// can see anything in it) and no ambient ship — the cleanest possible
    /// "nothing is visible until the test's own scanner arrives" stage.</summary>
    private static (Mission Mission, Sector Sector, ResolvedWarp Warp) CleanAleph()
    {
        var mission = NewStartedMission();
        foreach (var sector in mission.Sectors)
        {
            if (sector.StationRuntimes.Count != 0) continue;
            var warp = sector.Warps.FirstOrDefault(w => w.Warp.Position is { Length: 3 } && w.Destination is not null);
            if (warp is null) continue;
            return (mission, sector, warp);
        }
        throw new InvalidOperationException("no station-free sector with an aleph found in the corpus map");
    }

    private static Ship NewShip(short id, short side, Vector3 pos) =>
        new(id, ScannerHull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    /// <summary>Advance a full cadence window so the every-10 pass re-runs the
    /// sweep (<see cref="Sector.c_nPassesPerUpdate"/>, native
    /// <c>clusterIGC.cpp:348</c>).</summary>
    private static void ReSweep(Mission m, double start)
    {
        for (int i = 0; i < Sector.c_nPassesPerUpdate; i++)
            m.Update(start + (i + 1) * 0.5);
    }

    /// <summary>⭐ An aleph is seen only by a side whose scanner reaches it: the
    /// enemy side — which has no scanner in that sector — never sees it, so the
    /// same aleph is a per-side fact, not a whole-mission one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_aleph_is_seen_only_by_the_side_whose_scanner_reaches_it()
    {
        Require.Artwork();
        var (mission, sector, warp) = CleanAleph();

        Assert.False(warp.SeenBySide(Side0), "an aleph must not be seen before any sweep has run");
        Assert.False(warp.SeenBySide(Side1));

        // A side-0 faohshcar (scanner range 3600, detection radius ~1952) parked
        // 300 u from the aleph mouth.
        var scanner = NewShip(7971, Side0, warp.Position + new Vector3(300f, 0f, 0f));
        mission.PlaceShip(scanner, sector);

        mission.Update(0.25);   // first pass runs the sweep

        Assert.True(warp.SeenBySide(Side0), "side 0's scanner 300 u away must spot the aleph");
        Assert.True(warp.GetCurrentEye(Side0), "the scanner is the eye on it this tick");
        Assert.False(warp.SeenBySide(Side1), "side 1 has no scanner in this sector — the aleph stays unknown to it");

        Console.WriteLine($"ALEPH-VIS-IN: aleph {warp.Warp.WarpId} in sector {sector.Id}: SeenBySide(0)=true, SeenBySide(1)=false");
    }

    /// <summary>⭐ The native predictable latch: once a side has seen an aleph it
    /// stays known forever, while <c>GetCurrentEye</c> drops when the last scanner
    /// leaves — the exact <c>if (m_bPredictable) sv.fVisible(true)</c> rule
    /// (<c>FedSrv.CPP:11124-11126</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_aleph_stays_known_after_its_last_scanner_leaves()
    {
        Require.Artwork();
        var (mission, sector, warp) = CleanAleph();
        var scanner = NewShip(7972, Side0, warp.Position + new Vector3(300f, 0f, 0f));
        mission.PlaceShip(scanner, sector);

        mission.Update(0.25);
        Assert.True(warp.SeenBySide(Side0), "must first be seen (precondition)");

        scanner.Position = warp.Position + new Vector3(50_000f, 0f, 0f);
        ReSweep(mission, 1.0);

        Assert.True(warp.SeenBySide(Side0), "a predictable static aleph stays known after the scanner leaves (FedSrv.CPP:11124-11126)");
        Assert.False(warp.GetCurrentEye(Side0), "…but it is no longer the current eye");
        Assert.False(warp.SeenBySide(Side1), "side 1 never saw it, and nothing reveals it to them");

        Console.WriteLine($"ALEPH-VIS-LATCH: aleph {warp.Warp.WarpId} SeenBySide(0) stays true, GetCurrentEye(0) drops to false");
    }

    /// <summary>⭐ Native's aleph-pair reveal: seeing one end of an aleph makes the
    /// partner known to that side too (the forced <c>fVisible(true)</c> hack in
    /// <c>ShowObject</c>, <c>FedSrv.CPP:10786-10800</c>) — the partner's own
    /// position is exported in the same breath.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Seeing_one_aleph_reveals_its_partner_to_that_side()
    {
        Require.Artwork();
        var (mission, sector, warp) = CleanAleph();
        var partner = warp.Partner();
        Skip.If(partner is null, "this corpus map's aleph has no partner warp record (dangling destination)");
        Assert.False(partner!.SeenBySide(Side0));

        var scanner = NewShip(7973, Side0, warp.Position + new Vector3(300f, 0f, 0f));
        mission.PlaceShip(scanner, sector);
        mission.Update(0.25);

        Assert.True(warp.SeenBySide(Side0), "the scanned aleph is seen");
        Assert.True(partner.SeenBySide(Side0), "its partner is force-revealed to the same side (native ShowObject hack)");
        Assert.False(partner.SeenBySide(Side1), "…and still unknown to the other side");

        Console.WriteLine($"ALEPH-PAIR: aleph {warp.Warp.WarpId} seen -> partner {partner.Warp.WarpId} revealed to side 0 only");
    }

    /// <summary>⭐ The port's default is native's: <c>bShowMap = false</c>
    /// (<c>Igc/igc.h:1244</c>) means NOT every aleph is known at start, while
    /// <c>bShowMap = true</c> reveals them all (<c>RevealMap</c>,
    /// <c>Igc/mapmakerigc.cpp:999-1018</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShowMap_controls_whether_every_aleph_starts_known()
    {
        Require.Artwork();

        var hidden = NewStartedMission();
        hidden.SeedStartingVisibility();
        int hiddenKnown = hidden.Sectors.Sum(s => s.Warps.Count(w => w.SeenBySide(Side0)));
        int total = hidden.Sectors.Sum(s => s.Warps.Count);
        Assert.True(total > 0, "the corpus map must have alephs");
        Assert.True(hiddenKnown < total, "native's default (bShowMap=false) must leave at least one aleph unknown at start");

        var shown = NewStartedMission(showMap: true);
        shown.SeedStartingVisibility();
        Assert.All(shown.Sectors.SelectMany(s => s.Warps), w =>
        {
            Assert.True(w.SeenBySide(Side0), $"ShowMap must reveal aleph {w.Warp.WarpId} to side 0");
            Assert.True(w.SeenBySide(Side1), $"ShowMap must reveal aleph {w.Warp.WarpId} to side 1");
        });

        Console.WriteLine($"ALEPH-SHOWMAP: default known {hiddenKnown}/{total}; ShowMap known {total}/{total}");
    }

    /// <summary>⭐ <c>bShowHomeSector = true</c> reveals every model in a cluster
    /// where the side owns a station (<c>RevealHomeClusters</c>,
    /// <c>Igc/mapmakerigc.cpp:969-997</c>); the default leaves them fogged.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShowHomeSector_reveals_the_home_cluster_only_when_enabled()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sectorWithStation = mission.Sectors.FirstOrDefault(s => s.StationRuntimes.Count > 0);
        Skip.If(sectorWithStation is null, "corpus map has no station in any sector");
        var ownerSide = sectorWithStation!.StationRuntimes[0].SideId;
        Skip.If(ownerSide < 0, "the sector's first station is side-less");

        mission.SeedStartingVisibility();
        var ownedWarps = sectorWithStation.Warps;
        Skip.If(ownedWarps.Count == 0, "the station's sector has no aleph to test with");

        var revealed = NewStartedMission(showHomeSector: true);
        revealed.SeedStartingVisibility();
        var revealedSector = revealed.Sectors.First(s => s.Id == sectorWithStation.Id);
        Assert.All(revealedSector.Warps, w => Assert.True(w.SeenBySide(ownerSide),
            $"ShowHomeSector must reveal aleph {w.Warp.WarpId} in the side's home cluster"));

        Console.WriteLine($"ALEPH-SHOWHOME: sector {sectorWithStation.Id} side {ownerSide}: {ownedWarps.Count} aleph(s) revealed with the flag");
    }

    /// <summary>⭐ Determinism gate: the visibility sets the sweep produces are
    /// identical with the serial and the parallel intra-sector sim
    /// (<see cref="ParallelSectorSim"/>), for several worker degrees — the sweep
    /// itself stays serial on the sim thread (only collision DETECTION is
    /// parallelised, P667), so this pins that the flag can never perturb the
    /// per-side seen sets the exports are built from.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Serial_and_parallel_sector_sim_compute_identical_visibility_sets()
    {
        Require.Artwork();

        string Run(bool parallel, int threads)
        {
            ParallelSectorSim.Enabled = parallel;
            ParallelSectorSim.ThreadCount = threads;
            try
            {
                var (mission, sector, warp) = CleanAleph();
                mission.PlaceShip(NewShip(7981, Side0, warp.Position + new Vector3(300f, 0f, 0f)), sector);
                var other = sector.Warps.FirstOrDefault(w => w.Warp.WarpId != warp.Warp.WarpId
                    && w.Warp.Position is { Length: 3 });
                if (other is not null)
                    mission.PlaceShip(NewShip(7982, Side1, other.Position + new Vector3(400f, 0f, 0f)), sector);
                ReSweep(mission, 1.0);

                var seen = new List<string>();
                foreach (var s in mission.Sectors)
                {
                    foreach (var w in s.Warps)
                        seen.Add($"{s.Id}:w{w.Warp.WarpId}:{w.SeenBySide(Side0)}{w.SeenBySide(Side1)}");
                    foreach (var rock in s.Mineable)
                        seen.Add($"{s.Id}:a{rock.Data.AsteroidId}:{rock.SeenBySide(Side0)}{rock.SeenBySide(Side1)}");
                    foreach (var st in s.StationRuntimes)
                        seen.Add($"{s.Id}:s{st.StationId}:{st.SeenBySide(Side0)}{st.SeenBySide(Side1)}");
                }
                return string.Join("|", seen);
            }
            finally { ParallelSectorSim.Reset(); }
        }

        string serial = Run(parallel: false, threads: 1);
        Assert.Equal(serial, Run(parallel: true, threads: 1));
        Assert.Equal(serial, Run(parallel: true, threads: 4));
        Assert.Equal(serial, Run(parallel: true, threads: 8));

        Console.WriteLine($"ALEPH-DETERMINISM: {serial.Split('|').Length} visibility entries identical across serial/1/4/8 threads");
    }
}
