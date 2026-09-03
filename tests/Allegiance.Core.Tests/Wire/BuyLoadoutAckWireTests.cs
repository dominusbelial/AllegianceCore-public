using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="BuyLoadoutAck"/>
/// (FM_S_BUY_LOADOUT_ACK 5), ported from <c>Messages.h:70-74</c> and the
/// compiler-verified <c>ShipLoadout</c> (sizeof=2, hullID offset 0) +
/// MSVC-packed <c>bool:1</c> pair. Wire layout pinned against a hand-composed
/// buffer, not the encoder. nParts=0 everywhere; the decoder rejects any
/// cbLoadout != 2 as a drift. Total = 4 + 4 + 1 + 2 = 11 bytes.
/// </summary>
public class BuyLoadoutAckWireTests
{
    [Fact]
    public void BuyLoadoutAck_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)5, BuyLoadoutAck.Fmid);
        Assert.Equal(2, BuyLoadoutAck.ShipLoadoutSize);  // sizeof(ShipLoadout): compiler-confirmed
        Assert.Equal(11, BuyLoadoutAck.FixedSize);       // 4 hdr + 4 varitem + 1 flags + 2 loadout
    }

    [Fact]
    public void BuyLoadoutAck_RoundTrips_ThroughWire()
    {
        var msg = new BuyLoadoutAck { HullId = 904, FBoughtEverything = false, FLaunch = false };
        var d = BuyLoadoutAck.Decode(msg.Encode());
        Assert.Equal(904, d.HullId);
        Assert.False(d.FBoughtEverything);
        Assert.False(d.FLaunch);

        msg = new BuyLoadoutAck { HullId = -1, FBoughtEverything = true, FLaunch = true };
        d = BuyLoadoutAck.Decode(msg.Encode());
        Assert.Equal(-1, d.HullId);
        Assert.True(d.FBoughtEverything);
        Assert.True(d.FLaunch);
    }

    [Fact]
    public void BuyLoadoutAck_EmitsExactDeclaredBytes()
    {
        var msg = new BuyLoadoutAck { HullId = 905, FBoughtEverything = true, FLaunch = false };
        var got = msg.Encode();

        //                       cbmsg  fmid  ib     cb     flags hullID(le)
        var exp = new byte[] { 0x0B, 0x00, 0x05, 0x00, 0x09, 0x00, 0x02, 0x00, 0x01, 0x89, 0x03 };

        Assert.Equal(11, exp.Length);
        Assert.Equal(exp, got);
    }

    [Theory]
    [InlineData(false, false, 0x00)]
    [InlineData(true, false, 0x01)]   // fBoughtEverything = bit0 only
    [InlineData(false, true, 0x02)]   // fLaunch = bit1 only
    [InlineData(true, true, 0x03)]    // both set
    public void BuyLoadoutAck_FlagBits_AreLowBitsFirst(bool fbought, bool flaunch, byte expect)
    {
        var msg = new BuyLoadoutAck { HullId = 1, FBoughtEverything = fbought, FLaunch = flaunch };
        var got = msg.Encode();
        Assert.Equal(expect, got[8]);                 // flags byte at message offset 8
        var d = BuyLoadoutAck.Decode(got);
        Assert.Equal(fbought, d.FBoughtEverything);
        Assert.Equal(flaunch, d.FLaunch);
    }

    [Fact]
    public void BuyLoadoutAck_WithParts_RoundTripsAndEmitsExactBytes()
    {
        var msg = new BuyLoadoutAck
        {
            HullId = 885,
            FBoughtEverything = true,
            FLaunch = true,
            Parts =
            [
                new ExpandedPartData(PartId: 321, Amount: 12, MountId: 0, FractionMounted: 200),
                new ExpandedPartData(PartId: 333, Amount: 7, MountId: 1, FractionMounted: ExpandedPartData.FullyMounted),
            ],
        };
        var got = msg.Encode();

        //                       cbmsg      fmid  ib     cb     flags hullID(le)  p0: pid   amt mount frac   p1: pid   amt  mount frac
        var exp = new byte[] { 0x17, 0x00, 0x05, 0x00, 0x09, 0x00, 0x0E, 0x00, 0x03, 0x75, 0x03, 0x41, 0x01, 0x0C, 0x00, 0x00, 0xC8, 0x4D, 0x01, 0x07, 0x00, 0x01, 0xF0 };

        Assert.Equal(23, exp.Length);
        Assert.Equal(exp, got);

        var d = BuyLoadoutAck.Decode(got);
        Assert.Equal(885, d.HullId);
        Assert.True(d.FBoughtEverything);
        Assert.True(d.FLaunch);
        Assert.Equal(2, d.Parts.Count);
        Assert.Equal(new ExpandedPartData(321, 12, 0, 200), d.Parts[0]);
        Assert.Equal(new ExpandedPartData(333, 7, 1, 240), d.Parts[1]);
    }

    [Fact]
    public void BuyLoadoutAck_EmptyParts_StillMatchesHistoricalHullOnlyBytes()
    {
        // The P26/P27/P28 "adopt a hull" path must be byte-identical to before.
        var msg = new BuyLoadoutAck { HullId = 917, FBoughtEverything = false, FLaunch = false, Parts = [] };
        var got = msg.Encode();
        Assert.Equal(11, got.Length);
        Assert.Equal(new byte[] { 0x0B, 0x00, 0x05, 0x00, 0x09, 0x00, 0x02, 0x00, 0x00, 0x95, 0x03 }, got);
    }

    [Fact]
    public void BuyLoadoutAck_RejectsNonWholePartTail()
    {
        var bytes = new BuyLoadoutAck { HullId = 908, Parts = [new ExpandedPartData(1, 2, 3, 4)] }.Encode();
        // cbLoadout = 2 + 6 = 8 at offset 6; force to 2+3=5 → not whole entries.
        bytes[6] = 5;
        Assert.Throws<ArgumentException>(() => BuyLoadoutAck.Decode(bytes));
    }

    [Fact]
    public void BuyLoadoutAck_RejectsWrongFmid_AndWrongLoadoutSize()
    {
        var bytes = new BuyLoadoutAck { HullId = 1 }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => BuyLoadoutAck.Decode(badFmid));

        // cbLoadout sits at message offset 6 (2 bytes). Override to 3 → not sizeof(ShipLoadout).
        var badCb = (byte[])bytes.Clone();
        badCb[6] = 3;
        Assert.Throws<ArgumentException>(() => BuyLoadoutAck.Decode(badCb));
    }
}
