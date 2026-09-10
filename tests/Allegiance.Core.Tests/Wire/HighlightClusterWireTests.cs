using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P142 codec <see cref="HighlightCluster"/>
/// (FM_CS_HIGHLIGHT_CLUSTER, 199). Shape: header(4) + <c>clusterID</c> short
/// + <c>highlight</c> C++ bool (1 byte) = 7 B fixed, no var items
/// (<c>Messages.h:1033-1036</c>) — the same short+bool layout as the sibling
/// <c>TELEPORT_ACK(102)</c>. Byte expectations are built with
/// <see cref="ByteBuf"/>, not hand-typed hex.
/// </summary>
public class HighlightClusterWireTests
{
    [Theory]
    [InlineData((short)13, true)]
    [InlineData((short)-1, false)]
    [InlineData((short)0, true)]
    [InlineData((short)29999, false)]
    public void HighlightCluster_RoundTrips(short clusterId, bool highlight)
    {
        var d = HighlightCluster.Decode(new HighlightCluster { ClusterId = clusterId, Highlight = highlight }.Encode());
        Assert.Equal(HighlightCluster.Fmid, d.GetFmid());
        Assert.Equal(clusterId, d.ClusterId);
        Assert.Equal(highlight, d.Highlight);
    }

    [Fact]
    public void HighlightCluster_EmitsExactBytes()
    {
        var got = new HighlightCluster { ClusterId = 520, Highlight = true }.Encode();
        Assert.Equal(7, got.Length);
        var exp = ByteBuf.Build(7, w =>
        {
            w.U16(7);       // cbmsg
            w.U16(199);     // fmid
            w.I16(520);     // clusterID
            w.Byte(1);      // highlight (C++ bool)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void HighlightCluster_HighlightFalse_IsZeroByte()
    {
        var got = new HighlightCluster { ClusterId = 999, Highlight = false }.Encode();
        Assert.Equal(0, got[6]);
    }

    [Fact]
    public void HighlightCluster_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new HighlightCluster { ClusterId = 5, Highlight = true }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 222;
        Assert.Throws<ArgumentException>(() => HighlightCluster.Decode(bad));
        Assert.Throws<ArgumentException>(() => HighlightCluster.Decode(new byte[6]));
    }
}
