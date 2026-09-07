using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>DataMineExport</c> — the fixed, wire-transmitted form of a mine in the
/// generic <c>FM_S_EXPORT</c>(26) envelope. Source layout:
/// <c>DataMineExport : public DataMineBase</c> (<c>src/Igc/igc.h:2549-2572</c>,
/// quoted again verbatim in a doc block at <c>src/FedSrv/FedSrv.CPP:10011-10034</c>
/// and corroborated by the <c>MineInstances</c> SQL column mapping just below,
/// <c>FedSrv.CPP:10036-10051</c>: <c>MineID/MineTypeID/SideID/SectorID/
/// LocationX,Y,Z</c> agrees field-for-field). Do not confuse with its sibling
/// <c>DataMineIGC</c> (<c>igc.h:2557-2563</c>), which holds raw native
/// pointers (<c>ImineTypeIGC*</c>, <c>IshipIGC*</c>…) and is <b>never</b> what
/// goes over the network — the reference exporter always writes
/// <c>sizeof(DataMineExport)</c> (<c>src/Igc/mineigc.cpp:160</c>).
/// </summary>
/// <remarks>
/// <b>Field-by-field byte layout (C++ default alignment, confirmed
/// empirically</b>: a throwaway <c>g++</c> <c>offsetof</c>/<c>sizeof</c> probe
/// reproduced every offset below exactly). Smallest source of truth is
/// <c>igc.h:2549-2572</c>; each field type maps to a wire primitive as follows —
/// all ID typedefs (<c>MineID</c>/<c>SectorID</c>/<c>ExpendableTypeID</c>/
/// <c>ShipID</c>/<c>SideID</c>) are <c>ObjectID</c> = <c>short</c>
/// (<c>igc.h:477,487,495,497,498,491</c>), <c>Time</c> → <c>uint</c> (the
/// established convention in <c>ProjectileInstance.cs:16-18</c>), <c>Vector</c>
/// → the shared 12-byte three-float LE helpers (<c>MissileLaunchData.cs:80-90</c>),
/// <c>BytePercentage</c> → one byte via <c>CompactQuant.Percent</c>/
/// <c>DePercent</c> (the same convention <c>ServerShipUpdate.cs:73-87</c> uses
/// for hull/shield/ammo fractions; divisor 240, <c>Utility.h:1312</c>), and
/// both <c>bool</c>s are standalone 1-byte fields (nothing here is declared
/// <c>bool :1</c>).
/// <code>
///   offset  0: Vector   p0            (12 bytes, 4-aligned throughout)
///   offset 12: uint     time0         (Time)
///   offset 16: short    mineID        (MineID)
///   offset 18: bool     exportF       (1 byte)
///   offset 19: PAD byte              — see padding note below
///   offset 20: short    clusterID     (SectorID)
///   offset 22: short    minetypeID    (ExpendableTypeID)
///   offset 24: short    launcherID    (ShipID)
///   offset 26: short    sideID        (SideID)
///   offset 28: byte     fraction      (BytePercentage)
///   offset 29: bool     createNow     (1 byte)
///   offset 30: PAD, PAD (2 bytes)     — trailing pad rounds to 32
///   = 32 bytes total
/// </code>
/// <b>Padding reasoning.</b> After <c>mineID</c>(16-17) and <c>exportF</c>(18)
/// the run is at offset 19, odd. <c>clusterID</c> is a 2-aligned <c>short</c>,
/// so one pad byte lands at offset 19 pushing <c>clusterID</c> to 20 — the
/// same narrow-field-run rule <c>DataStationIGC.cs</c> models for this
/// codebase. Four <c>short</c>s follow back-to-back (20-27), then
/// <c>fraction</c>(28) and <c>createNow</c>(29) make the logical content 30
/// bytes; the struct's largest member alignment is 4 (from <c>Vector</c>/
/// <c>Time</c>), so <c>sizeof</c> rounds 30 up to 32 with 2 trailing pad bytes
/// — exactly what the empirical probe returned.
/// </remarks>
public readonly record struct DataMineExport(
    Vector3 P0,
    uint Time0,
    short MineId,
    bool ExportF,
    short ClusterId,
    short MinetypeId,
    short LauncherId,
    short SideId,
    byte Fraction,
    bool CreateNow)
{
    /// <summary>Wire width of the fixed payload: <c>sizeof(DataMineExport)</c> = 32.</summary>
    public const int Size = 32;

    /// <summary>The mine's health/damage fraction (0..1) recovered from the
    /// <c>BytePercentage</c> byte (div-240 convention, <c>Utility.h:1312</c>).</summary>
    public float FractionValue => CompactQuant.DePercent(Fraction);

    /// <summary>Encode exactly <see cref="Size"/> bytes, field-by-field.</summary>
    public byte[] Encode()
    {
        var buf = new byte[Size];
        WireWriter w = new(buf);
        WriteVec(ref w, P0);                    // p0       0-11
        w.WriteU32(Time0);                      // time0   12-15
        w.WriteI16(MineId);                     // mineID  16-17
        w.WriteByte(ExportF ? (byte)1 : (byte)0); // exportF 18
        w.WriteZeros(1);                        // pad at 19 → clusterID aligns to 20
        w.WriteI16(ClusterId);                  // clusterID  20-21
        w.WriteI16(MinetypeId);                 // minetypeID 22-23
        w.WriteI16(LauncherId);                 // launcherID 24-25
        w.WriteI16(SideId);                     // sideID     26-27
        w.WriteByte(Fraction);                  // fraction   28
        w.WriteByte(CreateNow ? (byte)1 : (byte)0); // createNow 29
        w.WriteZeros(2);                        // trailing pad → 32
        if (w.Position != Size) throw new InvalidOperationException($"DataMineExport layout drift: {w.Position} != {Size}");
        return buf;
    }

    /// <summary>Decode an <c>OT_mine</c> payload — the inverse of
    /// <see cref="Encode"/>. Requires exactly <see cref="Size"/> bytes.</summary>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataMineExport Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException($"DataMineExport expects {Size} bytes, got {b.Length}");
        WireReader r = new(b);
        var p0 = ReadVec(ref r);
        uint time0 = r.ReadU32();
        short mineId = r.ReadI16();
        bool exportF = r.ReadByte() != 0;
        r.Advance(1);                           // pad
        short clusterId = r.ReadI16();
        short minetypeId = r.ReadI16();
        short launcherId = r.ReadI16();
        short sideId = r.ReadI16();
        byte fraction = r.ReadByte();
        bool createNow = r.ReadByte() != 0;
        r.Advance(2);                           // trailing pad
        return new DataMineExport(p0, time0, mineId, exportF, clusterId,
            minetypeId, launcherId, sideId, fraction, createNow);
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
