using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataChaffTypeIGC</c> payload for <c>OT_chaffType</c> (26). Fixed 468
/// bytes. Source layout: <c>DataObjectIGC</c> (<c>src/Igc/igc.h:1904</c>) +
/// <c>DataExpendableTypeIGC</c> (<c>igc.h:1947</c>) + <c>DataChaffTypeIGC</c>
/// (<c>igc.h:1998</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_chaffType.
/// </summary>
/// <remarks>
/// The smallest expendable variant: it adds a single float
/// (<c>chaffStrength</c>). Alignment mirrors the missile/probe bases: 2 pad
/// bytes after DataObjectIGC put <c>loadTime</c>@52, and 2 after the embedded
/// LauncherDef's DataBuyableIGC base put <c>launcherDef.signature</c>@428, but
/// here there is a <b>third</b> correction unique to the chaff tail — 1 pad
/// byte after <c>iconName</c> (which ends at 463) puts <c>chaffStrength</c>@464.
/// The 468 total is 4-aligned. Verified against all 619 corpus records.
/// </remarks>
public readonly record struct DataChaffTypeIGC(
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
    float ChaffStrength)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataChaffTypeIGC)</c>.</summary>
    public const int Size = 468;

    /// <summary>
    /// Decode an <c>OT_chaffType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataChaffTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataChaffTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var color = new float[4];
        for (var i = 0; i < 4; i++)
            color[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));

        return new DataChaffTypeIGC(
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
            ChaffStrength: BinaryPrimitives.ReadSingleLittleEndian(b[464..]));
    }
}
