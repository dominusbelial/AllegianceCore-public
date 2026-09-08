using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P98 wire-only probe codecs:
/// <see cref="DataProbeExport"/> (the fixed OT_probe blob inside FM_S_EXPORT),
/// <see cref="ExportProbe"/> (FM_S_EXPORT 26, OT_probe 4) and
/// <see cref="ProbeDestroyed"/> (FM_S_PROBE_DESTROYED 85). All layouts are
/// pinned against programmatically-built expected buffers (the declarative-
/// builder <see cref="ByteBuf"/> lifted from
/// <c>MissileWireCodecsWireTests.cs</c>), not hand-typed hex — so a regression
/// in the encoder surfaces as a byte mismatch against an independently-derived
/// expectation, and a drift between <see cref="DataProbeExport.Size"/> and
/// <see cref="ExportProbe.FixedSize"/> as a failing length assert.
/// </summary>
public class ProbeWireCodecsWireTests
{
    // ------------------------------------------------------------------ //
    // DataProbeExport — the fixed OT_probe blob                          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DataProbeExport_Size_IsThirtySix()
    {
        // Field-by-field (C++ default alignment, confirmed via a g++
        // offsetof/sizeof probe that reproduced every offset below):
        //   0-11  Vector   p0          (12 B, 4-aligned)
        //  12-15  uint     time0       (Time → uint convention)
        //  16-17  short    probeID
        //  18     bool     exportF     (1 B, not a bitfield)
        //  19     PAD                 — exportF leaves offset 19 odd;
        //                               probetypeID is a 2-aligned short → 1 pad
        //  20-21  short    probetypeID
        //  22-23  short    sideID
        //  24-25  short    clusterID
        //  26-27  short    shipID
        //  28-29  short    otTarget    (native ObjectType = short, igc.h:115)
        //  30-31  short    oidTarget
        //  32     bool     createNow
        //  33-35  PAD×3               — struct alignment is 4 (Vector/Time), so
        //                               sizeof rounds 33 up to 36 → 3 trailing pad
        Assert.Equal(36, DataProbeExport.Size);
        Assert.Equal(36, ExportProbe.DataProbeSize);
        Assert.Equal(48, ExportProbe.FixedSize);   // 4 hdr + 4 varitem + 4 objecttype + 36 blob
    }

    [Fact]
    public void DataProbeExport_RoundTrips_EveryField_WithTarget()
    {
        var e = new DataProbeExport(
            P0: new Vector3(1.5f, -2.25f, 8391.5f),
            Time0: 7421u,
            ProbeId: 910,
            ExportF: true,
            ProbetypeId: 810,
            SideId: 710,
            ClusterId: 410,
            ShipId: -1,              // NA launcher (never fired from a ship here)
            OtTarget: 0,             // OT_ship — real target type
            OidTarget: 777,          // real target id
            CreateNow: true);
        var d = DataProbeExport.Decode(e.Encode());
        Assert.Equal(e, d);          // record-struct equality covers all 11 fields
        Assert.Equal((short)0, d.OtTarget);
        Assert.Equal((short)777, d.OidTarget);
    }

    [Fact]
    public void DataProbeExport_RoundTrips_NoTarget_Case()
    {
        // otTarget/oidTarget both NA (-1): the branch a probe with no
        // c_eabmShootOnlyTarget capability (or no tracked target) exercises —
        // probeigc.cpp:558-561 sets both to NA when m_target is null.
        var e = new DataProbeExport(
            P0: new Vector3(-400.5f, 990.25f, 6300f),
            Time0: 8812u,
            ProbeId: 4321,
            ExportF: true,
            ProbetypeId: 900,
            SideId: 800,
            ClusterId: 700,
            ShipId: 601,
            OtTarget: -1,            // NA
            OidTarget: -1,           // NA
            CreateNow: false);
        var d = DataProbeExport.Decode(e.Encode());
        Assert.Equal(e, d);
        Assert.Equal((short)-1, d.OtTarget);
        Assert.Equal((short)-1, d.OidTarget);
    }

