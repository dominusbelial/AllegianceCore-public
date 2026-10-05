using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
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

    /// <summary>
    /// P540c — <see cref="Allegiance.Core.Wire.ExportWarp.FromWarp"/> must export
    /// the NATIVE <c>DestinationId</c> (the id of the warp's PARTNER, per
    /// <c>warpIGC.h:129-141</c>) unchanged. The old P540b override bent it to the
    /// resolved destination SECTOR id (<c>rw.Destination.Id</c>) — a different id
    /// space — which broke native wire parity (a native client / WOPR bot that
    /// receives EXPORT_WARP looks up a warp whose id equals a sector id). This is
    /// the failing-first test that pins the wire to the partner warp id: it fails
    /// on the bent code (which would emit the sector id here) and passes on the
    /// native-correct <c>FromWarp</c>.
    /// </summary>
    [Fact]
    public void ExportWarp_FromWarp_PreservesNativeDestinationId_PartnerWarpId()
    {
        // warp 700 (sector 701) connects to its partner 702 (sector 704). The
        // exported DestinationId must remain 702 — the PARTNER WARP id — and must
        // NOT be overwritten with the destination sector id 704. A real resolved
        // destination (sector 704) is supplied so this genuinely fails on the old
        // P540b bend (which emitted rw.Destination.Id = 704).
        var warp = new DataWarpIGC(
            WarpId: 700, DestinationId: 702, Radius: 420,
            TextureName: "tex", IconName: "ico", Name: "WV",
            Position: [1f, 2f, 3f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], Signature: 1f, ClusterId: 701);
        var destSector = new Sector(new DataClusterIGC(
            StarSeed: 1, LightDirection: [1f, 1f, 1f], LightColor: 0, ScreenX: 0f, ScreenY: 0f,
            ClusterId: 704, Debris: 0, Stars: 0, Name: "Dest", PosterName: "", PlanetName: "",
            PlanetSinLatitude: 0, PlanetLongitude: 0, PlanetRadius: 0, Active: true, HomeSector: false));
        var rw = new ResolvedWarp(warp, destSector);
        var decoded = Allegiance.Core.Wire.ExportWarp.Decode(
            Allegiance.Core.Wire.ExportWarp.FromWarp(rw).Encode());

        Assert.Equal((short)702, decoded.Warp.DestinationId);   // the PARTNER warp id, preserved
        Assert.Equal((short)700, decoded.Warp.WarpId);
        Assert.Equal((short)701, decoded.Warp.ClusterId);
    }

    /// <summary>
    /// P540c — WIRE CHECK AGAINST NATIVE on the server's real map
    /// (<c>XXV_simple.igc</c>, the map the server boots in its live tests): decode
    /// the server's EXPORT_WARP bytes (built via the server's own
    /// <c>ExportWarp.FromWarp</c> from the assembled mission's resolved warps) and
    /// show <c>destinationID</c> == the PARTNER warp's <c>warpID</c> for every warp
    /// (asserting at least 3, quoted below). This is the native wire: the
    /// destination sector is the partner warp's <c>ClusterId</c>, never the raw
    /// warp id misread as a sector.
    /// </summary>
    [Fact]
    public void ExportWarp_RealMap_WireCarriesPartnerWarpId_NativeParity()
    {
        var corePath = Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", System.StringComparison.OrdinalIgnoreCase));
        var mapPath = Corpus.Files.First(p => Path.GetFileName(p).Equals("XXV_simple.igc", System.StringComparison.OrdinalIgnoreCase));
        var core = StaticCore.Load(IgcReader.ParseFile(corePath));
        var map = MissionMap.Load(IgcReader.ParseFile(mapPath));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "Team 1", civ)], new NullIgcSite());

        var resolvable = mission.Warps
            .Where(w => w.Destination is not null && w.Partner() is not null)
            .ToList();
        Assert.True(resolvable.Count >= 3, $"map has {resolvable.Count} resolvable warps; need >= 3");

        int checkedCount = 0;
        foreach (var rw in resolvable)
        {
            var decoded = Allegiance.Core.Wire.ExportWarp.Decode(
                Allegiance.Core.Wire.ExportWarp.FromWarp(rw).Encode());

            // destinationID on the wire == the partner warp's warpID (native).
            Assert.Equal(rw.Warp.DestinationId, decoded.Warp.DestinationId);
            Assert.Equal(rw.Partner()!.Warp.WarpId, decoded.Warp.DestinationId);
            // ...and that partner's ClusterId is the destination sector.
            Assert.Equal(rw.Partner()!.Warp.ClusterId, rw.Destination!.Id);
            checkedCount++;

            if (checkedCount <= 5)
                System.Console.WriteLine(
                    $"EXPORT_WARP parity: warp {rw.Warp.WarpId} (sector {rw.Warp.ClusterId}) " +
                    $"destinationID={decoded.Warp.DestinationId} == partner warp {rw.Partner()!.Warp.WarpId} " +
                    $"whose ClusterId={rw.Partner()!.Warp.ClusterId} == destination sector {rw.Destination!.Id}");
        }
        Assert.True(checkedCount >= 3, $"verified {checkedCount} warps; need >= 3");
    }
}
