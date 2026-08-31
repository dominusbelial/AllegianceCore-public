using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// Corpus-wide round-trip for <see cref="DataClusterIGC.Encode"/> /
/// <see cref="DataClusterIGC.Decode"/>. Every OT_cluster record in every map
/// (1506 records across 111 maps — the corpus baseline) is decoded from its
/// .igc bytes, re-encoded, and decoded again; the two decoded views must agree
/// on every field. This pins the encoder to the decoder (and therefore to the
/// corpus) field-by-field, complementing the hand-composed byte-exact test in
/// <c>ExportClusterWireTests</c>.
/// </summary>
public class DataClusterIGCRoundTripTests
{
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void EveryClusterRecord_RoundTrips_ThroughEncodeDecode(string path)
    {
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.DataSize) return;   // static cores etc.: not a map, skip (like CorpusValidateTests)
        var map = MissionMap.Load(file);

        Assert.NotEmpty(map.Clusters);                        // a map has ≥1 sector
        foreach (var c in map.Clusters)
        {
            // Actually exercise Encode: the decoded record back to bytes...
            var back = DataClusterIGC.Decode(c.Encode());
            AssertFieldEqual(c, back);
        }
    }

    private static void AssertFieldEqual(DataClusterIGC a, DataClusterIGC b)
    {
        Assert.Equal(a.StarSeed, b.StarSeed);
        Assert.Equal(a.LightDirection, b.LightDirection);
        Assert.Equal(a.LightColor, b.LightColor);
        Assert.Equal(a.ScreenX, b.ScreenX);
        Assert.Equal(a.ScreenY, b.ScreenY);
        Assert.Equal(a.ClusterId, b.ClusterId);
        Assert.Equal(a.Debris, b.Debris);
        Assert.Equal(a.Stars, b.Stars);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.PosterName, b.PosterName);
        Assert.Equal(a.PlanetName, b.PlanetName);
        Assert.Equal(a.PlanetSinLatitude, b.PlanetSinLatitude);
        Assert.Equal(a.PlanetLongitude, b.PlanetLongitude);
        Assert.Equal(a.PlanetRadius, b.PlanetRadius);
        Assert.False(b.Active);   // encoder forces activeF=false on export
        Assert.Equal(a.HomeSector, b.HomeSector);
    }

    public static TheoryData<string> AllMaps() => Corpus.Theory();
}
