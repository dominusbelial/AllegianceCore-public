using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="ShipStatusMessage"/>
/// (FM_S_SHIP_STATUS 90), ported from <c>Messages.h:630-633</c> (<c>ShipID
/// shipID; ShipStatus status;</c>). Byte layout compiler-verified against MSVC
/// (see <see cref="ShipStatusMessage"/> doc): ShipStatus = 16 bytes (4 shorts +
/// one bitfield byte + DWORD StateTime), message = 24 bytes with status@8.
/// </summary>
public class ShipStatusWireTests
{
    [Fact]
    public void ShipStatus_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)90, ShipStatusMessage.Fmid);
        Assert.Equal(16, ShipStatusMessage.ShipStatusSize);
        Assert.Equal(24, ShipStatusMessage.FixedSize);
    }

    [Fact]
    public void ShipStatus_RoundTrips_ThroughWire()
    {
        var msg = new ShipStatusMessage
        {
            ShipId = 8096,
            HullId = 12,
            SectorId = 3,
            StationId = -1,
            ParentId = -1,
            State = ShipStatusMessage.StateFlying,
            Unknown = false,
            Detected = true,
            StateTime = 0x12345678,
        };
        var d = ShipStatusMessage.Decode(msg.Encode());
        Assert.Equal(8096, d.ShipId);
        Assert.Equal(12, d.HullId);
        Assert.Equal(3, d.SectorId);
        Assert.Equal(-1, d.StationId);
        Assert.Equal(-1, d.ParentId);
        Assert.Equal(ShipStatusMessage.StateFlying, d.State);
        Assert.False(d.Unknown);
        Assert.True(d.Detected);
        Assert.Equal(0x12345678u, d.StateTime);
    }

    [Fact]
    public void ShipStatus_AllShipStateValues_RoundTrip()
    {
        foreach (var s in new[]
        {
            ShipStatusMessage.StateDead, ShipStatusMessage.StateDocked,
            ShipStatusMessage.StateFlying, ShipStatusMessage.StateObserver,
            ShipStatusMessage.StateTurret,
        })
        {
            var d = ShipStatusMessage.Decode(new ShipStatusMessage { State = s, Detected = true }.Encode());
            Assert.Equal(s, d.State);
        }
    }

    [Fact]
    public void ShipStatus_UnknownAndDetectedBits_RoundTrip()
    {
        var d = ShipStatusMessage.Decode(new ShipStatusMessage { Unknown = true, Detected = true }.Encode());
        Assert.True(d.Unknown);
        Assert.True(d.Detected);

        d = ShipStatusMessage.Decode(new ShipStatusMessage { Unknown = false, Detected = false }.Encode());
        Assert.False(d.Unknown);
        Assert.False(d.Detected);
    }

    [Fact]
    public void ShipStatus_EmitsExactDeclaredBytes()
    {
        var msg = new ShipStatusMessage
        {
            ShipId = 0x1112,          // le: 12 11
            HullId = 0x2122,          // le: 22 21
            SectorId = 0x3132,        // le: 32 31
            StationId = 0x4142,       // le: 42 41
            ParentId = 0x5152,        // le: 52 51
            State = 3,                // c_ssFlying
            Unknown = true,           // bit6
            Detected = true,          // bit7  → bitfield byte 0x40|0x80|0x03 = 0xC3
            StateTime = 0x61626364,   // le: 64 63 62 61
        };
        var got = msg.Encode();

        // cbmsg=24(fmid 0x005A=90)  shipID  pad2   hull  sector  station  parent  bitfield  pad3  dTime
        var exp = new byte[]
        {
            0x18, 0x00,               // cbmsg = 24
            0x5A, 0x00,               // fmid = 90
            0x12, 0x11,               // shipID = 0x1112
            0x00, 0x00,               // pad
            0x22, 0x21,               // HullID = 0x2122
            0x32, 0x31,               // SectorID = 0x3132
            0x42, 0x41,               // StationID = 0x4142
            0x52, 0x51,               // ParentID = 0x5152
            0xC3,                     // bitfield: state=3 (bits0-5), unknown(bit6), detected(bit7)
            0x00, 0x00, 0x00,         // pad
            0x64, 0x63, 0x62, 0x61,   // StateTime = 0x61626364
        };

        Assert.Equal(24, exp.Length);
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ShipStatus_RejectsWrongFmid_AndTruncatedBuffer()
    {
        var bytes = new ShipStatusMessage { Detected = true }.Encode();

        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => ShipStatusMessage.Decode(badFmid));

        Assert.Throws<ArgumentException>(() => ShipStatusMessage.Decode(bytes.AsSpan(0, 23)));
    }
}
