using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// Corpus-wide round-trip for <see cref="DataWarpIGC.Encode"/> /
/// <see cref="DataWarpIGC.Decode"/>. Every OT_warp record in every map
/// (5212 records across 111 maps — the corpus baseline) is decoded from its
/// .igc bytes, re-encoded, and decoded again; the two decoded views must agree
/// on every field. This pins the encoder to the decoder (and therefore to the
/// corpus) field-by-field, complementing the hand-composed byte-exact test in
/// <c>ExportWarpWireTests</c>. There is no always-false-on-export field for a
/// warp (CwarpIGC::Export, warpIGC.cpp:112-130, copies raw), so every field
/// must round-trip identically.
/// </summary>
[Trait("Requires","Artwork")]
public class DataWarpIGCRoundTripTests
{
    [SkippableTheory]
    [MemberData(nameof(AllMaps))]
    public void EveryWarpRecord_RoundTrips_ThroughEncodeDecode(string path)
    {
        Require.Artwork();
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.DataSize) return;   // static cores etc.: not a map, skip (like CorpusValidateTests)
        var map = MissionMap.Load(file);

        foreach (var w in map.Warps)
        {
            // Actually exercise Encode: the decoded record back to bytes...
            var back = DataWarpIGC.Decode(w.Encode());
            AssertFieldEqual(w, back);
        }
    }

    private static void AssertFieldEqual(DataWarpIGC a, DataWarpIGC b)
    {
        Assert.Equal(a.WarpId, b.WarpId);
        Assert.Equal(a.DestinationId, b.DestinationId);
        Assert.Equal(a.Radius, b.Radius);
        Assert.Equal(a.TextureName, b.TextureName);
        Assert.Equal(a.IconName, b.IconName);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Forward, b.Forward);
        Assert.Equal(a.Rotation, b.Rotation);
        Assert.Equal(a.Signature, b.Signature);
        Assert.Equal(a.ClusterId, b.ClusterId);
    }

    public static TheoryData<string> AllMaps() => Corpus.Theory();
}
