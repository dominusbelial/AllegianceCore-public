using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P104 wire-only codecs
/// <see cref="ObjectSpotted"/> (FM_S_OBJECT_SPOTTED, 6) and
/// <see cref="PlayerRescued"/> (FM_S_PLAYER_RESCUED, 16). All layouts are
/// pinned against programmatically-built expected buffers (the declarative
/// builder <see cref="ByteBuf"/> lifted from
/// <c>MissileWireCodecsWireTests.cs</c>), not hand-typed hex — so a regression
/// in either encoder surfaces as a byte mismatch against an independently-
/// derived expectation, and a drift between the codec's FixedSize and the
/// actual wire width as a failing length assert. Both messages are deliberately
/// wire-only: no trigger is wired, exactly like P89/P93/P98's slices.
/// </summary>
public class ObjectSpottedPlayerRescuedWireTests
{
    // ------------------------------------------------------------------ //
    // ObjectSpotted (FM_S_OBJECT_SPOTTED, 6)                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ObjectSpotted_FixedSize_IsTwelve()
    {
        // Arithmetic: hdr(4) + otObject(2) + oidObject(2) + otSpotter(2) +
        // oidSpotter(2) = 12. No alignment gaps: all four members are 2-byte
        // shorts sitting back-to-back at 4-11, no member wider than 2. The two
        // ObjectType members are the native typedef short (igc.h:115) — NOT the
        // 4-byte [v1_enum] int32 that is only the EXPORT envelope's objecttype
        // discriminator (DataProbeExport.cs:50-54), so there is no enum pad.
        Assert.Equal(12, ObjectSpotted.FixedSize);
    }

    [Fact]
    public void ObjectSpotted_RoundTrips_AllFourFields()
    {
        var msg = new ObjectSpotted
        {
            OtObject = 901,      // e.g. OT_station
            OidObject = 555,
            OtSpotter = 508,     // e.g. OT_ship
            OidSpotter = 999,
        };
        var d = ObjectSpotted.Decode(msg.Encode());
        Assert.Equal((short)901, d.OtObject);
        Assert.Equal((short)555, d.OidObject);
        Assert.Equal((short)508, d.OtSpotter);
        Assert.Equal((short)999, d.OidSpotter);
    }

    [Fact]
    public void ObjectSpotted_EmitsExactBytes()
    {
        var msg = new ObjectSpotted { OtObject = 701, OidObject = 705, OtSpotter = 711, OidSpotter = 718 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);     // cbmsg = 12
            w.U16(6);      // fmid
            w.I16(701);    // otObject  (offset 4)
            w.I16(705);    // oidObject (offset 6)
            w.I16(711);    // otSpotter (offset 8)
            w.I16(718);    // oidSpotter (offset 10)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ObjectSpotted_RejectsWrongFmid()
    {
        var bytes = new ObjectSpotted { OtObject = 721, OidObject = 722, OtSpotter = 725, OidSpotter = 726 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => ObjectSpotted.Decode(badFmid));
    }

    [Fact]
    public void ObjectSpotted_RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => ObjectSpotted.Decode(new byte[11]));
        Assert.Throws<ArgumentException>(() => ObjectSpotted.Decode([]));
    }

    [Fact]
    public void ObjectSpotted_Rejects_MismatchedCbmsg()
    {
        // Header-length mismatch (cbmsg set to a wrong value vs actual buffer):
        // the codec's Decode enforces hdr.CbMsg == buf.Length.
        var bytes = new ObjectSpotted { OtObject = 730, OidObject = 731, OtSpotter = 732, OidSpotter = 734 }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 99;                     // lower byte of cbmsg → mismatches length 12
        Assert.Throws<ArgumentException>(() => ObjectSpotted.Decode(badCb));
    }

    // ------------------------------------------------------------------ //
    // PlayerRescued (FM_S_PLAYER_RESCUED, 16)                             //
    // ------------------------------------------------------------------ //

    [Fact]
    public void PlayerRescued_FixedSize_IsEight()
    {
        // Arithmetic: hdr(4) + shipIDRescuer(2) + shipIDRescuee(2) = 8. No
        // alignment gaps: both 2-byte ShipIDs sit back-to-back at 4-7, no
        // member wider than 2.
        Assert.Equal(8, PlayerRescued.FixedSize);
    }

    [Fact]
    public void PlayerRescued_RoundTrips_BothFields()
    {
        var msg = new PlayerRescued { ShipIdRescuer = 402, ShipIdRescuee = 501 };
        var d = PlayerRescued.Decode(msg.Encode());
        Assert.Equal((short)402, d.ShipIdRescuer);
        Assert.Equal((short)501, d.ShipIdRescuee);
    }

    [Fact]
    public void PlayerRescued_EmitsExactBytes()
    {
        var msg = new PlayerRescued { ShipIdRescuer = 615, ShipIdRescuee = 715 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(8, w =>
        {
            w.U16(8);     // cbmsg
            w.U16(16);    // fmid
            w.I16(615);   // shipIDRescuer (offset 4)
            w.I16(715);   // shipIDRescuee (offset 6)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void PlayerRescued_RejectsWrongFmid()
    {
        var bytes = new PlayerRescued { ShipIdRescuer = 620, ShipIdRescuee = 621 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => PlayerRescued.Decode(badFmid));
    }

    [Fact]
    public void PlayerRescued_RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => PlayerRescued.Decode(new byte[7]));
        Assert.Throws<ArgumentException>(() => PlayerRescued.Decode([]));
    }

    [Fact]
    public void PlayerRescued_Rejects_MismatchedCbmsg()
    {
        var bytes = new PlayerRescued { ShipIdRescuer = 625, ShipIdRescuee = 628 }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = (byte)0x59;              // 89 != 8 → cbmsg mismatch → reject
        Assert.Throws<ArgumentException>(() => PlayerRescued.Decode(badCb));
    }
}
