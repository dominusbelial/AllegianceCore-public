using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// <see cref="Model.StaticCore"/> — the static-core type universe built from
/// <see cref="IgcWrapper.VersionAndDataSize"/> files. The key assertions: every
/// core loads, each carries exactly one <c>Constants</c>, no duplicate ID in any
/// file (loading throws on one), and per-dictionary counts exactly reproduce the
/// raw record histogram <see cref="IgcReader"/> reports for the same file.
/// </summary>
[Trait("Tier", "Live")] // Whole-corpus validation; docs/testing/TIERS.md.
public class StaticCoreTests
{
    private static IEnumerable<(string Path, IgcFile File, Model.StaticCore Core)> Loaded()
        => Corpus.Files
            .Select(p => (Path: p, File: IgcReader.ParseFile(p)))
            .Where(t => t.File.Wrapper == IgcWrapper.VersionAndDataSize)
            .Select(t => (t.Path, t.File, Model.StaticCore.Load(t.File)));

    /// <summary>All 98 artwork cores load; loading itself is the duplicate-ID check.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void All_98_static_cores_load_with_no_duplicate_IDs()
    {
        Require.Artwork();
        var cores = Loaded().ToList();
        Assert.Equal(Corpus.ExpectedStaticCores, cores.Count);
    }

    /// <summary>Each core carries exactly one Constants; the version word is a plausible timestamp.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Each_core_has_exactly_one_constants_and_version_is_a_timestamp()
    {
        Require.Artwork();
        foreach (var (_, f, c) in Loaded())
        {
            Assert.Equal(1, f.CountOf(IgcObjectType.Constants));
            Assert.Equal(DataConstantsIGC.FcidMax, c.Constants.FloatConstants.Length);
            Assert.Equal(DataConstantsIGC.DmgidMax, c.Constants.DamageConstants.Length);
            Assert.InRange(c.Version, 631152000, 4102444800L);
        }
    }

    /// <summary>
    /// Per-dictionary counts must exactly reproduce IgcReader's raw histogram
    /// for the same file. Cores carry no map/runtime objects, so this covers
    /// every record. <c>OT_partType</c> dispatches into two buckets by size
    /// (launcher vs part), so its raw count equals the two summed.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Per_type_counts_equal_the_raw_record_histogram()
    {
        Require.Artwork();
        foreach (var (path, f, c) in Loaded())
        {
            var raw = f.Records.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count());
            void Check(IgcObjectType type, int actual)
            {
                var expected = raw.TryGetValue(type, out var n) ? n : 0;
                Assert.True(expected == actual,
                    $"{Path.GetFileName(path)}: {type} count {actual} != raw {expected}");
            }

            Check(IgcObjectType.ProjectileType, c.ProjectileTypes.Count);
            Check(IgcObjectType.MissileType, c.MissileTypes.Count);
            Check(IgcObjectType.MineType, c.MineTypes.Count);
            Check(IgcObjectType.ProbeType, c.ProbeTypes.Count);
            Check(IgcObjectType.ChaffType, c.ChaffTypes.Count);
            Check(IgcObjectType.Civilization, c.Civilizations.Count);
            Check(IgcObjectType.HullType, c.HullTypes.Count);
            Check(IgcObjectType.PartType, c.PartTypes.Count + c.Launchers.Count);
            Check(IgcObjectType.StationType, c.StationTypes.Count);
            Check(IgcObjectType.Development, c.Developments.Count);
            Check(IgcObjectType.DroneType, c.DroneTypes.Count);
            Check(IgcObjectType.TreasureSet, c.TreasureSets.Count);
            Check(IgcObjectType.Constants, 1);
        }
    }
}
