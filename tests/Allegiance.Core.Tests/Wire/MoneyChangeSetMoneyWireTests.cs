using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P118 economy-sync codecs
/// <see cref="MoneyChange"/> (FM_S_MONEY_CHANGE, 60) and
/// <see cref="SetMoney"/> (FM_S_SET_MONEY, 115). Layouts are pinned against
/// the native struct arithmetic (Messages.h:441-445 and :751-754, default
/// MSVC packing, Money = 32-bit AGCMoney), composed with the declarative
/// <see cref="ByteBuf"/> builder — not hand-typed hex — so a regression in
/// either encoder surfaces as a byte mismatch against an independently-derived
/// expectation, and a drift between FixedSize and the actual wire width as a
/// failing length assert.
/// </summary>
public class MoneyChangeSetMoneyWireTests
{
    // ------------------------------------------------------------------ //
    // MoneyChange (FM_S_MONEY_CHANGE, 60)                                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public void MoneyChange_FixedSize_IsTwelve()
    {
        // Arithmetic: hdr(4) + Money dMoney(4) + sidTo(2) + sidFrom(2) = 12.
        // No alignment gaps: dMoney's 4-byte Money starts at offset 4, both
        // 2-byte ShipIDs sit back-to-back at 8-11, and the struct rounds to 12
        // (its own natural width) — so no trailing pad, unlike SetMoney.
        Assert.Equal(12, MoneyChange.FixedSize);
    }

    [Fact]
    public void MoneyChange_RoundTrips_AllThreeFields()
    {
        var msg = new MoneyChange { DMoney = 909090, SidTo = 911, SidFrom = -1 /* NA */ };
        var d = MoneyChange.Decode(msg.Encode());
        Assert.Equal(MoneyChange.Fmid, d.GetFmid());
        Assert.Equal(909090, d.DMoney);
        Assert.Equal((short)911, d.SidTo);
        Assert.Equal((short)-1, d.SidFrom);
    }

    [Fact]
    public void MoneyChange_EmitsExactBytes()
    {
        var msg = new MoneyChange { DMoney = -500, SidTo = 912, SidFrom = -1 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);      // cbmsg = 12
            w.U16(60);      // fmid
            w.I32(-500);    // dMoney (offset 4)
            w.I16(912);     // sidTo   (offset 8)
            w.I16(-1);      // sidFrom (offset 10) — NA
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void MoneyChange_RejectsWrongFmid()
    {
        var bytes = new MoneyChange { DMoney = 913, SidTo = 914, SidFrom = 915 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => MoneyChange.Decode(badFmid));
    }

    [Fact]
    public void MoneyChange_RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => MoneyChange.Decode(new byte[11]));
        Assert.Throws<ArgumentException>(() => MoneyChange.Decode([]));
    }

    [Fact]
    public void MoneyChange_Rejects_MismatchedCbmsg()
    {
        var bytes = new MoneyChange { DMoney = 918, SidTo = 919, SidFrom = 922 }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 0x40;   // 64 != 12 → cbmsg mismatch → reject
        Assert.Throws<ArgumentException>(() => MoneyChange.Decode(badCb));
    }

    // ------------------------------------------------------------------ //
    // SetMoney (FM_S_SET_MONEY, 115)                                      //
    // ------------------------------------------------------------------ //

    [Fact]
    public void SetMoney_FixedSize_IsTwelve_IncludingTrailingPad()
    {
        // Arithmetic: hdr(4) + Money(4) + ShipID(2) = 10 field bytes, but the
        // struct's largest member (the 4-byte Money long) forces sizeof up to a
        // multiple of 4 → 12, with 2 trailing pad bytes after shipID
        // (Messages.h:751-754, default MSVC packing; cf. the same trailing-pad
        // rule MissileLaunchData.Size applies, so cbmsg = sizeof = 12 goes on
        // the wire verbatim per messagecore.h:714,724).
        Assert.Equal(12, SetMoney.FixedSize);
    }

    [Fact]
    public void SetMoney_RoundTrips_AllFields()
    {
        var msg = new SetMoney { Money = 981234, ShipId = 992 };
        var d = SetMoney.Decode(msg.Encode());
        Assert.Equal(SetMoney.Fmid, d.GetFmid());
        Assert.Equal(981234, d.Money);
        Assert.Equal((short)992, d.ShipId);
    }

    [Fact]
    public void SetMoney_EmitsExactBytes_WithTwoTrailingZeroPads()
    {
        var msg = new SetMoney { Money = -7, ShipId = 995 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);      // cbmsg = 12 (sizeof FMD_S_SET_MONEY)
            w.U16(115);     // fmid
            w.I32(-7);      // money (offset 4)
            w.I16(995);     // shipID (offset 8)
            w.Pad(2);       // offset 10-11: trailing struct pad (native sizeof)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void SetMoney_RejectsWrongFmid()
    {
        var bytes = new SetMoney { Money = 997, ShipId = 998 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => SetMoney.Decode(badFmid));
    }

    [Fact]
    public void SetMoney_RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => SetMoney.Decode(new byte[11]));
        Assert.Throws<ArgumentException>(() => SetMoney.Decode([]));
    }

    [Fact]
    public void SetMoney_Rejects_MismatchedCbmsg()
    {
        var bytes = new SetMoney { Money = 991, ShipId = 994 }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 0x55;   // 85 != 12 → cbmsg mismatch → reject
        Assert.Throws<ArgumentException>(() => SetMoney.Decode(badCb));
    }
}
