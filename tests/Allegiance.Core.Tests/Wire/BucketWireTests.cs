using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P119 bucket-construction-funding
/// codecs: <see cref="BucketDonate"/> (FM_C_BUCKET_DONATE, 64),
/// <see cref="BucketStatus"/> (FM_S_BUCKET_STATUS, 65) and
/// <see cref="CreateBuckets"/> (FM_S_CREATE_BUCKETS, 191). Layouts are pinned
/// against the native struct arithmetic (Messages.h:488-498 and :983-986,
/// default MSVC packing; Money = 32-bit AGCMoney; SideID/BucketID = 16-bit
/// short; DWORD = uint32; TechTreeBitMask = u8[50], zlib/mask.h:351), composed
/// with the declarative <see cref="ByteBuf"/> builder so a regression in either
/// encoder surfaces as a byte mismatch against an independently-derived
/// expectation.
/// </summary>
public class BucketWireTests
{
    // ------------------------------------------------------------------ //
    // BucketDonate (FM_C_BUCKET_DONATE, 64)                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void BucketDonate_FixedSize_IsTwelve()
    {
        // Arithmetic: hdr(4) + Money moneyGiven(4) + BucketID iBucket(2) + 2
        // trailing pad (the single trailing short rounds sizeof up to the 4-byte
        // Money alignment, exactly the SetMoney rule) = 12.
        Assert.Equal(12, BucketDonate.FixedSize);
    }

    [Fact]
    public void BucketDonate_RoundTrips_BothFields()
    {
        var msg = new BucketDonate { MoneyGiven = 75000, IBucket = 3 };
        var d = BucketDonate.Decode(msg.Encode());
        Assert.Equal(BucketDonate.Fmid, d.GetFmid());
        Assert.Equal(75000, d.MoneyGiven);
        Assert.Equal((short)3, d.IBucket);
    }

    [Fact]
    public void BucketDonate_EmitsExactBytes()
    {
        var msg = new BucketDonate { MoneyGiven = 1500, IBucket = 1 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);      // cbmsg = 12
            w.U16(64);      // fmid
            w.I32(1500);    // moneyGiven (offset 4)
            w.I16(1);       // iBucket    (offset 8)
            w.Pad(2);       // trailing pad (offset 10)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BucketDonate_RejectsTruncatedFrame()
    {
        var raw = new BucketDonate { MoneyGiven = 1, IBucket = 1 }.Encode();
        Assert.Throws<ArgumentException>(() => BucketDonate.Decode(raw.AsSpan(0, 8)));
    }

    [Fact]
    public void BucketDonate_RejectsWrongFmid()
    {
        var raw = new BucketDonate { MoneyGiven = 1, IBucket = 1 }.Encode();
        raw[2] = 0; raw[3] = 66;   // corrupt fmid 64 -> 66
        Assert.Throws<ArgumentException>(() => BucketDonate.Decode(raw));
    }

    // ------------------------------------------------------------------ //
    // BucketStatus (FM_S_BUCKET_STATUS, 65)                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void BucketStatus_FixedSize_IsTwenty()
    {
        // Arithmetic: hdr(4) + DWORD timeTotal(4) + SideID sideID(2) + pad(2)
        // + Money moneyTotal(4) + BucketID iBucket(2) + trailing pad(2) = 20.
        Assert.Equal(20, BucketStatus.FixedSize);
    }

    [Fact]
    public void BucketStatus_RoundTrips_AllFourFields()
    {
        var msg = new BucketStatus
        {
            TimeTotal = 456000u,
            SideId = 1,
            MoneyTotal = 7890,
            IBucket = 4,
        };
        var d = BucketStatus.Decode(msg.Encode());
        Assert.Equal(BucketStatus.Fmid, d.GetFmid());
        Assert.Equal(456000u, d.TimeTotal);
        Assert.Equal((short)1, d.SideId);
        Assert.Equal(7890, d.MoneyTotal);
        Assert.Equal((short)4, d.IBucket);
    }

    [Fact]
    public void BucketStatus_EmitsExactBytes()
    {
        var msg = new BucketStatus { TimeTotal = 377777u, SideId = 2, MoneyTotal = 555, IBucket = 9 };
        var got = msg.Encode();

        var exp = ByteBuf.Build(20, w =>
        {
            w.U16(20);      // cbmsg = 20
            w.U16(65);      // fmid
            w.U32(377777);  // timeTotal (offset 4)
            w.I16(2);       // sideID    (offset 8)
            w.Pad(2);       // interior pad (offset 10, Money 4-align)
            w.I32(555);     // moneyTotal (offset 12)
            w.I16(9);       // iBucket    (offset 16)
            w.Pad(2);       // trailing pad (offset 18)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BucketStatus_RejectsTruncatedFrame()
    {
        var raw = new BucketStatus { TimeTotal = 1, SideId = 0, MoneyTotal = 1, IBucket = 1 }.Encode();
        Assert.Throws<ArgumentException>(() => BucketStatus.Decode(raw.AsSpan(0, 16)));
    }

    // ------------------------------------------------------------------ //
    // CreateBuckets (FM_S_CREATE_BUCKETS, 191)                           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void CreateBuckets_FixedSize_IsOneHundredFour()
    {
        // Arithmetic: hdr(4) + TechTreeBitMask ttbmDevelopments(50) +
        // TechTreeBitMask ttbmInitial(50) = 104. TechTreeBitMask is
        // uint8_t[((400-1)/8)+1] = [50] bytes (zlib/mask.h:351), all byte
        // aligned — no padding.
        Assert.Equal(4 + 2 * CreateBuckets.MaskBytes, CreateBuckets.FixedSize);
        Assert.Equal(50, CreateBuckets.MaskBytes);
        Assert.Equal(104, CreateBuckets.FixedSize);
    }

    [Fact]
    public void CreateBuckets_DefaultsToZeroedMasks()
    {
        // The port has no tech-tree infrastructure (the P117 finding), so a
        // freshly-constructed frame carries both 50-byte masks all-zero —
        // exactly what a cleared TechTreeBitMask is (mask.h:149-152).
        var d = CreateBuckets.Decode(new CreateBuckets().Encode());
        Assert.All(d.TtbmDevelopments, b => Assert.Equal(0, b));
        Assert.All(d.TtbmInitial, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateBuckets_EmitsExactZeroedBytes()
    {
        var got = new CreateBuckets().Encode();

        var exp = ByteBuf.Build(104, w =>
        {
            w.U16(104);     // cbmsg = 104
            w.U16(191);     // fmid
            w.Pad(50);      // ttbmDevelopments (offset 4)
            w.Pad(50);      // ttbmInitial      (offset 54)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void CreateBuckets_RoundTrips_NonzeroMasks()
    {
        // Even though the port transmits zeroed masks, the codec must be
        // byte-faithful to the 50-byte mask type so a future tech-aware task can
        // populate real bits — verify an arbitrary bit-pattern round-trips.
        var dev = new byte[50]; dev[0] = 0xAB; dev[49] = 0x7F;
        var init = new byte[50]; init[17] = 0x01;
        var msg = new CreateBuckets { TtbmDevelopments = dev, TtbmInitial = init };
        var d = CreateBuckets.Decode(msg.Encode());
        Assert.Equal(dev, d.TtbmDevelopments);
        Assert.Equal(init, d.TtbmInitial);
    }

    [Fact]
    public void CreateBuckets_RejectsTruncatedFrame()
    {
        var raw = new CreateBuckets().Encode();
        Assert.Throws<ArgumentException>(() => CreateBuckets.Decode(raw.AsSpan(0, 60)));
    }
}
