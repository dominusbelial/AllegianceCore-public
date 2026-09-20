using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>DataBuildingEffectExport</c> — the fixed, wire-transmitted form of an
/// in-progress building effect in the generic <c>FM_S_EXPORT</c>(26) envelope
/// (this port's name for the blob native exports as
/// <c>DataBuildingEffectExport</c>, <c>src/Igc/igc.h:2591-2596</c>). Source
/// layout, quoted in full:
/// <code>
///   struct DataBuildingEffectBase {
///       Time                timeStart;
///       float               radiusAsteroid;
///       float               radiusStation;
///       Vector              positionStart;
///       Vector              positionStop;
///   };
///   struct DataBuildingEffectExport : DataBuildingEffectBase {
///       SectorID            clusterID;
///       AsteroidID          asteroidID;
///       Color               color;
///   };
/// </code>
/// (both halves: <c>src/Igc/igc.h:2574-2596</c>), corroborated by
/// <c>CbuildingEffectIGC::Export</c> (<c>src/Igc/buildingeffectigc.h:136-158</c>),
/// which assigns every field explicitly and returns
/// <c>sizeof(DataBuildingEffectExport)</c>. Do <b>not</b> confuse with its
/// in-memory sibling <c>DataBuildingEffectIGC</c>
/// (<c>igc.h:2583-2589</c>), which holds raw native pointers
/// (<c>IclusterIGC*</c>/<c>IshipIGC*</c>/<c>IasteroidIGC*</c>/<c>IstationIGC*</c>)
/// and is <b>never</b> transmitted.
/// </summary>
/// <remarks>
/// <b>Field-by-field byte layout (C++ default alignment).</b> All ID typedefs
/// (<c>SectorID</c>/<c>AsteroidID</c>) are <c>short</c>
/// (<c>igc.h:491,492</c>), <c>Time</c> → <c>uint</c> (the established port
/// convention, <c>DataProbeExport.cs</c> / <c>ProjectileInstance.cs</c>),
/// <c>Vector</c> → the shared 12-byte three-float LE helpers
/// (<c>MissileLaunchData.cs:80-90</c>), and <c>Color</c> is the native
/// <c>Color</c> class of <b>four</b> <c>float</c>s (<c>r,g,b,a</c>,
/// <c>src/Utility/color.h</c>) = 16 bytes, memcpy'd verbatim into the blob
/// exactly as <c>CbuildingEffectIGC::Export</c> writes it. The struct's
/// largest member is 4-aligned (<c>float</c>/<c>uint</c>/<c>Vector</c>/
/// <c>Color</c>), so offsets run clean and <c>sizeof</c> needs no trailing pad:
/// <code>
///   offset  0: uint     timeStart        (Time)
///   offset  4: float    radiusAsteroid
///   offset  8: float    radiusStation
///   offset 12: Vector   positionStart    (12 bytes)
///   offset 24: Vector   positionStop     (12 bytes)
///   offset 36: short    clusterID        (SectorID)
///   offset 38: short    asteroidID       (AsteroidID)
///   offset 40: Color    color            (4 floats, 16 bytes)
///   = 56 bytes total (4-aligned, no trailing pad)
/// </code>
/// <c>timeStart</c> is the native <c>m_timeEnvelope</c> converted back to a
/// server tick (<c>ServerTimeFromClientTime</c>, <c>buildingeffectigc.h:142</c>);
/// in this port it is the server tick the build began (<c>(uint)TickIndex</c>).
/// The <c>color</c> is the building side's colour (<c>buildingeffectigc.h:154</c>);
/// this port's renderer uses its own under-construction treatment, but the
/// field is preserved on the wire for byte fidelity.
/// </remarks>
public readonly record struct DataBuildingEffectExport(
    uint TimeStart,
    float RadiusAsteroid,
    float RadiusStation,
    Vector3 PositionStart,
    Vector3 PositionStop,
    short ClusterId,
    short AsteroidId,
    float ColorR,
    float ColorG,
    float ColorB,
    float ColorA)
{
    /// <summary>Wire width of the fixed payload: <c>sizeof(DataBuildingEffectExport)</c> = 56.</summary>
    public const int Size = 56;

    /// <summary>Encode exactly <see cref="Size"/> bytes, field-by-field.</summary>
    public byte[] Encode()
    {
        var buf = new byte[Size];
        WireWriter w = new(buf);
        w.WriteU32(TimeStart);                              // timeStart        0-3
        WriteF(ref w, RadiusAsteroid);                      // radiusAsteroid   4-7
        WriteF(ref w, RadiusStation);                       // radiusStation    8-11
        WriteVec(ref w, PositionStart);                     // positionStart    12-23
        WriteVec(ref w, PositionStop);                      // positionStop     24-35
        w.WriteI16(ClusterId);                              // clusterID        36-37
        w.WriteI16(AsteroidId);                             // asteroidID       38-39
        WriteF(ref w, ColorR);                              // color.r          40
        WriteF(ref w, ColorG);                              // color.g          44
        WriteF(ref w, ColorB);                              // color.b          48
        WriteF(ref w, ColorA);                              // color.a          52
        if (w.Position != Size) throw new InvalidOperationException($"DataBuildingEffectExport layout drift: {w.Position} != {Size}");
        return buf;
    }

    /// <summary>Decode an <c>OT_buildingEffect</c> payload — the inverse of
    /// <see cref="Encode"/>. Requires exactly <see cref="Size"/> bytes.</summary>
    public static DataBuildingEffectExport Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException($"DataBuildingEffectExport expects {Size} bytes, got {b.Length}");
        WireReader r = new(b);
        uint timeStart = r.ReadU32();
        float radiusAsteroid = ReadF(ref r);
        float radiusStation = ReadF(ref r);
        var posStart = ReadVec(ref r);
        var posStop = ReadVec(ref r);
        short clusterId = r.ReadI16();
        short asteroidId = r.ReadI16();
        float cr = ReadF(ref r);
        float cg = ReadF(ref r);
        float cb = ReadF(ref r);
        float ca = ReadF(ref r);
        return new DataBuildingEffectExport(
            timeStart, radiusAsteroid, radiusStation, posStart, posStop,
            clusterId, asteroidId, cr, cg, cb, ca);
    }

    // Shared 12-byte three-float LE helpers (DataProbeExport.cs:150-162).
    private static void WriteF(ref WireWriter w, float v) =>
        w.WriteU32(BitConverter.SingleToUInt32Bits(v));

    private static float ReadF(ref WireReader r) =>
        BitConverter.UInt32BitsToSingle(r.ReadU32());

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
