using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="Allegiance.Core.Wire.ExportStation"/>
/// (FM_S_EXPORT 26, OT_station 1) — the station half of the same FM_S_EXPORT
/// message the other EXPORT codecs cover. Byte exactness is pinned against a
/// hand-composed buffer derived from the task's field table (NOT from the
/// encoder), exactly as <c>ExportClusterWireTests</c>/<c>ExportWarpWireTests</c>
/// do. Layout: header(4) + one var item(4) + objecttype int32(4) +
/// DataStationIGC(88) = 100 bytes total. Decode rejects any objecttype != 1 or
/// a blob cb != 88.
/// </summary>
public class ExportStationWireTests
{
    [Fact]
    public void ExportStation_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)26, Allegiance.Core.Wire.ExportStation.Fmid);
        Assert.Equal(1, Allegiance.Core.Wire.ExportStation.OtStation);   // OT_station = AGC_Station = 1
        Assert.Equal(88, Allegiance.Core.Wire.ExportStation.DataStationSize);
        Assert.Equal(100, Allegiance.Core.Wire.ExportStation.FixedSize); // 4 hdr + 4 varitem + 4 objecttype + 88 blob
        Assert.Equal(88, DataStationIGC.Size);
    }

    [Fact]
    public void ExportStation_RoundTrips_ThroughWire()
    {
        var station = new DataStationIGC(
            Position: [100f, 200f, 300f],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [1f, 0f, 0f, 1f],
            ClusterId: 1053, SideId: 0, StationId: 555, StationTypeId: 8912,
            BpHull: 0xAB, BpShield: 0xCD,
            Name: "Station Omega");
        var msg = new Allegiance.Core.Wire.ExportStation { Station = station };

        var d = Allegiance.Core.Wire.ExportStation.Decode(msg.Encode());

        Assert.Equal(1, d.ObjectType);
        Assert.Equal(new[] { 100f, 200f, 300f }, d.Station.Position);
        Assert.Equal(new[] { 0f, 1f, 0f }, d.Station.Up);
        Assert.Equal(new[] { 1f, 0f, 0f }, d.Station.Forward);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, d.Station.Rotation);
        Assert.Equal((short)1053, d.Station.ClusterId);
        Assert.Equal((short)0, d.Station.SideId);
        Assert.Equal((short)555, d.Station.StationId);
        Assert.Equal((short)8912, d.Station.StationTypeId);
        Assert.Equal((byte)0xAB, d.Station.BpHull);
        Assert.Equal((byte)0xCD, d.Station.BpShield);
        Assert.Equal("Station Omega", d.Station.Name);
    }

    [Fact]
    public void ExportStation_EmitsExactDeclaredBytes()
    {
        var station = new DataStationIGC(
            Position: [1.5f, -2.25f, 30f],
            Up: [0f, 1f, 0f],
            Forward: [0.5f, 0f, 0f],
            Rotation: [1f, 2f, 3f, 4f],
            ClusterId: 9341, SideId: -1, StationId: 817, StationTypeId: 9921,
            BpHull: 0xAA, BpShield: 0x77,
            Name: "Ns");
        var msg = new Allegiance.Core.Wire.ExportStation { Station = station };
        var got = msg.Encode();

        var exp = new byte[100];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void s16(short v) => u16(unchecked((ushort)v));
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void f(float v) { u32(BitConverter.SingleToUInt32Bits(v)); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }
        void str(string s, int width) { foreach (var b in Encoding.ASCII.GetBytes(s)) exp[p++] = b; pad(width - s.Length); }

        u16(100);                        // cbmsg
        u16(26);                         // fmid = EXPORT
        u16(12);                         // ib exportData
        u16(88);                         // cb exportData = sizeof(DataStationIGC)
        u32(1);                          // objecttype = OT_station (int32, 4-byte)
        // ---- DataStationIGC (88 bytes) at message offset 12 ----
        f(1.5f); f(-2.25f); f(30f);      // position (0)
        f(0f); f(1f); f(0f);             // up (12)
        f(0.5f); f(0f); f(0f);           // forward (24)
        f(1f); f(2f); f(3f); f(4f);      // rotation (36)
        s16(9341);                       // clusterID (52)
        s16(-1);                         // sideID (54)
        s16(817);                        // stationID (56)
        s16(9921);                       // stationTypeID (58)
        exp[p++] = 0xAA;                 // bpHull (60)
        exp[p++] = 0x77;                 // bpShield (61)
        str("Ns", 25);                   // name char[25] at 62
        pad(1);                          // 87 pad → 88 total
        Assert.Equal(100, p);            // header(4)+varitem(4)+objecttype(4)+DataStationIGC(88)

        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportStation_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new Allegiance.Core.Wire.ExportStation
        {
            Station = new DataStationIGC([1f, 2f, 3f], [0f, 1f, 0f], [1f, 0f, 0f],
                [1f, 0f, 0f, 0f], 6, 7, 8, 9101, 9, 10, "Nm")
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportStation.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to 13 → not OT_station → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 13;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportStation.Decode(badOt));

        // Truncate the blob below 88: cb != 88 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 87))];
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportStation.Decode(shortBuf));
    }
}
