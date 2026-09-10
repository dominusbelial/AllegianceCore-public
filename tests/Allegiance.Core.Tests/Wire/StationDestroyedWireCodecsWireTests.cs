using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="StationDestroyed"/> — the P125
/// <c>FM_S_STATION_DESTROYED</c>(59) codec (the server's "this station was
/// destroyed" notice, <c>Messages.h:436-439</c>). Fixed short/short layout
/// (stationID, launcher), 8 bytes total, no var items. Bytes are pinned against
/// a programmatically-built expectation (the <see cref="ByteBuf"/> builder), not
/// hand-typed hex.
/// </summary>
public class StationDestroyedWireCodecsWireTests
{
    [Fact]
    public void Constants_AreDeclared()
    {
        Assert.Equal((ushort)59, StationDestroyed.Fmid);
        Assert.Equal(8, StationDestroyed.FixedSize);   // hdr(4) + stationID(2) + launcher(2)
    }

    [Fact]
    public void RoundTrips_TwoFields()
    {
        var msg = new StationDestroyed { StationId = 811, Launcher = 632 };
        var d = StationDestroyed.Decode(msg.Encode());
        Assert.Equal((short)811, d.StationId);
        Assert.Equal((short)632, d.Launcher);
    }

    [Fact]
    public void RoundTrips_LauncherNA()
    {
        // launcher = NA (-1) when the credited launcher was not a ship.
        var msg = new StationDestroyed { StationId = 812, Launcher = -1 };
        var d = StationDestroyed.Decode(msg.Encode());
        Assert.Equal((short)812, d.StationId);
        Assert.Equal((short)-1, d.Launcher);
    }

    [Fact]
    public void EmitsExactEightBytes()
    {
        var msg = new StationDestroyed { StationId = 815, Launcher = 642 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(8, w =>
        {
            w.U16(8);      // cbmsg
            w.U16(59);     // fmid
            w.I16(815);    // stationID (StationID)
            w.I16(642);    // launcher (ShipID)
        });
        Assert.Equal(exp, got);
    }
}
