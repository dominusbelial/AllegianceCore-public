using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="Allegiance.Core.Wire.ExportWarp"/>
/// (FM_S_EXPORT 26, OT_warp 7) — the warp half of the same FM_S_EXPORT message
/// <c>ExportClusterWireTests</c>/<c>ExportShipWireTests</c> cover for other
/// object types. Byte exactness is pinned against a hand-composed buffer
/// derived from the task's field table (NOT from the encoder), exactly as
/// <c>ExportClusterWireTests</c> does. Layout: header(4) + one var item(4) +
/// objecttype int32(4) + DataWarpIGC(108) = 120 bytes total. Decode rejects any
/// objecttype != 7 or a blob cb != 108.
/// </summary>
public class ExportWarpWireTests
{
    [Fact]
    public void ExportWarp_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)26, Allegiance.Core.Wire.ExportWarp.Fmid);
        Assert.Equal(7, Allegiance.Core.Wire.ExportWarp.OtWarp);   // OT_warp = AGC_Warp = 7
        Assert.Equal(108, Allegiance.Core.Wire.ExportWarp.DataWarpSize);
        Assert.Equal(120, Allegiance.Core.Wire.ExportWarp.FixedSize); // 4 hdr + 4 varitem + 4 objecttype + 108 blob
        Assert.Equal(108, DataWarpIGC.Size);
    }

    [Fact]
    public void ExportWarp_RoundTrips_ThroughWire()
    {
        var warp = new DataWarpIGC(
            WarpId: 999, DestinationId: 998, Radius: 550,
            TextureName: "texA", IconName: "icoB", Name: "WarpAlpha",
            Position: [1.5f, -2.25f, 0.0625f],
            Forward: [0f, 1f, 0f],
            Rotation: [1f, 0f, 0f, 1f],
            Signature: 812.75f,
            ClusterId: 1053);
        var msg = new Allegiance.Core.Wire.ExportWarp { Warp = warp };

        var d = Allegiance.Core.Wire.ExportWarp.Decode(msg.Encode());

        Assert.Equal(7, d.ObjectType);
        Assert.Equal((short)999, d.Warp.WarpId);
        Assert.Equal((short)998, d.Warp.DestinationId);
        Assert.Equal((short)550, d.Warp.Radius);
        Assert.Equal("texA", d.Warp.TextureName);
        Assert.Equal("icoB", d.Warp.IconName);
        Assert.Equal("WarpAlpha", d.Warp.Name);
        Assert.Equal(new[] { 1.5f, -2.25f, 0.0625f }, d.Warp.Position);
        Assert.Equal(new[] { 0f, 1f, 0f }, d.Warp.Forward);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, d.Warp.Rotation);
        Assert.Equal(812.75f, d.Warp.Signature);
        Assert.Equal((short)1053, d.Warp.ClusterId);
    }

    [Fact]
    public void ExportWarp_EmitsExactDeclaredBytes()
    {
        var warp = new DataWarpIGC(
            WarpId: 9403, DestinationId: -9271, Radius: 512,
            TextureName: "TexW", IconName: "IcoW", Name: "Wu",
            Position: [10f, -20f, 30f],
            Forward: [0.5f, 0.5f, 0f],
            Rotation: [1f, 2f, 3f, 4f],
            Signature: 908.5f,
            ClusterId: 9362);
        var msg = new Allegiance.Core.Wire.ExportWarp { Warp = warp };
        var got = msg.Encode();

        var exp = new byte[120];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void s16(short v) => u16(unchecked((ushort)v));
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void f(float v) { u32(BitConverter.SingleToUInt32Bits(v)); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }
        void str(string s, int width) { foreach (var b in Encoding.ASCII.GetBytes(s)) exp[p++] = b; pad(width - s.Length); }

        u16(120);                        // cbmsg
        u16(26);                         // fmid = EXPORT
        u16(12);                         // ib exportData
        u16(108);                        // cb exportData = sizeof(DataWarpIGC)
        u32(7);                          // objecttype = OT_warp (int32, 4-byte)
        // ---- DataWarpIGC (108 bytes) at message offset 12 ----
        s16(9403);                       // warpID
        s16(-9271);                      // destinationID
        s16(512);                        // radius
        str("TexW", 13);                 // textureName char[13]
        str("IcoW", 13);                 // iconName char[13]
        str("Wu", 25);                   // name char[25] at 32
        pad(3);                          // 57-59 pad (align Vector to 4)
        f(10f); f(-20f); f(30f);         // position (60)
        f(0.5f); f(0.5f); f(0f);         // forward (72)
        f(1f); f(2f); f(3f); f(4f);      // rotation (84)
        f(908.5f);                       // signature (100)
        s16(9362);                       // clusterID (104)
        pad(2);                          // 106-107 → 108 total
        Assert.Equal(120, p);            // header(4)+varitem(4)+objecttype(4)+DataWarpIGC(108)

        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportWarp_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new Allegiance.Core.Wire.ExportWarp
        {
            Warp = new DataWarpIGC(1, 2, 3, "T", "I", "N",
                [1f, 2f, 3f], [0f, 1f, 0f], [1f, 0f, 0f, 0f], 5f, 6)
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportWarp.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to 13 → not OT_warp → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 13;
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportWarp.Decode(badOt));

        // Truncate the blob below 108: cb != 108 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 107))];
        Assert.Throws<ArgumentException>(() => Allegiance.Core.Wire.ExportWarp.Decode(shortBuf));
    }
}
