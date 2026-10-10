using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// <see cref="Model.MissionMap"/> — the map-file universe. Every one of the 111
/// maps loads, and each list's count exactly reproduces IgcReader's raw record
/// count for the same file.
/// </summary>
public class MissionMapTests
{
    private static IEnumerable<(string Path, IgcFile File, Model.MissionMap Map)> Loaded()
        => Corpus.Files
            .Select(p => (Path: p, File: IgcReader.ParseFile(p)))
            .Where(t => t.File.Wrapper == IgcWrapper.DataSize)
            .Select(t => (t.Path, t.File, Model.MissionMap.Load(t.File)));

    /// <summary>All 111 maps load and contain only the four expected object types.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void All_111_maps_load()
    {
        Require.Artwork();
        Assert.Equal(Corpus.ExpectedMaps, Loaded().ToList().Count);
    }

    /// <summary>
    /// Each list's count equals the raw record count IgcReader reports for the
    /// same file — an off-by-one in dispatch shows up here. Maps carry no
    /// static-core records, so this covers every record in the file.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Counts_equal_the_raw_record_histogram()
    {
        Require.Artwork();
        foreach (var (path, f, m) in Loaded())
        {
            void Check(IgcObjectType type, int actual)
            {
                var expected = f.CountOf(type);
                Assert.True(expected == actual,
                    $"{Path.GetFileName(path)}: {type} count {actual} != raw {expected}");
            }

            Check(IgcObjectType.Cluster, m.Clusters.Count);
            Check(IgcObjectType.Warp, m.Warps.Count);
            Check(IgcObjectType.Station, m.Stations.Count);
            Check(IgcObjectType.Asteroid, m.Asteroids.Count);

            // The whole record stream is exactly these four types.
            var covered = f.CountOf(IgcObjectType.Cluster) + f.CountOf(IgcObjectType.Warp) +
                          f.CountOf(IgcObjectType.Station) + f.CountOf(IgcObjectType.Asteroid);
            Assert.True(covered == f.Records.Count,
                $"{Path.GetFileName(path)}: {f.Records.Count - covered} map records of an unhandled type");
        }
    }
}
