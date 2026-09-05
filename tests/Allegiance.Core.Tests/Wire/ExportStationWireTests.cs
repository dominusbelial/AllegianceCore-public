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
    public void ExportStation_FromStation_DegenerateOrientation_UsesSafeFallbackInWireBytes()
    {
        // Real m2xspiderw.igc Garrison values (P79): Up=Forward=(0,0,0).
        var station = new DataStationIGC(
            Position: [1120f, 1560f, 0f],
            Up: [0f, 0f, 0f],
            Forward: [0f, 0f, 0f],
            Rotation: [1f, 0f, 0f, 1f],
            ClusterId: 4300, SideId: 0, StationId: 4328, StationTypeId: 9000,
            BpHull: 0x55, BpShield: 0xAA, Name: "Garrison");

        var msg = Allegiance.Core.Wire.ExportStation.FromStation(station);
        var got = msg.Encode();

        // Payload begins at message offset 12; within the payload Up is at payload
        // offset 12 (=> message 24) and Forward at payload offset 24 (=> message 36).
        Assert.Equal(1120f, BitConverter.ToSingle(got, 12)); // Position x — unchanged
        Assert.Equal(0f, BitConverter.ToSingle(got, 20));  // Position z — unchanged
        // Up must become the native identity fallback (0,1,0) at message 24-35.
        Assert.Equal(new[] { 0f, 1f, 0f }, new[]
        {
            BitConverter.ToSingle(got, 24),
            BitConverter.ToSingle(got, 28),
            BitConverter.ToSingle(got, 32),
        });
        // Forward must become (0,0,-1) at message 36-47.
        Assert.Equal(new[] { 0f, 0f, -1f }, new[]
        {
            BitConverter.ToSingle(got, 36),
            BitConverter.ToSingle(got, 40),
            BitConverter.ToSingle(got, 44),
        });
        // ClusterId / StationId preserved (payload offset 52 => message 64).
        Assert.Equal((short)4300, BitConverter.ToInt16(got, 64));
        Assert.Equal((short)4328, BitConverter.ToInt16(got, 68));
        Assert.Equal(100, got.Length);
    }

    [Fact]
    public void ExportStation_FromStation_ParallelNonZero_OrientationsAlsoFallBack()
    {
        // Same direction, different magnitude => native cross product genuinely zero
        // even though neither input is the zero vector. Proves the check ports the
        // real cross-product invariant, not just "both fields are literally (0,0,0)".
        var station = new DataStationIGC(
            Position: [7f, 8f, 9f],
            Up: [2f, 0f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            ClusterId: 4001, SideId: -1, StationId: 777, StationTypeId: 9120,
            BpHull: 0x11, BpShield: 0x22, Name: "Parallel");

        var msg = Allegiance.Core.Wire.ExportStation.FromStation(station);
        var got = msg.Encode();

        Assert.Equal(new[] { 0f, 1f, 0f }, new[]
        {
            BitConverter.ToSingle(got, 24),
            BitConverter.ToSingle(got, 28),
            BitConverter.ToSingle(got, 32),
        });
        Assert.Equal(new[] { 0f, 0f, -1f }, new[]
        {
            BitConverter.ToSingle(got, 36),
            BitConverter.ToSingle(got, 40),
            BitConverter.ToSingle(got, 44),
        });
        Assert.Equal((short)777, BitConverter.ToInt16(got, 68)); // StationId preserved
    }

    [Fact]
    public void ExportStation_FromStation_NormalOrientation_RoundTripsUnchanged()
    {
        // Real non-degenerate value (fixed from the corpus) — substitution must not fire.
        var station = new DataStationIGC(
            Position: [1.5f, -2.25f, 30f],
            Up: [0f, 1f, 0f],
            Forward: [0.5f, 0f, 0f],
            Rotation: [1f, 2f, 3f, 4f],
            ClusterId: 9341, SideId: -1, StationId: 817, StationTypeId: 9921,
            BpHull: 0xAA, BpShield: 0x77, Name: "Ns");

        var msg = Allegiance.Core.Wire.ExportStation.FromStation(station);

        // Forward/Up bit-exact through the factory (decoded from the wire bytes).
        var d = Allegiance.Core.Wire.ExportStation.Decode(msg.Encode());
        Assert.Equal(new[] { 0f, 1f, 0f }, d.Station.Up);
        Assert.Equal(new[] { 0.5f, 0f, 0f }, d.Station.Forward);
        // And everything else still round-trips unchanged.
        Assert.Equal(new[] { 1.5f, -2.25f, 30f }, d.Station.Position);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, d.Station.Rotation);
        Assert.Equal((short)9341, d.Station.ClusterId);
        Assert.Equal((short)-1, d.Station.SideId);
        Assert.Equal((short)817, d.Station.StationId);
        Assert.Equal((short)9921, d.Station.StationTypeId);
        Assert.Equal((byte)0xAA, d.Station.BpHull);
        Assert.Equal((byte)0x77, d.Station.BpShield);
        Assert.Equal("Ns", d.Station.Name);
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
