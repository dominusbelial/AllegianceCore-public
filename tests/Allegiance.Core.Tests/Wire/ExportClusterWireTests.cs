using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="Allegiance.Core.Wire.ExportCluster"/>
/// (FM_S_EXPORT 26, OT_cluster 13) — the cluster half of the same
/// FM_S_EXPORT message <c>ExportShipWireTests</c> covers for OT_ship. Byte
/// exactness is pinned against a hand-composed buffer derived from the task's
/// field table (NOT from the encoder), exactly as <c>ExportShipWireTests</c>
/// does. Layout: header(4) + one var item(4) + objecttype int32(4) +
/// DataClusterIGC(92) = 104 bytes total. Decode rejects any objecttype != 13
/// or a blob cb != 92.
/// </summary>
public class ExportClusterWireTests
{
    [Fact]
    public void ExportCluster_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)26, Allegiance.Core.Wire.ExportCluster.Fmid);
        Assert.Equal(13, Allegiance.Core.Wire.ExportCluster.OtCluster);   // OT_cluster = AGC_Cluster = 13
        Assert.Equal(92, Allegiance.Core.Wire.ExportCluster.DataClusterSize);
        Assert.Equal(104, Allegiance.Core.Wire.ExportCluster.FixedSize);  // 4 hdr + 4 varitem + 4 objecttype + 92 blob
        Assert.Equal(92, DataClusterIGC.Size);
    }

    [Fact]
    public void ExportCluster_RoundTrips_ThroughWire()
    {
        var cluster = new DataClusterIGC(
            StarSeed: 0xDEADBEEF,
            LightDirection: [1.5f, -2.25f, 0.0625f],
            LightColor: 0x01020304,
            ScreenX: 100.5f, ScreenY: -200.25f,
            ClusterId: 34, Debris: 12, Stars: 900,
            Name: "Start Home", PosterName: "Poster!", PlanetName: "",
            PlanetSinLatitude: 0x11, PlanetLongitude: 0x22, PlanetRadius: 0x33,
            Active: true,              // decoder-independent; encoder forces false on export
            HomeSector: true);
        var msg = new Allegiance.Core.Wire.ExportCluster { Cluster = cluster };

        var d = Allegiance.Core.Wire.ExportCluster.Decode(msg.Encode());

        Assert.Equal(13, d.ObjectType);
        Assert.Equal(0xDEADBEEF, d.Cluster.StarSeed);
        Assert.Equal(new[] { 1.5f, -2.25f, 0.0625f }, d.Cluster.LightDirection);
        Assert.Equal(0x01020304u, d.Cluster.LightColor);
        Assert.Equal(100.5f, d.Cluster.ScreenX);
        Assert.Equal(-200.25f, d.Cluster.ScreenY);
        Assert.Equal((short)34, d.Cluster.ClusterId);
        Assert.Equal((short)12, d.Cluster.Debris);
        Assert.Equal((short)900, d.Cluster.Stars);
        Assert.Equal("Start Home", d.Cluster.Name);
        Assert.Equal("Poster!", d.Cluster.PosterName);
        Assert.Equal("", d.Cluster.PlanetName);
        Assert.Equal((byte)0x11, d.Cluster.PlanetSinLatitude);
        Assert.Equal((byte)0x22, d.Cluster.PlanetLongitude);
        Assert.Equal((byte)0x33, d.Cluster.PlanetRadius);
        Assert.False(d.Cluster.Active);     // activeF forced false on export
        Assert.True(d.Cluster.HomeSector);
    }

    [Fact]
    public void ExportCluster_EmitsExactDeclaredBytes()
    {
        var cluster = new DataClusterIGC(
            StarSeed: 0xDEADBEEF,
            LightDirection: [1.5f, -2.25f, 0.03125f],
            LightColor: 0x01020304,
            ScreenX: 100.5f, ScreenY: -200.25f,
            ClusterId: 34, Debris: 12, Stars: 900,
            Name: "Hi", PosterName: "Poster!", PlanetName: "",
            PlanetSinLatitude: 0x11, PlanetLongitude: 0x22, PlanetRadius: 0x33,
            Active: true /* encoder must still write 0 */, HomeSector: true);
        var msg = new Allegiance.Core.Wire.ExportCluster { Cluster = cluster };
        var got = msg.Encode();

        var exp = new byte[104];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void f(float v) { u32(BitConverter.SingleToUInt32Bits(v)); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(104);                        // cbmsg
        u16(26);                         // fmid = EXPORT
        u16(12);                         // ib exportData
        u16(92);                         // cb exportData = sizeof(DataClusterIGC)
        u32(13);                         // objecttype = OT_cluster (int32, 4-byte)
        // ---- DataClusterIGC (92 bytes) at message offset 12 ----
        u32(0xDEADBEEF);                 // starSeed
        f(1.5f); f(-2.25f); f(0.03125f); // lightDirection[3]
        u32(0x01020304);                 // lightColor
        f(100.5f); f(-200.25f);          // screenX/Y
        u16(34); u16(12); u16(900);      // clusterID, debris, stars
        foreach (var b in Encoding.ASCII.GetBytes("Hi")) exp[p++] = b;   // name "Hi"
        pad(23);                         // name zero-pad to 25
        foreach (var b in Encoding.ASCII.GetBytes("Poster!")) exp[p++] = b; // posterName
        pad(6);                          // posterName zero-pad to 13
        pad(13);                         // planetName "" (all zeros)
        exp[p++] = 0x11;                 // planetSinLatitude
        exp[p++] = 0x22;                 // planetLongitude
        exp[p++] = 0x33;                 // planetRadius
        exp[p++] = 0;                    // activeF — ALWAYS false on export
        exp[p++] = 1;                    // bHomeSector
        exp[p++] = 0;                    // pad
        exp[p++] = 0;                    // pad → 92 total
        Assert.Equal(104, p);            // header(4)+varitem(4)+objecttype(4)+DataClusterIGC(92)

        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportCluster_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new Allegiance.Core.Wire.ExportCluster
        {
            Cluster = new DataClusterIGC(1, [1f, 2f, 3f], 4, 5f, 6f, 7, 8, 9,
                "A", "B", "C", 1, 2, 3, false, false)
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportCluster.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to 2 → not OT_cluster → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 2;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportCluster.Decode(badOt));

        // Truncate the blob below 92: cb != 92 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 91))];
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportCluster.Decode(shortBuf));
    }
}