    [Fact]
    public void DataProbeExport_EmitsExactThirtySixBytes()
    {
        var e = new DataProbeExport(
            P0: new Vector3(1f, 2f, 3f),
            Time0: 8800u,
            ProbeId: 660,
            ExportF: true,
            ProbetypeId: 770,
            SideId: 870,
            ClusterId: 970,
            ShipId: 580,
            OtTarget: 680,           // OT_missile-ish arbitrary target type
            OidTarget: 690,
            CreateNow: true);
        var got = e.Encode();

        var exp = ByteBuf.Build(36, w =>
        {
            F(w, 1f); F(w, 2f); F(w, 3f);  // p0        (0)
            w.U32(8800);                   // time0     (12)
            w.I16(660);                    // probeID   (16)
            w.Byte(1);                     // exportF   (18)
            w.Pad(1);                      // pad       (19)
            w.I16(770);                    // probetypeID (20)
            w.I16(870);                    // sideID       (22)
            w.I16(970);                    // clusterID    (24)
            w.I16(580);                    // shipID       (26)
            w.I16(680);                    // otTarget     (28)
            w.I16(690);                    // oidTarget    (30)
            w.Byte(1);                     // createNow   (32)
            w.Pad(3);                      // trailing pad → 36
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void DataProbeExport_RejectsNonThirtySixByteBuffer()
    {
        var valid = new DataProbeExport(Vector3.Zero, 0u, 1, true, 2, 3, 4, 5, 0, 0, false).Encode();
        Assert.Throws<InvalidDataException>(() => DataProbeExport.Decode(valid.AsSpan(0, 35)));
        Assert.Throws<InvalidDataException>(() => DataProbeExport.Decode([]));
    }

    [Fact]
    public void DataProbeExport_Values_AppearAtDeclaredOffsets()
    {
        var e = new DataProbeExport(
            P0: new Vector3(10f, 20f, 30f), Time0: 876543210u, ProbeId: 404,
            ExportF: false, ProbetypeId: 707, SideId: 909, ClusterId: 805,
            ShipId: 906, OtTarget: 604, OidTarget: 903, CreateNow: true);
        var b = e.Encode();
        Assert.Equal(10f, BitConverter.ToSingle(b, 0));         // p0.x
        Assert.Equal(30f, BitConverter.ToSingle(b, 8));         // p0.z
        Assert.Equal(876543210u, BitConverter.ToUInt32(b, 12)); // time0
        Assert.Equal(404, BitConverter.ToInt16(b, 16));         // probeID
        Assert.Equal(0, b[18]);                                  // exportF = false → 0
        Assert.Equal(707, BitConverter.ToInt16(b, 20));         // probetypeID after offset-19 pad
        Assert.Equal(909, BitConverter.ToInt16(b, 22));         // sideID
        Assert.Equal(805, BitConverter.ToInt16(b, 24));         // clusterID
        Assert.Equal(906, BitConverter.ToInt16(b, 26));         // shipID
        Assert.Equal(604, BitConverter.ToInt16(b, 28));         // otTarget — 2-byte short, no enum pad
        Assert.Equal(903, BitConverter.ToInt16(b, 30));         // oidTarget
        Assert.Equal(1, b[32]);                                 // createNow
        Assert.Equal(36, b.Length);
        Assert.All(b[33..36], byte_ => Assert.Equal(0, byte_)); // trailing pad region (33-35)
    }

    // ------------------------------------------------------------------ //
    // ExportProbe (FM_S_EXPORT 26, OT_probe 4)                           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ExportProbe_Constants_AreDeclared()
    {
        Assert.Equal((ushort)26, ExportProbe.Fmid);
        Assert.Equal(4, ExportProbe.OtProbe);      // OT_probe = AGC_Probe = 4 (AGCIDL.idl:312, igc.h:123)
        Assert.Equal(36, ExportProbe.DataProbeSize);
        Assert.Equal(48, ExportProbe.FixedSize);   // 4 hdr + 4 varitem + 4 objecttype + 36 blob
    }

    [Fact]
    public void ExportProbe_RoundTrips_ThroughWire()
    {
        var msg = new ExportProbe
        {
            ObjectType = 4,
            Probe = new DataProbeExport(
                P0: new Vector3(12f, 34f, 9021.75f), Time0: 3911u, ProbeId: 933,
                ExportF: true, ProbetypeId: 422, SideId: 733, ClusterId: 844,
                ShipId: -1, OtTarget: 955, OidTarget: 966, CreateNow: false),
        };
        var d = ExportProbe.Decode(msg.Encode());
        Assert.Equal(4, d.ObjectType);
        Assert.Equal(msg.Probe, d.Probe);
        Assert.Equal(new Vector3(12f, 34f, 9021.75f), d.Probe.P0);
        Assert.Equal(3911u, d.Probe.Time0);
        Assert.Equal((short)933, d.Probe.ProbeId);
        Assert.True(d.Probe.ExportF);
        Assert.Equal((short)422, d.Probe.ProbetypeId);
        Assert.Equal((short)733, d.Probe.SideId);
        Assert.Equal((short)844, d.Probe.ClusterId);
        Assert.Equal((short)-1, d.Probe.ShipId);
        Assert.Equal((short)955, d.Probe.OtTarget);
        Assert.Equal((short)966, d.Probe.OidTarget);
        Assert.False(d.Probe.CreateNow);
    }

    [Fact]
    public void ExportProbe_EmitsExactDeclaredBytes()
    {
        var msg = new ExportProbe
        {
            ObjectType = 4,
            Probe = new DataProbeExport(
                P0: new Vector3(1f, 2f, 3f), Time0: 6161u, ProbeId: 717,
                ExportF: true, ProbetypeId: 818, SideId: 919, ClusterId: 626,
                ShipId: 727, OtTarget: 828, OidTarget: 929, CreateNow: true),
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(48, w =>
        {
            w.U16(48);            // cbmsg
            w.U16(26);            // fmid
            w.U16(12);            // ib exportData
            w.U16(36);            // cb exportData = sizeof(DataProbeExport)
            w.I32(4);             // objecttype = OT_probe (int32, 4-byte)
            // ---- DataProbeExport (36 bytes) at message offset 12 ----
            F(w, 1f); F(w, 2f); F(w, 3f);  // p0        (12)
            w.U32(6161);                  // time0     (24)
            w.I16(717);                   // probeID   (28)
            w.Byte(1);                    // exportF   (30)
            w.Pad(1);                     // pad       (31)
            w.I16(818);                   // probetypeID (32)
            w.I16(919);                   // sideID       (34)
            w.I16(626);                   // clusterID    (36)
            w.I16(727);                   // shipID       (38)
            w.I16(828);                   // otTarget     (40)
            w.I16(929);                   // oidTarget    (42)
            w.Byte(1);                    // createNow   (44)
            w.Pad(3);                     // trailing pad → 48
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportProbe_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new ExportProbe
        {
            Probe = new DataProbeExport(Vector3.Zero, 801u, 802, true, 803, 804, 805, 806, 807, 808, false)
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => ExportProbe.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to OT_mine (3) →
        // not OT_probe → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 3;
        Assert.Throws<ArgumentException>(() => ExportProbe.Decode(badOt));

        // Truncate the blob below 36: cb != 36 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 35))];
        Assert.Throws<ArgumentException>(() => ExportProbe.Decode(shortBuf));
    }

    // ------------------------------------------------------------------ //
    // ProbeDestroyed (FM_S_PROBE_DESTROYED, 85)                           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ProbeDestroyed_FixedSize_IsEight()
    {
        // hdr(4) + clusterID(2) + probeID(2) = 8. No alignment gaps: the two
        // 2-byte IDs sit back-to-back at 4-7, no member wider than 2.
        Assert.Equal(8, ProbeDestroyed.FixedSize);
    }

    [Fact]
    public void ProbeDestroyed_RoundTrips_TwoFields()
    {
        var msg = new ProbeDestroyed { ClusterId = 992, ProbeId = 663 };
        var d = ProbeDestroyed.Decode(msg.Encode());
        Assert.Equal(992, d.ClusterId);
        Assert.Equal(663, d.ProbeId);
    }

    [Fact]
    public void ProbeDestroyed_EmitsExactBytes()
    {
        var msg = new ProbeDestroyed { ClusterId = 814, ProbeId = 915 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(8, w =>
        {
            w.U16(8);      // cbmsg
            w.U16(85);     // fmid
            w.I16(814);    // clusterID (SectorID)
            w.I16(915);    // probeID (ProbeID)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ProbeDestroyed_RejectsWrongFmid()
    {
        var bytes = new ProbeDestroyed { ClusterId = 651, ProbeId = 659 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => ProbeDestroyed.Decode(badFmid));
    }

    [Fact]
    public void ProbeDestroyed_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => ProbeDestroyed.Decode(new byte[7]));
    }

    // ------------------------------------------------------------------ //
    // helpers                                                            //
    // ------------------------------------------------------------------ //

    private static void F(ByteBuf.Writer w, float v) =>
        w.U32(BitConverter.SingleToUInt32Bits(v));
}
