using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataMissileTypeIGC</c> payload for <c>OT_missileType</c> (23). Fixed
/// 524 bytes. Source layout: <c>DataObjectIGC</c> (<c>src/Igc/igc.h:1904</c>)
/// + <c>DataExpendableTypeIGC</c> (<c>igc.h:1947</c>) + <c>DataMissileTypeIGC</c>
/// (<c>igc.h:1960</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_missileType.
/// </summary>
/// <remarks>
/// Three alignment pads must be accounted for, all where a narrow/string run
/// meets a 4-aligned float or an even-aligned short:
/// <list type="bullet">
/// <item>DataObjectIGC is 50 bytes (color 16 + radius 4 + rotation 4 + two
/// 13-char names); 2 pad bytes at 50-51 put <c>loadTime</c>@52.</item>
/// <item>Inside the embedded <c>LauncherDef</c>, the base DataBuyableIGC ends
/// at 362; 2 pad bytes at 426-427 put <c>launcherDef.signature</c>@428.</item>
/// <item>DataExpendableTypeIGC ends with <c>iconName</c> at 450 (13 bytes,
/// ending 463); 1 pad byte at 463 puts <c>acceleration</c>@464.</item>
/// </list>
/// All derived offsets are computed forwards from these; each was verified
/// against all 5370 corpus records as a clean finite float / ASCII string.
/// </remarks>
public readonly record struct DataMissileTypeIGC(
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
    float Acceleration,
    float TurnRate,
    float InitialSpeed,
    float LockTime,
    float ReadyTime,
    float MaxLock,
    float ChaffResistance,
    float Dispersion,
    float LockAngle,
    float Power,
    float BlastPower,
    float BlastRadius,
    float Width,
    byte DamageType,
    bool Directional,
    short LaunchSound,
    short AmbientSound)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataMissileTypeIGC)</c>.</summary>
    public const int Size = 524;

    /// <summary>
    /// Decode an <c>OT_missileType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataMissileTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataMissileTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var color = new float[4];
        for (var i = 0; i < 4; i++)
            color[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));

        return new DataMissileTypeIGC(
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
            Acceleration: BinaryPrimitives.ReadSingleLittleEndian(b[464..]),
            TurnRate: BinaryPrimitives.ReadSingleLittleEndian(b[468..]),
            InitialSpeed: BinaryPrimitives.ReadSingleLittleEndian(b[472..]),
            LockTime: BinaryPrimitives.ReadSingleLittleEndian(b[476..]),
            ReadyTime: BinaryPrimitives.ReadSingleLittleEndian(b[480..]),
            MaxLock: BinaryPrimitives.ReadSingleLittleEndian(b[484..]),
            ChaffResistance: BinaryPrimitives.ReadSingleLittleEndian(b[488..]),
            Dispersion: BinaryPrimitives.ReadSingleLittleEndian(b[492..]),
            LockAngle: BinaryPrimitives.ReadSingleLittleEndian(b[496..]),
            Power: BinaryPrimitives.ReadSingleLittleEndian(b[500..]),
            BlastPower: BinaryPrimitives.ReadSingleLittleEndian(b[504..]),
            BlastRadius: BinaryPrimitives.ReadSingleLittleEndian(b[508..]),
            Width: BinaryPrimitives.ReadSingleLittleEndian(b[512..]),
            DamageType: b[516],
            Directional: b[517] != 0,
            LaunchSound: BinaryPrimitives.ReadInt16LittleEndian(b[518..]),
            AmbientSound: BinaryPrimitives.ReadInt16LittleEndian(b[520..]));
    }

    /// <summary>True when this missile type carries the native
    /// <c>c_eabmWarpBomb</c> capability (<c>igc.h:772-774</c>:
    /// <c>c_eabmWarpBomb = c_eabmWarpBombDual(0x02) | c_eabmWarpBombSingle(0x04)
    /// = 0x06</c>). Only these may detonate an aleph (<c>missileigc.cpp:459-466</c>);
    /// a non-warp-bomb missile touching an aleph does nothing, and a warp-bomb
    /// missile touching a non-warp target deals no damage.</summary>
    public bool HasWarpBombCapability => (EabmCapabilities & 0x06) != 0;
}
