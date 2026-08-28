using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Core.Tests;

/// <summary>
/// Exercises the static <see cref="Mission.Assemble"/> over the whole corpus:
/// every map must assemble against a real core, every warp destination must
/// resolve to a sector inside the same mission, and each sector's contents
/// must sum exactly to the raw record counts carried by its <see cref="MissionMap"/>.
/// </summary>
public class MissionAssemblyTests
{
    /// <summary>
    /// The "real core" every map is assembled against. Nothing in a map file
    /// names its governing core, so the corpus-wide convention (as in
    /// <see cref="CrossRefValidator"/>) is a loose pairing: pick one
    /// representative core deterministically and assemble every map against it.
    /// </summary>
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

    /// <summary>All unique map files in the corpus, in deterministic order.</summary>
    private static readonly Lazy<IReadOnlyList<(string Path, MissionMap Map)>> AllMaps = new(() =>
    {
        var maps = new List<(string, MissionMap)>();
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            maps.Add((path, MissionMap.Load(file)));
        }
        return maps;
    });

    /// <summary>Two sides drawn from the representative core's civilizations.</summary>
    private static IReadOnlyList<SideSpec> TwoSides()
    {
        var core = RepresentativeCore.Value;
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        if (civs.Count == 0)
            throw new InvalidOperationException("representative core has no civilizations");
        return civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [
                new SideSpec(0, "Team 1", civs[0].CivilizationId),
                new SideSpec(1, "Team 2", civs[1].CivilizationId),
            ];
    }

    [Fact]
    public void Every_map_assembles_against_a_real_core()
    {
        Assert.Equal(111, AllMaps.Value.Count);

        foreach (var (path, map) in AllMaps.Value)
        {
            var mission = Mission.Assemble(RepresentativeCore.Value, map, TwoSides());

            // One sector per OT_cluster, in map order.
            Assert.Equal(map.Clusters.Count, mission.Sectors.Count);
            for (var i = 0; i < map.Clusters.Count; i++)
                Assert.Equal(map.Clusters[i].ClusterId, mission.Sectors[i].Id);

            // Every warp destination resolves to a sector inside the mission.
            Assert.Empty(mission.DanglingWarps);
            Assert.All(mission.Warps, w => Assert.NotNull(w.Destination));

            // Sector contents summed equal the raw record counts from the map.
            Assert.Equal(map.Warps.Count, mission.Sectors.Sum(s => s.Warps.Count));
            Assert.Equal(map.Stations.Count, mission.Sectors.Sum(s => s.Stations.Count));
            Assert.Equal(map.Asteroids.Count, mission.Sectors.Sum(s => s.Asteroids.Count));
        }
    }

    [Fact]
    public void Warp_destinations_close_over_the_sector_graph()
    {
        var (_, map) = AllMaps.Value[0];
        var mission = Mission.Assemble(RepresentativeCore.Value, map, TwoSides());

        // Each warp's destination sector is reachable from the mission, and a
        // warp on the far side leads back to this warp's sector (graph symmetry
        // — the whole point of resolving the warp graph).
        var warpByWarpId = mission.Warps.ToDictionary(w => w.Warp.WarpId);
        foreach (var w in mission.Warps)
        {
            var dst = Assert.IsType<Sector>(w.Destination);
            Assert.Contains(dst, mission.Sectors);
            var there = dst.Warps.FirstOrDefault(x => x.Warp.WarpId == w.Warp.DestinationId);
            Assert.NotNull(there);
            Assert.Equal(w.Warp.ClusterId, there.Destination?.Id);
        }
    }

    [Fact]
    public void Sides_resolve_their_civilizations_from_the_core()
    {
        var core = RepresentativeCore.Value;
        var spec = new SideSpec(0, "Alpha", core.Civilizations.Keys.First());
        var mission = Mission.Assemble(core, AllMaps.Value[0].Map, new[] { spec });

        var side = Assert.Single(mission.Sides);
        Assert.Equal((short)0, side.Id);
        Assert.Equal("Alpha", side.Name);
        Assert.Equal(spec.CivilizationId, side.Civilization.CivilizationId);
    }

    [Fact]
    public void Assemble_rejects_a_side_whose_civilization_is_absent()
    {
        var core = RepresentativeCore.Value;
        // A civilization id that is definitely not present.
        short bogus = (short)(core.Civilizations.Keys.Max() + 777);
        while (core.Civilizations.ContainsKey(bogus)) bogus++;

        var ex = Assert.Throws<InvalidOperationException>(
            () => Mission.Assemble(core, AllMaps.Value[0].Map, new[] { new SideSpec(0, "Bogus", bogus) }));
        Assert.Contains($"civilization {bogus}", ex.Message);
    }

    [Fact]
    public void Representative_core_and_full_corpus_counts_are_sane()
    {
        var core = RepresentativeCore.Value;
        Assert.NotEmpty(core.Civilizations);
        Assert.Equal(99, Corpus.Files.Count(f => { try { return IgcReader.ParseFile(f).Wrapper == IgcWrapper.VersionAndDataSize; } catch { return false; } }));
        Assert.Equal(111, AllMaps.Value.Count);
    }
}
