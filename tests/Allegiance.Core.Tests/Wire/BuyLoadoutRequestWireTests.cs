using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="BuyLoadoutRequest"/>
/// (FM_C_BUY_LOADOUT, 4), ported from <c>Messages.h:65-68</c>. Same
/// <c>ShipLoadout + ExpandedPartData[]</c> blob shape as the ACK, but the
/// request packs only the single <c>bool fLaunch</c> (bit0 of the flags byte;
/// the ACK's second <c>bool fBoughtEverything</c> does not exist here).
/// Wire layout: header(4) + var descriptor(4) + flags(1) + blob(2 + 6·nParts).
/// nParts = 0 → 11 bytes; nParts = 1 → 17 bytes. Verified against a hand-composed
/// buffer, not the encoder.
/// </summary>
public class BuyLoadoutRequestWireTests
{
    [Fact]
    public void BuyLoadoutRequest_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)4, BuyLoadoutRequest.Fmid);
        Assert.Equal(2, BuyLoadoutRequest.ShipLoadoutSize);
        Assert.Equal(6, ExpandedPartData.Size);
        Assert.Equal(11, BuyLoadoutRequest.FixedSize);   // 4 hdr + 4 varitem + 1 flags + 2 loadout (nParts=0)
    }

    [Fact]
    public void BuyLoadoutRequest_HullOnly_RoundTrips()
    {
        var msg = new BuyLoadoutRequest { HullId = 903, FLaunch = false, Parts = [] };
        var d = BuyLoadoutRequest.Decode(msg.Encode());
        Assert.Equal(903, d.HullId);
        Assert.False(d.FLaunch);
        Assert.Empty(d.Parts);

        msg = new BuyLoadoutRequest { HullId = -1, FLaunch = true, Parts = [] };
        d = BuyLoadoutRequest.Decode(msg.Encode());
        Assert.Equal(-1, d.HullId);
        Assert.True(d.FLaunch);
        Assert.Empty(d.Parts);
    }

    [Fact]
    public void BuyLoadoutRequest_EmitsExactDeclaredBytes_HullOnly()
    {
        var msg = new BuyLoadoutRequest { HullId = 905, FLaunch = true, Parts = [] };
        var got = msg.Encode();

        //                       cbmsg  fmid  ib     cb     flags hullID(le)
        var exp = new byte[] { 0x0B, 0x00, 0x04, 0x00, 0x09, 0x00, 0x02, 0x00, 0x01, 0x89, 0x03 };

        Assert.Equal(11, exp.Length);
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BuyLoadoutRequest_WithOnePart_EmitsExactDeclaredBytes()
    {
        var msg = new BuyLoadoutRequest
        {
            HullId = 905,
            FLaunch = true,
            Parts = [new ExpandedPartData(PartId: 1000, Amount: 5, MountId: 2, FractionMounted: ExpandedPartData.FullyMounted)],
        };
        var got = msg.Encode();

        //                       cbmsg      fmid  ib     cb     flags hullID(le)  partID   amount  mount fraction
        var exp = new byte[] { 0x11, 0x00, 0x04, 0x00, 0x09, 0x00, 0x08, 0x00, 0x01, 0x89, 0x03, 0xE8, 0x03, 0x05, 0x00, 0x02, 0xF0 };

        Assert.Equal(17, exp.Length);
        Assert.Equal(exp, got);

        var d = BuyLoadoutRequest.Decode(got);
        Assert.Equal(905, d.HullId);
        Assert.True(d.FLaunch);
        var p = Assert.Single(d.Parts);
        Assert.Equal(1000, p.PartId);
        Assert.Equal(5, p.Amount);
        Assert.Equal(2, p.MountId);
        Assert.Equal(240, p.FractionMounted);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x01)]
    public void BuyLoadoutRequest_FlagsBit_IsLowBit(bool flaunch, byte expect)
    {
        var msg = new BuyLoadoutRequest { HullId = 1, FLaunch = flaunch };
        var got = msg.Encode();
        Assert.Equal(expect, got[8]);               // flags byte at message offset 8
        var d = BuyLoadoutRequest.Decode(got);
        Assert.Equal(flaunch, d.FLaunch);
    }

    [Fact]
    public void BuyLoadoutRequest_RejectsWrongFmid_AndMalformedLoadoutSize()
    {
        var bytes = new BuyLoadoutRequest { HullId = 1 }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => BuyLoadoutRequest.Decode(badFmid));

        // cbLoadout at offset 6: 3 → not (2 + whole 6-byte entries)
        var badCb = (byte[])bytes.Clone();
        badCb[6] = 3;
        Assert.Throws<ArgumentException>(() => BuyLoadoutRequest.Decode(badCb));
    }
}
