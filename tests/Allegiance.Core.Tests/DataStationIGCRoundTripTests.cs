using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// Corpus-wide round-trip for <see cref="DataStationIGC.Encode"/> /
/// <see cref="DataStationIGC.Decode"/>. Every OT_station record in every map
/// (289 records across 111 maps — the corpus baseline) is decoded from its
/// .igc bytes, re-encoded, and decoded again; the two decoded views must agree
/// on every field. This pins the encoder to the decoder (and therefore to the
/// corpus) field-by-field, complementing the hand-composed byte-exact test in
/// <c>ExportStationWireTests</c>. There is no always-false-on-export field for
/// a station (CstationIGC::Export, stationIGC.cpp:89-113, copies raw), so
/// every field must round-trip identically.
/// </summary>
public class DataStationIGCRoundTripTests
{
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void EveryStationRecord_RoundTrips_ThroughEncodeDecode(string path)
    {
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.DataSize) return;   // static cores etc.: not a map, skip (like CorpusValidateTests)
        var map = MissionMap.Load(file);

        foreach (var s in map.Stations)
        {
            // Actually exercise Encode: the decoded record back to bytes...
            var back = DataStationIGC.Decode(s.Encode());
            AssertFieldEqual(s, back);
        }
    }

    private static void AssertFieldEqual(DataStationIGC a, DataStationIGC b)
    {
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Up, b.Up);
        Assert.Equal(a.Forward, b.Forward);
        Assert.Equal(a.Rotation, b.Rotation);
        Assert.Equal(a.ClusterId, b.ClusterId);
        Assert.Equal(a.SideId, b.SideId);
        Assert.Equal(a.StationId, b.StationId);
        Assert.Equal(a.StationTypeId, b.StationTypeId);
        Assert.Equal(a.BpHull, b.BpHull);
        Assert.Equal(a.BpShield, b.BpShield);
        Assert.Equal(a.Name, b.Name);
    }

    public static TheoryData<string> AllMaps() => Corpus.Theory();
}
