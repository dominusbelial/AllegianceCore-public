using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataProbeTypeIGC</c> payload for <c>OT_probeType</c> (25). Fixed 492
/// bytes. Source layout: <c>DataObjectIGC</c> (<c>src/Igc/igc.h:1904</c>) +
/// <c>DataExpendableTypeIGC</c> (<c>igc.h:1947</c>) + <c>DataProbeTypeIGC</c>
/// (<c>igc.h:2003</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_probeType.
/// </summary>
/// <remarks>
/// <para>Expendable-ability rescue bits (<c>src/Igc/igc.h:781-782</c>), named
/// because P780 reads them server-side for the first time: <see cref="EabmRescue"/>
/// rescues a lifepod that collides with the probe when the pod's side is the
/// probe's own or an ally; <see cref="EabmRescueAny"/> rescues any lifepod
/// regardless of side. Native aliases both straight from the station bits:
/// <c>c_eabmRescue = c_sabmRescue</c> and <c>c_eabmRescueAny = c_sabmRescueAny</c>.</para>
/// Same three alignment corrections as the missile type, computed the same
/// way: 2 pad bytes after DataObjectIGC put <c>loadTime</c>@52; 2 after the
/// embedded LauncherDef's DataBuyableIGC base put
/// <c>launcherDef.signature</c>@428; and 1 after <c>iconName</c> puts
/// <c>scannerRange</c>@464. The probe-specific tail (<c>ammo</c>,
/// <c>projectileTypeID</c>, <c>ambientSound</c>, <c>dtRipcord</c>) is already
/// even/4-aligned at 480/482/484/488, so no further padding. The total 492 is
/// 4-aligned. All offsets verified against all 4,760 corpus records.
/// </remarks>
public readonly record struct DataProbeTypeIGC(
    float[] Color,
    float Radius,
    float Rotation,
    string ModelName,
    string TextureName,
    float LoadTime,
    float Lifespan,
    float Signature,
    int Price,
    uint TimeToBuild,
    string LauncherModelName,
    string LauncherIconName,
    string LauncherName,
    string LauncherDescription,
    byte LauncherGroupId,
    byte[] LauncherTtbmRequired,
    byte[] LauncherTtbmEffects,
    float LauncherSignature,
    float LauncherMass,
    short LauncherPartMask,
    short LauncherExpendableSize,
    float HitPoints,
    byte DefenseType,
    short ExpendableTypeId,
    short EabmCapabilities,
    string IconName,
    float ScannerRange,
    float DtimeBurst,
    float Dispersion,
    float Accuracy,
    short Ammo,
    short ProjectileTypeId,
    short AmbientSound,
    float DtRipcord)
{
    /// <summary><c>c_eabmRescue = c_sabmRescue = 0x2000</c> (<c>src/Igc/igc.h:781</c> —
    /// &quot;Rescue lifepods that collide with it&quot;): own-side or allied pods only.
    /// P780 is the first server-side reader.</summary>
    public const short EabmRescue = 0x2000;

    /// <summary><c>c_eabmRescueAny = c_sabmRescueAny = 0x4000</c> (<c>src/Igc/igc.h:782</c> —
    /// &quot;Rescue any lifepod that collide with it&quot;): any pod, any side.</summary>
    public const short EabmRescueAny = 0x4000;

    /// <summary>Exact payload size in bytes: <c>sizeof(DataProbeTypeIGC)</c>.</summary>
    public const int Size = 492;

    /// <summary>
    /// Decode an <c>OT_probeType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataProbeTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataProbeTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var color = new float[4];
        for (var i = 0; i < 4; i++)
            color[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));

        return new DataProbeTypeIGC(
            Color: color,
            Radius: BinaryPrimitives.ReadSingleLittleEndian(b[16..]),
            Rotation: BinaryPrimitives.ReadSingleLittleEndian(b[20..]),
            ModelName: Fix(b, 24, 13),
            TextureName: Fix(b, 37, 13),
            LoadTime: BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            Lifespan: BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[60..]),
            Price: BinaryPrimitives.ReadInt32LittleEndian(b[64..]),
            TimeToBuild: BinaryPrimitives.ReadUInt32LittleEndian(b[68..]),
            LauncherModelName: Fix(b, 72, 14),
            LauncherIconName: Fix(b, 86, 13),
            LauncherName: Fix(b, 99, 25),
            LauncherDescription: Fix(b, 124, 201),
            LauncherGroupId: b[325],
            LauncherTtbmRequired: b.Slice(326, 50).ToArray(),
            LauncherTtbmEffects: b.Slice(376, 50).ToArray(),
            LauncherSignature: BinaryPrimitives.ReadSingleLittleEndian(b[428..]),
            LauncherMass: BinaryPrimitives.ReadSingleLittleEndian(b[432..]),
            LauncherPartMask: BinaryPrimitives.ReadInt16LittleEndian(b[436..]),
            LauncherExpendableSize: BinaryPrimitives.ReadInt16LittleEndian(b[438..]),
            HitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[440..]),
            DefenseType: b[444],
            ExpendableTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[446..]),
            EabmCapabilities: BinaryPrimitives.ReadInt16LittleEndian(b[448..]),
            IconName: Fix(b, 450, 13),
            ScannerRange: BinaryPrimitives.ReadSingleLittleEndian(b[464..]),
            DtimeBurst: BinaryPrimitives.ReadSingleLittleEndian(b[468..]),
            Dispersion: BinaryPrimitives.ReadSingleLittleEndian(b[472..]),
            Accuracy: BinaryPrimitives.ReadSingleLittleEndian(b[476..]),
            Ammo: BinaryPrimitives.ReadInt16LittleEndian(b[480..]),
            ProjectileTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[482..]),
            AmbientSound: BinaryPrimitives.ReadInt16LittleEndian(b[484..]),
            DtRipcord: BinaryPrimitives.ReadSingleLittleEndian(b[488..]));
    }
}
