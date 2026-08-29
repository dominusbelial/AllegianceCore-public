using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Regression guard for <c>tools/corpusvalidate</c>: recomputes the aggregate
/// corpus counts the tool reports and locks them against the documented README /
/// embedded baseline. These are the same numbers corpusvalidate compares against
/// on every run, so this test keeps the tool's baseline and the doc honest
/// without reaching into the tool's private constants. A drift here means the
/// corpus (or a decoder) changed — update this test, the tool's baseline, and
/// the README together, consciously.
/// </summary>
public class CorpusValidateTests
{
    [Fact]
    public void Aggregate_matches_the_recorded_baseline()
    {
        var files = Corpus.Files;
        var cores = new List<StaticCore>();
        var coreFiles = new List<string>();

        foreach (var path in files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            cores.Add(StaticCore.Load(file));
            coreFiles.Add(path);
        }

        // Every core loads exactly once into a unique ID-keyed bucket.
        Assert.Equal(99, cores.Count);

        // Type counts per bucket, summed across all cores — must match README.
        Assert.Equal(9369, cores.Sum(c => c.ProjectileTypes.Count));
        Assert.Equal(5370, cores.Sum(c => c.MissileTypes.Count));
        Assert.Equal(782, cores.Sum(c => c.MineTypes.Count));
        Assert.Equal(4760, cores.Sum(c => c.ProbeTypes.Count));
        Assert.Equal(619, cores.Sum(c => c.ChaffTypes.Count));
        Assert.Equal(952, cores.Sum(c => c.Civilizations.Count));
        Assert.Equal(38927, cores.Sum(c => c.HullTypes.Count));
        Assert.Equal(10928, cores.Sum(c => c.PartTypes.Count));
        Assert.Equal(11511, cores.Sum(c => c.Launchers.Count));
        Assert.Equal(12659, cores.Sum(c => c.StationTypes.Count));
        Assert.Equal(28500, cores.Sum(c => c.Developments.Count));
        Assert.Equal(6503, cores.Sum(c => c.DroneTypes.Count));
        Assert.Equal(99, cores.Sum(c => c.TreasureSets.Count));

        // Core-internal cross-reference dangles, by kind (genuine data quirks —
        // successor chains that legitimately dangle across cores).
        var coreIssues = cores.SelectMany(c => CrossRefValidator.Validate(c))
            .GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(31, coreIssues["hull-successor"]);
        Assert.Equal(3, coreIssues["part-successor"]);
        Assert.Equal(34, coreIssues.Sum(kv => kv.Value));

        // Every map assembles against the representative core; totals per the
        // README / corpusvalidate baseline.
        var rep = cores.First();
        var civs = rep.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> sides = civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [new SideSpec(0, "Team 1", civs[0].CivilizationId),
               new SideSpec(1, "Team 2", civs[1].CivilizationId)];

        int sectors = 0, warps = 0, dangling = 0, stations = 0, asteroids = 0;
        var mapIssueKinds = new Dictionary<string, int>();
        int maps = 0;
        foreach (var path in files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            var mission = Mission.Assemble(rep, map, sides, new NullIgcSite());
            maps++;
            sectors += mission.Sectors.Count;
            warps += mission.Warps.Count;
            dangling += mission.DanglingWarps.Count;
            stations += mission.Sectors.Sum(s => s.Stations.Count);
            asteroids += mission.Sectors.Sum(s => s.Asteroids.Count);
            foreach (var i in CrossRefValidator.Validate(rep, map))
                mapIssueKinds[i.Kind] = mapIssueKinds.GetValueOrDefault(i.Kind) + 1;
        }

        Assert.Equal(111, maps);
        Assert.Equal(1506, sectors);
        Assert.Equal(5212, warps);
        Assert.Equal(0, dangling);
        Assert.Equal(289, stations);
        Assert.Equal(973, asteroids);
        // Maps validated against the loose pairing core are internally clean and
        // carry no map->core dangles that land in a nonzero aggregate for the
        // representative core's own maps; no kind appears. (See CrossRefSurveyTests
        // for the station-type universe question answered against every core.)
        Assert.Empty(mapIssueKinds);
    }
}
