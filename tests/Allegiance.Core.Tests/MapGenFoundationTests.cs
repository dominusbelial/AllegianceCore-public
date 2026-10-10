namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

/// <summary>
/// P397 — structural verification of the procedural SingleRing map generator:
/// correct sector count, the plain-ring warp topology (each sector links
/// exactly its two ring neighbours), home starbases at team clusters and real
/// (minable) asteroids at populated clusters. These are the generator's
/// discriminating invariants — each should turn red if the corresponding piece
/// of the algorithm is removed.
/// </summary>
public sealed class MapGenFoundationTests
{
    private static readonly Lazy<StaticCore> GenCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            // Need >= 2 civilizations whose initial station type resolves in the
            // same core, else GenerateStarbase cannot build a home base.
            var ok = core.Civilizations.Values.Count(c =>
                c.InitialStationTypeId != -1 && core.StationTypes.ContainsKey(c.InitialStationTypeId));
            if (ok >= 2) return core;
        }
        throw new InvalidOperationException("no static core with 2 usable civilizations found in corpus");
    });

    private static readonly Lazy<int[]> CivPair = new(() =>
        GenCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId)
            .Where(c => c.InitialStationTypeId != -1 && GenCore.Value.StationTypes.ContainsKey(c.InitialStationTypeId))
            .Take(2)
            .Select(c => (int)c.CivilizationId)
            .ToArray());

    private static MissionMap GenerateTwoTeamRing()
    {
        var pmp = new MapGenParams
        {
            Type = MapGenType.SingleRing,
            Teams = 2,
            CivIds = CivPair.Value,
            MapSize = 0,
        };
        return MissionMapGenerator.Generate(pmp, GenCore.Value, 0.0);
    }

    private static Mission AssembleMap(MissionMap map)
    {
        var specs = new[]
        {
            new SideSpec(0, "Team 0", (short)CivPair.Value[0]),
            new SideSpec(1, "Team 1", (short)CivPair.Value[1]),
        };
        var m = Mission.Assemble(GenCore.Value, map, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>The default SingleRing layout (iMapSize=0): teamPerTeam=1,
    /// neutralPerTeam=1 ⇒ (1+1)×2 = 4 sectors.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SingleRing_TwoTeams_ProducesFourSectors()
    {
        Require.Artwork();
        var map = GenerateTwoTeamRing();
        Assert.Equal(4, map.Clusters.Count);

        var m = AssembleMap(map);
        Assert.Equal(4, m.Sectors.Count);

        // Exactly 2 home sectors (the two team clusters), 2 neutral.
        Assert.Equal(2, map.Clusters.Count(c => c.HomeSector));
        Assert.Equal(2, map.Clusters.Count(c => !c.HomeSector));
    }

    /// <summary>The plain ring: every cluster connects to exactly its two ring
    /// neighbours (i-1 and i+1 mod N) — <c>LinkClusters()</c>'s chain i→i+1
    /// with a last→first wrap (mapmakerigc.cpp:867-870).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SingleRing_EachSectorLinksItsTwoRingNeighbours()
    {
        Require.Artwork();
        var map = GenerateTwoTeamRing();
        var m = AssembleMap(map);
        int n = m.Sectors.Count;

        for (int i = 0; i < n; i++)
        {
            var sector = m.Sectors[i];
            Assert.Equal(2, sector.Warps.Count);   // exactly two ring neighbours
            var dst = sector.Warps.Select(w => w.Destination!.Id).ToHashSet();
            Assert.Equal(
                new HashSet<short> { (short)((i + n - 1) % n), (short)((i + 1) % n) },
                dst);
        }
    }

    /// <summary>Home (team) sectors get a starbase; neutral sectors do not
    /// (GenerateStarbase is called only for home sectors, mapmakerigc.cpp:962-963).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SingleRing_HomeSectorsGetStations_NeutralSectorsHaveNone()
    {
        Require.Artwork();
        var m = AssembleMap(GenerateTwoTeamRing());

        foreach (var sector in m.Sectors)
        {
            if (sector.Cluster.HomeSector)
                Assert.Single(sector.Stations);
            else
                Assert.Empty(sector.Stations);
        }
    }

    /// <summary>Every sector is populated with at least one genuinely minable
    /// He3 asteroid — the PopulateCluster/CreateAsteroid port (an asteroid the
    /// mining pass can drain).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SingleRing_EverySectorHasMinableAsteroids()
    {
        Require.Artwork();
        var m = AssembleMap(GenerateTwoTeamRing());

        foreach (var sector in m.Sectors)
        {
            Assert.NotEmpty(sector.Asteroids);
            Assert.Contains(sector.Mineable, a => a.CanMineHe3);
        }
    }

    /// <summary>Native asserts nTeams >= 2 before generation (mapmakerigc.cpp:172).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SingleRing_RequiresTwoOrMoreTeams()
    {
        Require.Artwork();
        var pmp = new MapGenParams { Type = MapGenType.SingleRing, Teams = 1, CivIds = [CivPair.Value[0]] };
        Assert.Throws<InvalidDataException>(() =>
            MissionMapGenerator.Generate(pmp, GenCore.Value, 0.0));
    }
}
