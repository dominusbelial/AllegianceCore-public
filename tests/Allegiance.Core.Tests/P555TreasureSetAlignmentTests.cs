using Allegiance.Core;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P555 — <c>DataTreasureSetIGC</c> had the same decode bug class as the hull
/// record: its <c>char name[25]</c> (odd) is followed by a <c>short</c>, and
/// native alignment (<c>src/Igc/igc.h:2100-2118</c>) puts <c>treasureSetID</c>
/// at <b>offset 26</b> (byte 25 is the pad), <c>nTreasureData</c> at 28,
/// <c>bZoneOnly</c> at 30, entries at 32. The port read <c>treasureSetID</c> at
/// 25 and <c>bZoneOnly</c> at 29 (off by one), yielding a wrong set id.
///
/// These tests FAIL on the pre-fix code (offset 25/29) and PASS at 26/30. They
/// pin the native alignment directly against the raw file bytes for every
/// OT_treasureSet record in the corpus.
/// </summary>
public sealed class P555TreasureSetAlignmentTests
{
    private static byte[] RawPayload(string path, IgcFile f, IgcRecord r)
    {
        var raw = File.ReadAllBytes(path);
        var body = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
        return raw[(body + r.PayloadOffset)..(body + r.PayloadOffset + r.Size)];
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TreasureSetId_ComesFromNativeOffset26_AcrossCorpus()
    {
        Require.Artwork();
        int total = 0;
        foreach (var path in Corpus.Files)
        {
            var f = IgcReader.ParseFile(path);
            foreach (var r in f.Records.Where(r => r.Type == IgcObjectType.TreasureSet))
            {
                total++;
                var pay = RawPayload(path, f, r);
                var decoded = DataTreasureSetIGC.Decode(pay);
                short nativeId = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pay.AsSpan(26, 2));
                Assert.Equal(nativeId, decoded.TreasureSetId);
                // Sanity: a real treasure-set id is small and positive.
                Assert.InRange(decoded.TreasureSetId, 1, short.MaxValue);
                Assert.Equal(pay[30] != 0, decoded.BZoneOnly);
            }
        }
        Assert.Equal(99, total); // one per static core (PayloadDecodeTests counts 99)
    }

    /// <summary>Native <c>nTreasureData</c> at offset 28 always equals the
    /// size-derived entry count — proving the head is native-aligned and that
    /// the previous "count@27 = 256·entries" observation was the off-by-one
    /// read, not a writer quirk.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NativeCountAt28_MatchesSizeDerived_AcrossCorpus()
    {
        Require.Artwork();
        int total = 0;
        foreach (var path in Corpus.Files)
        {
            var f = IgcReader.ParseFile(path);
            foreach (var r in f.Records.Where(r => r.Type == IgcObjectType.TreasureSet))
            {
                total++;
                var pay = RawPayload(path, f, r);
                int derived = (pay.Length - DataTreasureSetIGC.HeadSize) / DataTreasureSetIGC.EntryStride;
                short countAt28 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pay.AsSpan(28, 2));
                Assert.Equal(derived, countAt28);
            }
        }
        Assert.True(total >= 95, $"expected ~99 treasure-set records, saw {total}");
    }
}
