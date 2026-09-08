using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>DataProbeExport</c> — the fixed, wire-transmitted form of a probe in
/// the generic <c>FM_S_EXPORT</c>(26) envelope. Source layout:
/// <c>DataProbeExport : public DataProbeBase</c>
/// (<c>src/Igc/igc.h:2598-2623</c>), where the base is
/// <c>DataProbeBase</c> (<c>igc.h:2598-2604</c>). Both halves are quoted in
/// full:
/// <code>
///   struct DataProbeBase {
///       Vector              p0;
///       Time                time0;
///       ProbeID             probeID;
///       bool                exportF;
///   };
///   struct DataProbeExport : DataProbeBase {
///       ExpendableTypeID    probetypeID;
///       SideID              sideID;
///       SectorID            clusterID;
///       ShipID              shipID;
///       ObjectType          otTarget;    // NA when no target
///       ObjectID            oidTarget;   // NA when no target
///       bool                createNow;
///   };
/// </code>
/// Corroborated by <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>),
/// which assigns every field explicitly (matching names, though it writes them
/// in a different textual order — only the declaration order in <c>igc.h</c>
/// determines byte layout) and returns <c>sizeof(DataProbeExport)</c>
/// (<c>probeigc.cpp:567</c>); and by <c>Initialize</c>,
/// <c>probeigc.cpp:60</c> asserts <c>dataSize == sizeof(DataProbeExport)</c>
/// on the export path. Do <b>not</b> confuse with its sibling
/// <c>DataProbeIGC</c> (<c>igc.h:2606-2613</c>), which holds raw native
/// pointers (<c>IprobeTypeIGC*</c>, <c>IsideIGC*</c>…) and is <b>never</b>
/// transmitted.
/// </summary>
/// <remarks>
/// <b>Field-by-field byte layout (C++ default alignment, confirmed
/// empirically</b>: a throwaway <c>g++</c> <c>offsetof</c>/<c>sizeof</c> probe
/// reproduced every offset below exactly). Smallest source of truth is
/// <c>igc.h:2598-2623</c>; each field type maps to a wire primitive as follows —
/// all ID typedefs (<c>ProbeID</c>/<c>ExpendableTypeID</c>/<c>SideID</c>/
/// <c>SectorID</c>/<c>ShipID</c>/<c>ObjectID</c>) are <c>ObjectID</c> =
/// <c>short</c> (<c>igc.h:477,496,497,491,487,480</c>), <c>Time</c> → <c>uint</c>
/// (the established convention in <c>ProjectileInstance.cs:16-18</c>),
/// <c>Vector</c> → the shared 12-byte three-float LE helpers
/// (<c>MissileLaunchData.cs:80-90</c>), <c>ObjectType</c> → the native
/// <c>short</c> (<c>igc.h:115</c>, <b>not</b> the message-level
/// <c>[v1_enum]</c> int32 the EXPORT envelope's own <c>objecttype</c> field
/// uses — that 4-byte enum is a property of <c>ExportShip</c>/<c>ExportMine</c>'s
/// wrapper field, unrelated to this struct's <c>otTarget</c> member), and both
/// <c>bool</c>s are standalone 1-byte fields (nothing here is declared
/// <c>bool :1</c>).
/// <code>
///   offset  0: Vector   p0            (12 bytes, 4-aligned throughout)
///   offset 12: uint     time0         (Time)
///   offset 16: short    probeID       (ProbeID)
///   offset 18: bool     exportF       (1 byte)
///   offset 19: PAD byte              — see padding note below
///   offset 20: short    probetypeID   (ExpendableTypeID)
///   offset 22: short    sideID        (SideID)
///   offset 24: short    clusterID     (SectorID)
///   offset 26: short    shipID        (ShipID)
///   offset 28: short    otTarget      (ObjectType — native short, igc.h:115)
///   offset 30: short    oidTarget     (ObjectID)
///   offset 32: bool     createNow     (1 byte)
///   offset 33: PAD ×3                — trailing pad rounds to 36
///   = 36 bytes total
/// </code>
/// <b>Padding reasoning.</b> After <c>probeID</c>(16-17) and <c>exportF</c>(18)
/// the run is at offset 19, odd. <c>probetypeID</c> is a 2-aligned <c>short</c>,
/// so one pad byte lands at offset 19 pushing <c>probetypeID</c> to 20 — the
/// same narrow-field-run rule <c>DataStationIGC.cs</c> models for this
/// codebase. From 20 to 31 there is a clean back-to-back run of six 2-byte
/// fields (three short IDs, then <c>otTarget</c> and <c>oidTarget</c>: the
/// native <c>ObjectType</c> being a <c>short</c> introduces <b>no</b> 4-byte
/// enum alignment gap — that was the one trap in this struct, and it simply
/// is not present), so <c>otTarget</c> lands at 28 and <c>oidTarget</c> at 30.
/// <c>createNow</c>(32) makes the logical content 33 bytes; the struct's
/// largest-member alignment is 4 (from <c>Vector</c>/<c>Time</c>), so
/// <c>sizeof</c> rounds 33 up to 36 with 3 trailing pad bytes — exactly what
/// the empirical probe returned.
/// </remarks>
public readonly record struct DataProbeExport(
    Vector3 P0,
    uint Time0,
    short ProbeId,
    bool ExportF,
    short ProbetypeId,
    short SideId,
    short ClusterId,
    short ShipId,
    short OtTarget,
    short OidTarget,
    bool CreateNow)
{
    /// <summary>Wire width of the fixed payload: <c>sizeof(DataProbeExport)</c> = 36.</summary>
    public const int Size = 36;

    /// <summary>Encode exactly <see cref="Size"/> bytes, field-by-field.</summary>
    public byte[] Encode()
    {
        var buf = new byte[Size];
        WireWriter w = new(buf);
        WriteVec(ref w, P0);                        // p0        0-11
        w.WriteU32(Time0);                          // time0    12-15
        w.WriteI16(ProbeId);                        // probeID  16-17
        w.WriteByte(ExportF ? (byte)1 : (byte)0);   // exportF  18
        w.WriteZeros(1);                            // pad 19 → probetypeID aligns to 20
        w.WriteI16(ProbetypeId);                    // probetypeID 20-21
        w.WriteI16(SideId);                         // sideID    22-23
        w.WriteI16(ClusterId);                      // clusterID 24-25
        w.WriteI16(ShipId);                         // shipID    26-27
        w.WriteI16(OtTarget);                       // otTarget 28-29 (native short, igc.h:115)
        w.WriteI16(OidTarget);                      // oidTarget 30-31
        w.WriteByte(CreateNow ? (byte)1 : (byte)0); // createNow 32
        w.WriteZeros(3);                            // trailing pad → 36
        if (w.Position != Size) throw new InvalidOperationException($"DataProbeExport layout drift: {w.Position} != {Size}");
        return buf;
    }

    /// <summary>Decode an <c>OT_probe</c> payload — the inverse of
    /// <see cref="Encode"/>. Requires exactly <see cref="Size"/> bytes.</summary>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataProbeExport Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException($"DataProbeExport expects {Size} bytes, got {b.Length}");
        WireReader r = new(b);
        var p0 = ReadVec(ref r);
        uint time0 = r.ReadU32();
        short probeId = r.ReadI16();
        bool exportF = r.ReadByte() != 0;
        r.Advance(1);                           // pad
        short probetypeId = r.ReadI16();
        short sideId = r.ReadI16();
        short clusterId = r.ReadI16();
        short shipId = r.ReadI16();
        short otTarget = r.ReadI16();
        short oidTarget = r.ReadI16();
        bool createNow = r.ReadByte() != 0;
        r.Advance(3);                           // trailing pad
        return new DataProbeExport(p0, time0, probeId, exportF, probetypeId,
            sideId, clusterId, shipId, otTarget, oidTarget, createNow);
    }

    // Shared 12-byte three-float LE helpers (ProjectileInstance.cs:127-137 /
    // MissileLaunchData.cs:80-90).
    private static void WriteVec(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private static Vector3 ReadVec(ref WireReader r) =>
        new(BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()));
}
