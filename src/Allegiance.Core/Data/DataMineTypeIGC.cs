using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataMineTypeIGC</c> payload for <c>OT_mineType</c> (24). Fixed 480
/// bytes. Source layout: <c>DataObjectIGC</c> (<c>src/Igc/igc.h:1904</c>) +
/// <c>DataExpendableTypeIGC</c> (<c>igc.h:1947</c>) + <c>DataMineTypeIGC</c>
/// (<c>igc.h:1990</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_mineType.
/// </summary>
/// <remarks>
/// Shares the expendable base layout with missile/probe/chaff (two 2-byte
/// pads after DataObjectIGC and the embedded LauncherDef's DataBuyableIGC
/// base respectively) plus one more: <c>iconName</c> ends at 463, so a single
/// pad byte puts <c>radius</c>@464. The mine tail is three floats then a byte
/// (<c>damageType</c>@476); the struct rounds up to 480 (three trailing pad
/// bytes, the largest-member alignment of 4). All offsets verified against
/// all 782 corpus records.
/// </remarks>
public readonly record struct DataMineTypeIGC(
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
    float MineRadius,   // mine blast radius; base model Radius is above
    float Power,
    float Endurance,
    byte DamageType)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataMineTypeIGC)</c>.</summary>
    public const int Size = 480;

    /// <summary>
    /// Decode an <c>OT_mineType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataMineTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataMineTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var color = new float[4];
        for (var i = 0; i < 4; i++)
            color[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));

        return new DataMineTypeIGC(
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
            MineRadius: BinaryPrimitives.ReadSingleLittleEndian(b[464..]),
            Power: BinaryPrimitives.ReadSingleLittleEndian(b[468..]),
            Endurance: BinaryPrimitives.ReadSingleLittleEndian(b[472..]),
            DamageType: b[476]);
    }
}
