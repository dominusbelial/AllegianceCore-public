using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P294 building-effect codecs:
/// <see cref="DataBuildingEffectExport"/> (the fixed OT_buildingEffect blob
/// inside FM_S_EXPORT) and <see cref="ExportBuildingEffect"/> (FM_S_EXPORT 26,
/// OT_buildingEffect 11). Layouts are pinned against programmatically-built
/// expected buffers (the <see cref="ByteBuf"/> declarative builder), the same
/// discipline as <see cref="ProbeWireCodecsWireTests"/>.
/// </summary>
public class BuildingEffectWireCodecsWireTests
{
    // ------------------------------------------------------------------ //
    // DataBuildingEffectExport — the fixed OT_buildingEffect blob          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DataBuildingEffectExport_Size_IsFiftySix()
    {
        // Field-by-field (C++ default alignment, 4-aligned throughout — no
        // trailing pad; largest member is float/uint/Vector/Color = 4):
        //   0-3   uint     timeStart
        //   4-7   float    radiusAsteroid
        //   8-11  float    radiusStation
        //  12-23  Vector   positionStart   (12 B)
        //  24-35  Vector   positionStop    (12 B)
        //  36-37  short    clusterID
        //  38-39  short    asteroidID
        //  40-55  Color    color           (4 floats, 16 B)
        Assert.Equal(56, DataBuildingEffectExport.Size);
        Assert.Equal(56, ExportBuildingEffect.DataBuildingEffectSize);
        Assert.Equal(68, ExportBuildingEffect.FixedSize);   // 4 hdr + 4 varitem + 4 objecttype + 56 blob
    }

    [Fact]
    public void DataBuildingEffectExport_RoundTrips_EveryField()
    {
        var e = new DataBuildingEffectExport(
            TimeStart: 4711u,
            RadiusAsteroid: 950.5f,
            RadiusStation: 302.5f,
            PositionStart: new Vector3(100f, 200f, 300f),
            PositionStop: new Vector3(4000f, 5100f, 6200f),
            ClusterId: 883,
            AsteroidId: 994,
            ColorR: 0.2f, ColorG: 0.11f, ColorB: 0.57f, ColorA: 1f);
        var d = DataBuildingEffectExport.Decode(e.Encode());
        Assert.Equal(e, d);          // record-struct equality covers all 11 fields
        Assert.Equal(4711u, d.TimeStart);
        Assert.Equal(302.5f, d.RadiusStation);
        Assert.Equal(new Vector3(4000f, 5100f, 6200f), d.PositionStop);
    }

