using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P93 wire-only mine codecs:
/// <see cref="DataMineExport"/> (the fixed OT_mine blob inside FM_S_EXPORT),
/// <see cref="ExportMine"/> (FM_S_EXPORT 26, OT_mine 3) and
/// <see cref="MineDestroyed"/> (FM_S_MINE_DESTROYED 84). All layouts are
/// pinned against programmatically-built expected buffers (the
/// declarative-builder <see cref="ByteBuf"/> lifted from
/// <c>MissileWireCodecsWireTests.cs</c>), not hand-typed hex — so a regression
/// in the encoder surfaces as a byte mismatch against an independently-derived
/// expectation, and a drift between <see cref="DataMineExport.Size"/> and
/// <see cref="ExportMine.FixedSize"/> as a failing length assert.
/// </summary>
public class MineWireCodecsWireTests
{
    // ------------------------------------------------------------------ //
    // DataMineExport — the fixed OT_mine blob                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DataMineExport_Size_IsThirtyTwo()
    {
        // Field-by-field (C++ default alignment, confirmed via a g++
        // offsetof/sizeof probe that reproduced every offset below):
        //   0-11  Vector   p0         (12 B, 4-aligned)
        //  12-15  uint     time0      (Time)
        //  16-17  short    mineID
        //  18     bool     exportF    (1 B, not a bitfield)
        //  19     PAD                — exportF leaves offset 19 odd; clusterID
        //                              is a 2-aligned short → 1 pad byte
        //  20-21  short    clusterID
        //  22-23  short    minetypeID
        //  24-25  short    launcherID
        //  26-27  short    sideID
        //  28     byte     fraction
        //  29     bool     createNow
        //  30-31  PAD,PAD            — struct alignment is 4 (Vector/Time), so
        //                              sizeof rounds 30 up to 32 → 2 trailing pad
        Assert.Equal(32, DataMineExport.Size);
        Assert.Equal(32, ExportMine.DataMineSize);
        Assert.Equal(44, ExportMine.FixedSize);   // 4 hdr + 4 varitem + 4 objecttype + 32 blob
    }

    [Fact]
    public void DataMineExport_RoundTrips_EveryField()
    {
        var e = new DataMineExport(
            P0: new Vector3(1.5f, -2.25f, 4498.5f),
            Time0: 7710u,
            MineId: 901,
            ExportF: true,
            ClusterId: 711,
            MinetypeId: 811,
            LauncherId: -1,          // NA (no launcher, per FedSrv.CPP:10064)
            SideId: 911,
            Fraction: CompactPercent(0.875f),  // BytePercentage byte
            CreateNow: false);
        var d = DataMineExport.Decode(e.Encode());
        Assert.Equal(e, d);                     // record-struct equality covers all 10 fields
        Assert.Equal(0.875f, d.FractionValue, 3); // 210/240 (CompactQuant div-240)
    }

