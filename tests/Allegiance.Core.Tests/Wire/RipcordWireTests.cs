using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P111 ripcord wire codecs:
/// <see cref="RipcordRequest"/> (FM_C_RIPCORD_REQUEST 184),
/// <see cref="RipcordActivate"/> (FM_S_RIPCORD_ACTIVATE 185),
/// <see cref="RipcordDenied"/> (FM_S_RIPCORD_DENIED 186) and
/// <see cref="RipcordAborted"/> (FM_S_RIPCORD_ABORTED 187). All four layouts
/// come from <c>src/Inc/Messages.h:951-967</c> and the type typedefs
/// (<c>src/Igc/igc.h:115,477,487</c>: <c>ObjectType</c>, <c>ObjectID</c> =
/// <c>ShipID</c> = <c>SectorID</c> all <c>short</c>). The existing suite's
/// declarative <see cref="ByteBuf"/> builder pins the exact emitted bytes.
/// </summary>
public class RipcordWireTests
{
    // ------------------------------------------------------------------ //
    // RipcordRequest (FM_C_RIPCORD_REQUEST, 184) — cbmsg = 6             //
    // ------------------------------------------------------------------ //

    [Fact]
    public void RipcordRequest_FixedSize_IsSix()
    {
        // hdr(4) + sidRipcord short (2) = 6, no padding.
        Assert.Equal(6, RipcordRequest.FixedSize);
        Assert.Equal((ushort)184, RipcordRequest.Fmid);
    }

    [Fact]
    public void RipcordRequest_RoundTrips_RealSector()
    {
        var d = RipcordRequest.Decode(new RipcordRequest { SidRipcord = 1054 }.Encode());
        Assert.Equal((short)1054, d.SidRipcord);
    }

    [Fact]
    public void RipcordRequest_RoundTrips_NA_Cancel()
    {
        var d = RipcordRequest.Decode(new RipcordRequest { SidRipcord = -1 }.Encode());
        Assert.Equal((short)-1, d.SidRipcord);   // NA — cancel
    }

    [Fact]
    public void RipcordRequest_EmitsExactBytes()
    {
        var got = new RipcordRequest { SidRipcord = 0x4123 }.Encode();
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);      // cbmsg
            w.U16(184);    // fmid
            w.I16(0x4123); // sidRipcord (SectorID short)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void RipcordRequest_RejectsWrongFmid_And_ShortBuffer()
    {
        var bytes = new RipcordRequest { SidRipcord = 811 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 99;
        Assert.Throws<ArgumentException>(() => RipcordRequest.Decode(bad));
        Assert.Throws<ArgumentException>(() => RipcordRequest.Decode(new byte[5]));
    }

    // ------------------------------------------------------------------ //
    // RipcordActivate (FM_S_RIPCORD_ACTIVATE, 185) — cbmsg = 12          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void RipcordActivate_FixedSize_IsTwelve()
    {
        // hdr(4) + four shorts (shipidRipcord, otRipcord, oidRipcord,
        // sidRipcord) = 12, no padding.
        Assert.Equal(12, RipcordActivate.FixedSize);
        Assert.Equal((ushort)185, RipcordActivate.Fmid);
    }

    [Fact]
    public void RipcordActivate_RoundTrips_AllFourFields()
    {
        var d = RipcordActivate.Decode(new RipcordActivate
        {
            ShipidRipcord = 8330, OtRipcord = 1, OidRipcord = 8150, SidRipcord = 8129,
        }.Encode());
        Assert.Equal((short)8330, d.ShipidRipcord);
        Assert.Equal((short)1, d.OtRipcord);          // OT_station
        Assert.Equal((short)8150, d.OidRipcord);
        Assert.Equal((short)8129, d.SidRipcord);
    }

    [Fact]
    public void RipcordActivate_EmitsExactBytes()
    {
        var got = new RipcordActivate
        {
            ShipidRipcord = 8250, OtRipcord = 8260, OidRipcord = 8270, SidRipcord = 8280,
        }.Encode();
        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);    // cbmsg
            w.U16(185);   // fmid
            w.I16(8250);  // shipidRipcord
            w.I16(8260);  // otRipcord   (ObjectType short)
            w.I16(8270);  // oidRipcord
            w.I16(8280);  // sidRipcord  (SectorID short)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void RipcordActivate_RejectsWrongFmid_And_ShortBuffer()
    {
        var bytes = new RipcordActivate { ShipidRipcord = 8300, OtRipcord = 8310, OidRipcord = 8320, SidRipcord = 8400 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 254;
        Assert.Throws<ArgumentException>(() => RipcordActivate.Decode(bad));
        Assert.Throws<ArgumentException>(() => RipcordActivate.Decode(new byte[11]));
    }

    // ------------------------------------------------------------------ //
    // RipcordDenied (FM_S_RIPCORD_DENIED, 186) — header-only, cbmsg = 4  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void RipcordDenied_IsHeaderOnly()
    {
        Assert.Equal(4, RipcordDenied.FixedSize);
        Assert.Equal((ushort)186, RipcordDenied.Fmid);
        var got = new RipcordDenied().Encode();
        var exp = ByteBuf.Build(4, w => { w.U16(4); w.U16(186); });
        Assert.Equal(exp, got);
        Assert.Equal(RipcordDenied.Fmid, RipcordDenied.Decode(got).GetFmid());
    }

    [Fact]
    public void RipcordDenied_RejectsWrongFmid_And_ShortBuffer()
    {
        var bad = new byte[] { 4, 0, 077, 0 };   // cbmsg=4, fmid != 186
        Assert.Throws<ArgumentException>(() => RipcordDenied.Decode(bad));
        Assert.Throws<ArgumentException>(() => RipcordDenied.Decode(new byte[3]));
    }

    // ------------------------------------------------------------------ //
    // RipcordAborted (FM_S_RIPCORD_ABORTED, 187) — cbmsg = 6            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void RipcordAborted_FixedSize_IsSix()
    {
        Assert.Equal(6, RipcordAborted.FixedSize);
        Assert.Equal((ushort)187, RipcordAborted.Fmid);
    }

    [Fact]
    public void RipcordAborted_RoundTrips_ShipId()
    {
        var d = RipcordAborted.Decode(new RipcordAborted { ShipidRipcord = 8420 }.Encode());
        Assert.Equal((short)8420, d.ShipidRipcord);
    }

    [Fact]
    public void RipcordAborted_EmitsExactBytes()
    {
        var got = new RipcordAborted { ShipidRipcord = 8460 }.Encode();
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);     // cbmsg
            w.U16(187);   // fmid
            w.I16(8460);  // shipidRipcord
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void RipcordAborted_RejectsWrongFmid_And_ShortBuffer()
    {
        var bytes = new RipcordAborted { ShipidRipcord = 8490 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 7;
        Assert.Throws<ArgumentException>(() => RipcordAborted.Decode(bad));
        Assert.Throws<ArgumentException>(() => RipcordAborted.Decode(new byte[5]));
    }
}
