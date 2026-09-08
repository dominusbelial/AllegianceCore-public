using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P101 wire codec
/// <see cref="WarpBomb"/> (FM_S_WARP_BOMB 188). The layout is pinned against a
/// programmatically-built expected buffer (the declarative <see cref="ByteBuf"/>
/// builder), so a regression in the encoder surfaces as a byte mismatch against
/// an independently-derived expectation and a drift between
/// <see cref="WarpBomb.FixedSize"/> and the actual encode as a failing length
/// assert. Field widths follow the ObjectID-family convention: <c>Time</c> →
/// <c>uint</c> (this project's uint-Time wire convention), <c>WarpID</c> and
/// <c>ExpendableTypeID</c> → <c>short</c> (both derive from <c>ObjectID</c>,
/// <c>igc.h:477,488,497</c>).
/// </summary>
public class WarpBombWireCodecsWireTests
{
    [Fact]
    public void WarpBomb_FixedSize_IsTwelve()
    {
        // hdr(4) + timeExplosion(4) + warpid(2) + expendableid(2) = 12.
        // Time is 4-aligned at offset 4 (right after the header); the two 2-byte
        // IDs sit back-to-back at 8-11; largest-member alignment 4 → no padding.
        Assert.Equal(12, WarpBomb.FixedSize);
    }

    [Fact]
    public void WarpBomb_Fmid_Is188()
    {
        Assert.Equal((ushort)188, WarpBomb.Fmid);
        Assert.Equal((ushort)188, new WarpBomb().GetFmid());
    }

    [Fact]
    public void WarpBomb_RoundTrips_ThreeFields()
    {
        var msg = new WarpBomb { TimeExplosion = 999000u, WarpIdBombed = 917, ExpendableIdMissile = 913 };
        var d = WarpBomb.Decode(msg.Encode());
        Assert.Equal(999000u, d.TimeExplosion);
        Assert.Equal(917, d.WarpIdBombed);
        Assert.Equal(913, d.ExpendableIdMissile);
    }

    [Fact]
    public void WarpBomb_EmitsExactBytes()
    {
        var msg = new WarpBomb { TimeExplosion = 918000u, WarpIdBombed = 919, ExpendableIdMissile = 912 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);      // cbmsg
            w.U16(188);     // fmid
            w.U32(918000);  // timeExplosion (Time — uint, this project's convention)
            w.I16(919);     // warpidBombed (WarpID, ObjectID typedef)
            w.I16(912);     // expendableidMissile (ExpendableTypeID, ObjectID typedef)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void WarpBomb_RejectsWrongFmid()
    {
        var bytes = new WarpBomb { TimeExplosion = 923u, WarpIdBombed = 924, ExpendableIdMissile = 925 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 234;
        Assert.Throws<ArgumentException>(() => WarpBomb.Decode(badFmid));
    }

    [Fact]
    public void WarpBomb_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => WarpBomb.Decode(new byte[11]));
    }

    [Fact]
    public void WarpBomb_Decode_RejectsCbMsgMismatch()
    {
        var bytes = new WarpBomb { TimeExplosion = 926u, WarpIdBombed = 927, ExpendableIdMissile = 928 }.Encode();
        // Corrupt cbmsg so it disagrees with the buffer length → reject.
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 13;
        Assert.Throws<ArgumentException>(() => WarpBomb.Decode(badCb));
    }
}