    [Fact]
    public void DataMineExport_EmitsExactThirtyTwoBytes()
    {
        var e = new DataMineExport(
            P0: new Vector3(1f, 2f, 3f),
            Time0: 4400u,
            MineId: 550,
            ExportF: true,
            ClusterId: 650,
            MinetypeId: 750,
            LauncherId: 850,
            SideId: 950,
            Fraction: 0x60,          // 96/240 = 0.4
            CreateNow: true);
        var got = e.Encode();

        var exp = ByteBuf.Build(32, w =>
        {
            F(w, 1f); F(w, 2f); F(w, 3f);  // p0       (0)
            w.U32(4400);                   // time0    (12)
            w.I16(550);                    // mineID   (16)
            w.Byte(1);                     // exportF  (18)
            w.Pad(1);                      // pad      (19)
            w.I16(650);                    // clusterID   (20)
            w.I16(750);                    // minetypeID  (22)
            w.I16(850);                    // launcherID  (24)
            w.I16(950);                    // sideID      (26)
            w.Byte(0x60);                  // fraction    (28)
            w.Byte(1);                     // createNow   (29)
            w.Pad(2);                      // trailing pad → 32
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void DataMineExport_RejectsNonThirtyTwoByteBuffer()
    {
        var valid = new DataMineExport(Vector3.Zero, 0u, 1, true, 2, 3, 4, 5, 0x80, false).Encode();
        Assert.Throws<InvalidDataException>(() => DataMineExport.Decode(valid.AsSpan(0, 31)));
        Assert.Throws<InvalidDataException>(() => DataMineExport.Decode([]));
    }

    [Fact]
    public void DataMineExport_Values_AppearAtDeclaredOffsets()
    {
        // Cross-check the padded field offsets against the encoder output so a
        // layout change (dropping/adding a pad byte) is caught by byte position.
        var e = new DataMineExport(
            P0: new Vector3(10f, 20f, 30f), Time0: 987654321u, MineId: 444, ExportF: false,
            ClusterId: 888, MinetypeId: 889, LauncherId: 890, SideId: 892,
            Fraction: 0xAB, CreateNow: true);
        var b = e.Encode();
        Assert.Equal(10f, BitConverter.ToSingle(b, 0));       // p0.x
        Assert.Equal(30f, BitConverter.ToSingle(b, 8));       // p0.z
        Assert.Equal(987654321u, BitConverter.ToUInt32(b, 12)); // time0
        Assert.Equal(444, BitConverter.ToInt16(b, 16));       // mineID
        Assert.Equal(0, b[18]);                               // exportF = false → 0
        Assert.Equal(888, BitConverter.ToInt16(b, 20));       // clusterID after the offset-19 pad
        Assert.Equal(892, BitConverter.ToInt16(b, 26));       // sideID
        Assert.Equal(0xAB, b[28]);                            // fraction
        Assert.Equal(1, b[29]);                               // createNow
        Assert.Equal(32, b.Length);                           // 32 total
        Assert.All(b[30..32], byte_ => Assert.Equal(0, byte_)); // trailing pad region (30-31)
    }

    // ------------------------------------------------------------------ //
    // ExportMine (FM_S_EXPORT 26, OT_mine 3)                             //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ExportMine_Constants_AreDeclared()
    {
        Assert.Equal((ushort)26, ExportMine.Fmid);
        Assert.Equal(3, ExportMine.OtMine);      // OT_mine = AGC_Mine = 3 (igc.h:122)
        Assert.Equal(32, ExportMine.DataMineSize);
        Assert.Equal(44, ExportMine.FixedSize);  // 4 hdr + 4 varitem + 4 objecttype + 32 blob
    }

    [Fact]
    public void ExportMine_RoundTrips_ThroughWire()
    {
        var msg = new ExportMine
        {
            ObjectType = 3,
            Mine = new DataMineExport(
                P0: new Vector3(12f, 34f, 5678.25f), Time0: 8129u, MineId: 921,
                ExportF: true, ClusterId: 511, MinetypeId: 521, LauncherId: -1,
                SideId: 531, Fraction: 0xC0, CreateNow: false),
        };
        var d = ExportMine.Decode(msg.Encode());
        Assert.Equal(3, d.ObjectType);
        Assert.Equal(msg.Mine, d.Mine);
        Assert.Equal(new Vector3(12f, 34f, 5678.25f), d.Mine.P0);
        Assert.Equal(8129u, d.Mine.Time0);
        Assert.Equal((short)921, d.Mine.MineId);
        Assert.True(d.Mine.ExportF);
        Assert.Equal((short)511, d.Mine.ClusterId);
        Assert.Equal((short)521, d.Mine.MinetypeId);
        Assert.Equal((short)-1, d.Mine.LauncherId);
        Assert.Equal((short)531, d.Mine.SideId);
        Assert.Equal((byte)0xC0, d.Mine.Fraction);
        Assert.False(d.Mine.CreateNow);
    }

    [Fact]
    public void ExportMine_EmitsExactDeclaredBytes()
    {
        var msg = new ExportMine
        {
            ObjectType = 3,
            Mine = new DataMineExport(
                P0: new Vector3(1f, 2f, 3f), Time0: 4141u, MineId: 515,
                ExportF: true, ClusterId: 565, MinetypeId: 575, LauncherId: -2,
                SideId: 585, Fraction: 0x4B, CreateNow: true),
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(44, w =>
        {
            w.U16(44);            // cbmsg
            w.U16(26);            // fmid
            w.U16(12);            // ib exportData
            w.U16(32);            // cb exportData = sizeof(DataMineExport)
            w.I32(3);             // objecttype = OT_mine (int32, 4-byte)
            // ---- DataMineExport (32 bytes) at message offset 12 ----
            F(w, 1f); F(w, 2f); F(w, 3f);  // p0       (12)
            w.U32(4141);                  // time0    (24)
            w.I16(515);                   // mineID   (28)
            w.Byte(1);                    // exportF  (30)
            w.Pad(1);                     // pad      (31)
            w.I16(565);                   // clusterID   (32)
            w.I16(575);                   // minetypeID  (34)
            w.I16(-2);                    // launcherID  (36)
            w.I16(585);                   // sideID      (38)
            w.Byte(0x4B);                 // fraction    (40)
            w.Byte(1);                    // createNow   (41)
            w.Pad(2);                     // trailing pad → 44
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportMine_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new ExportMine
        {
            Mine = new DataMineExport(Vector3.Zero, 701u, 702, true, 703, 704, 705, 706, 170, false)
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => ExportMine.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to 4 → not OT_mine → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 4;
        Assert.Throws<ArgumentException>(() => ExportMine.Decode(badOt));

        // Truncate the blob below 32: cb != 32 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 31))];
        Assert.Throws<ArgumentException>(() => ExportMine.Decode(shortBuf));
    }

    // ------------------------------------------------------------------ //
    // MineDestroyed (FM_S_MINE_DESTROYED, 84)                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void MineDestroyed_FixedSize_IsEight()
    {
        // hdr(4) + clusterID(2) + mineID(2) = 8. No alignment gaps: the two
        // 2-byte IDs sit back-to-back at 4-7, no member wider than 2.
        Assert.Equal(8, MineDestroyed.FixedSize);
    }

    [Fact]
    public void MineDestroyed_RoundTrips_TwoFields()
    {
        var msg = new MineDestroyed { ClusterId = 991, MineId = 662 };
        var d = MineDestroyed.Decode(msg.Encode());
        Assert.Equal(991, d.ClusterId);
        Assert.Equal(662, d.MineId);
    }

    [Fact]
    public void MineDestroyed_EmitsExactBytes()
    {
        var msg = new MineDestroyed { ClusterId = 813, MineId = 914 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(8, w =>
        {
            w.U16(8);      // cbmsg
            w.U16(84);     // fmid
            w.I16(813);    // clusterID (SectorID)
            w.I16(914);    // mineID (MineID)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void MineDestroyed_RejectsWrongFmid()
    {
        var bytes = new MineDestroyed { ClusterId = 551, MineId = 559 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 234;
        Assert.Throws<ArgumentException>(() => MineDestroyed.Decode(badFmid));
    }

    [Fact]
    public void MineDestroyed_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => MineDestroyed.Decode(new byte[7]));
    }

    // ------------------------------------------------------------------ //
    // helpers                                                            //
    // ------------------------------------------------------------------ //

    private static void F(ByteBuf.Writer w, float v) =>
        w.U32(BitConverter.SingleToUInt32Bits(v));

    /// <summary>BytePercentage store (div-240 convention): <paramref name="f"/> fraction → byte.</summary>
    private static byte CompactPercent(float f) =>
        (byte)Math.Clamp((int)(f * 240.0 + 0.5), 0, 240);
}
