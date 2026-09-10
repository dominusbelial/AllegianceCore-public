using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P140 promote wire codecs:
/// <see cref="Promote"/> (FM_S_PROMOTE, 189) and <see cref="PromoteRequest"/>
/// (FM_C_PROMOTE, 190). Both are the trivial shape
/// <c>{ ushort cbmsg; ushort fmid; <i>payload</i>; }</c> and are 6 bytes fixed:
/// PROMOTE-S carries one <c>ShipID</c> short (2-aligned, no padding);
/// PROMOTE-C carries a single <c>Mount</c> char at offset 4 followed by 1 byte
/// of alignment padding (the C++ struct's alignment 2 rounds <c>sizeof</c> to
/// 6). Matches <c>Messages.h:975-981</c>. Byte expectations are built with
/// <see cref="ByteBuf"/>, not hand-typed hex.
/// </summary>
public class PromoteWireTests
{
    // ---- Promote (S, 189): shipidPromoted ---------------------------------

    [Fact]
    public void Promote_RoundTrips_ShipidPromoted()
    {
        var msg = new Promote { ShipidPromoted = 733 };
        var d = Promote.Decode(msg.Encode());
        Assert.Equal(Promote.Fmid, d.GetFmid());
        Assert.Equal((short)733, d.ShipidPromoted);
    }

    [Fact]
    public void Promote_EmitsExactBytes()
    {
        var msg = new Promote { ShipidPromoted = 734 };
        var got = msg.Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);    // cbmsg
            w.U16(189);  // fmid
            w.I16(734);  // shipidPromoted
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void Promote_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new Promote { ShipidPromoted = 741 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 201;
        Assert.Throws<ArgumentException>(() => Promote.Decode(bad));
        Assert.Throws<ArgumentException>(() => Promote.Decode(new byte[5]));
    }

    // ---- PromoteRequest (C, 190): mountidPromoted (Mount char + pad) ------

    [Fact]
    public void PromoteRequest_RoundTrips_MountidPromoted()
    {
        var msg = new PromoteRequest { MountidPromoted = 3 };
        var d = PromoteRequest.Decode(msg.Encode());
        Assert.Equal(PromoteRequest.Fmid, d.GetFmid());
        Assert.Equal((short)3, d.MountidPromoted);
    }

    [Fact]
    public void PromoteRequest_EmitsExactBytes_CharPlusPad()
    {
        var msg = new PromoteRequest { MountidPromoted = 2 };
        var got = msg.Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);    // cbmsg
            w.U16(190);  // fmid
            w.Byte(2);   // offset 4: char mountidPromoted
            w.Byte(0);   // offset 5: alignment padding
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void PromoteRequest_WidensSignedChar_IntoShort()
    {
        // A negative Mount char (e.g. -1 = NA) widens to the signed short -1,
        // so the handler can compare directly against Ship.TurretId.
        var msg = new PromoteRequest { MountidPromoted = -1 };
        Assert.Equal((short)-1, PromoteRequest.Decode(msg.Encode()).MountidPromoted);
    }

    [Fact]
    public void PromoteRequest_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new PromoteRequest { MountidPromoted = 4 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 203;
        Assert.Throws<ArgumentException>(() => PromoteRequest.Decode(bad));
        Assert.Throws<ArgumentException>(() => PromoteRequest.Decode(new byte[5]));
    }
}
