using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="StationCapture"/> — the P126
/// <c>FM_S_STATION_CAPTURE</c>(58) codec (the server's "this station changed
/// sides" notice, <c>Messages.h:429-434</c>). Fixed short/short/short/short layout
/// (stationID, sidOld, sidNew, shipIDCredit), 12 bytes total, no var items. Bytes
/// are pinned against a programmatically-built expectation (the
/// <see cref="ByteBuf"/> builder), not hand-typed hex.
/// </summary>
public class StationCaptureWireCodecsWireTests
{
    [Fact]
    public void Constants_AreDeclared()
    {
        Assert.Equal((ushort)58, StationCapture.Fmid);
        Assert.Equal(12, StationCapture.FixedSize);   // hdr(4) + stationID(2) + sidOld(2) + sidNew(2) + shipIDCredit(2)
    }

    [Fact]
    public void RoundTrips_AllFourFields()
    {
        var msg = new StationCapture { StationId = 801, SidOld = 0, SidNew = 3, ShipIdCredit = 911 };
        var d = StationCapture.Decode(msg.Encode());
        Assert.Equal((short)801, d.StationId);
        Assert.Equal((short)0, d.SidOld);
        Assert.Equal((short)3, d.SidNew);
        Assert.Equal((short)911, d.ShipIdCredit);
    }

    [Fact]
    public void RoundTrips_SameSideCapture_NoSideChange()
    {
        // A degenerate case where sidOld == sidNew must still round-trip.
        var msg = new StationCapture { StationId = 802, SidOld = 1, SidNew = 1, ShipIdCredit = 912 };
        var d = StationCapture.Decode(msg.Encode());
        Assert.Equal((short)802, d.StationId);
        Assert.Equal((short)1, d.SidOld);
        Assert.Equal((short)1, d.SidNew);
        Assert.Equal((short)912, d.ShipIdCredit);
    }

    [Fact]
    public void EmitsExactTwelveBytes()
    {
        var msg = new StationCapture { StationId = 803, SidOld = 2, SidNew = 5, ShipIdCredit = 913 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(12, w =>
        {
            w.U16(12);     // cbmsg
            w.U16(58);     // fmid
            w.I16(803);    // stationID (StationID)
            w.I16(2);      // sidOld (SideID)
            w.I16(5);      // sidNew (SideID)
            w.I16(913);    // shipIDCredit (ShipID)
        });
        Assert.Equal(exp, got);
    }
}