    [Fact]
    public void DataBuildingEffectExport_EmitsExactFiftySixBytes()
    {
        var e = new DataBuildingEffectExport(
            TimeStart: 8912u,
            RadiusAsteroid: 501f,
            RadiusStation: 823f,
            PositionStart: new Vector3(1f, 2f, 3f),
            PositionStop: new Vector3(4f, 5f, 6f),
            ClusterId: 737,
            AsteroidId: 838,
            ColorR: 949f, ColorG: 959f, ColorB: 969f, ColorA: 979f);
        var got = e.Encode();

        var exp = ByteBuf.Build(56, w =>
        {
            w.U32(8912);                   // timeStart      (0)
            F(w, 501f);                    // radiusAsteroid (4)
            F(w, 823f);                    // radiusStation  (8)
            F(w, 1f); F(w, 2f); F(w, 3f);  // positionStart  (12)
            F(w, 4f); F(w, 5f); F(w, 6f);  // positionStop   (24)
            w.I16(737);                    // clusterID      (36)
            w.I16(838);                    // asteroidID     (38)
            F(w, 949f); F(w, 959f); F(w, 969f); F(w, 979f);  // color (40)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void DataBuildingEffectExport_Values_AppearAtDeclaredOffsets()
    {
        var e = new DataBuildingEffectExport(
            TimeStart: 654321u, RadiusAsteroid: 111f, RadiusStation: 222f,
            PositionStart: new Vector3(10f, 20f, 30f), PositionStop: new Vector3(40f, 50f, 60f),
            ClusterId: 515, AsteroidId: 517,
            ColorR: 018f, ColorG: 019f, ColorB: 020f, ColorA: 022f);
        var b = e.Encode();
        Assert.Equal(654321u, BitConverter.ToUInt32(b, 0));   // timeStart
        Assert.Equal(111f, BitConverter.ToSingle(b, 4));      // radiusAsteroid
        Assert.Equal(222f, BitConverter.ToSingle(b, 8));      // radiusStation
        Assert.Equal(10f, BitConverter.ToSingle(b, 12));      // positionStart.x
        Assert.Equal(60f, BitConverter.ToSingle(b, 32));      // positionStop.z
        Assert.Equal(515, BitConverter.ToInt16(b, 36));       // clusterID
        Assert.Equal(517, BitConverter.ToInt16(b, 38));       // asteroidID
        Assert.Equal(019f, BitConverter.ToSingle(b, 44));     // color.g
        Assert.Equal(022f, BitConverter.ToSingle(b, 52));     // color.a
        Assert.Equal(56, b.Length);
    }

    [Fact]
    public void DataBuildingEffectExport_RejectsNonFiftySixByteBuffer()
    {
        var valid = new DataBuildingEffectExport(
            0u, 1f, 2f, Vector3.Zero, Vector3.Zero, 3, 4, 0f, 0f, 0f, 0f).Encode();
        Assert.Throws<InvalidDataException>(() => DataBuildingEffectExport.Decode(valid.AsSpan(0, 55)));
        Assert.Throws<InvalidDataException>(() => DataBuildingEffectExport.Decode([]));
    }

    // ------------------------------------------------------------------ //
    // ExportBuildingEffect (FM_S_EXPORT 26, OT_buildingEffect 11)          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ExportBuildingEffect_Constants_AreDeclared()
    {
        Assert.Equal((ushort)26, ExportBuildingEffect.Fmid);
        Assert.Equal(11, ExportBuildingEffect.OtBuildingEffect);  // OT_buildingEffect = AGC_BuildingEffect = 11 (AGCIDL.idl:319, igc.h:131)
        Assert.Equal((short)IgcObjectType.BuildingEffect, (short)11);
        Assert.Equal(56, ExportBuildingEffect.DataBuildingEffectSize);
        Assert.Equal(68, ExportBuildingEffect.FixedSize);   // 4 hdr + 4 varitem + 4 objecttype + 56 blob
    }

    [Fact]
    public void ExportBuildingEffect_RoundTrips_ThroughWire()
    {
        var msg = new ExportBuildingEffect
        {
            ObjectType = 11,
            Effect = new DataBuildingEffectExport(
                TimeStart: 9753u, RadiusAsteroid: 822f, RadiusStation: 944f,
                PositionStart: new Vector3(1f, 2f, 3f), PositionStop: new Vector3(7f, 8f, 9f),
                ClusterId: 514, AsteroidId: 826, ColorR: 827f, ColorG: 028f, ColorB: 029f, ColorA: 030f),
        };
        var d = ExportBuildingEffect.Decode(msg.Encode());
        Assert.Equal(11, d.ObjectType);
        Assert.Equal(msg.Effect, d.Effect);
        Assert.Equal(9753u, d.Effect.TimeStart);
        Assert.Equal(944f, d.Effect.RadiusStation);
        Assert.Equal((short)826, d.Effect.AsteroidId);
    }

    [Fact]
    public void ExportBuildingEffect_EmitsExactDeclaredBytes()
    {
        var msg = new ExportBuildingEffect
        {
            ObjectType = 11,
            Effect = new DataBuildingEffectExport(
                TimeStart: 8931u, RadiusAsteroid: 011f, RadiusStation: 033f,
                PositionStart: new Vector3(4f, 5f, 6f), PositionStop: new Vector3(7f, 8f, 9f),
                ClusterId: 042, AsteroidId: 044, ColorR: 046f, ColorG: 048f, ColorB: 050f, ColorA: 052f),
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(68, w =>
        {
            w.U16(68);            // cbmsg
            w.U16(26);            // fmid
            w.U16(12);            // ib exportData
            w.U16(56);            // cb exportData = sizeof(DataBuildingEffectExport)
            w.U32(11);            // objecttype = OT_buildingEffect (int32, 4-byte)
            // ---- DataBuildingEffectExport (56 bytes) at message offset 12 ----
            w.U32(8931);                  // timeStart      (12)
            F(w, 011f);                   // radiusAsteroid (16)
            F(w, 033f);                   // radiusStation  (20)
            F(w, 4f); F(w, 5f); F(w, 6f); // positionStart  (24)
            F(w, 7f); F(w, 8f); F(w, 9f); // positionStop   (36)
            w.I16(042);                   // clusterID      (48)
            w.I16(044);                   // asteroidID     (50)
            F(w, 046f); F(w, 048f); F(w, 050f); F(w, 052f);  // color (52)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportBuildingEffect_RejectsWrongFmid_WrongObjectType_WrongBlobSize()
    {
        var bytes = new ExportBuildingEffect
        {
            Effect = new DataBuildingEffectExport(1u, 2f, 3f, Vector3.Zero, Vector3.Zero, 506, 508, 509f, 010f, 011f, 013f)
        }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => ExportBuildingEffect.Decode(badFmid));

        // objecttype sits at message offset 8 (int32). Set to OT_probe (4) →
        // not OT_buildingEffect → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 4;
        Assert.Throws<ArgumentException>(() => ExportBuildingEffect.Decode(badOt));

        // Truncate the blob below 56: cb != 56 → reject.
        var shortBuf = bytes[..^(bytes.Length - (12 + 55))];
        Assert.Throws<ArgumentException>(() => ExportBuildingEffect.Decode(shortBuf));
    }

    private static void F(ByteBuf.Writer w, float v) =>
        w.U32(BitConverter.SingleToUInt32Bits(v));
}
