using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataDroneTypeIGC</c> payload for <c>OT_droneType</c> (33). Fixed 384
/// bytes. Source layout: <c>DataBuyableIGC</c> (<c>src/Igc/igc.h:1753</c>) +
/// <c>DataDroneTypeIGC</c> (<c>igc.h:2698</c>), C++ default alignment. See wiki
/// page <c>allegiance-igc-file-format</c> §OT_droneType.
/// </summary>
/// <remarks>
/// The DataBuyableIGC base ends at 362, so two pad bytes put
/// <c>shootSkill</c>@364. Thereafter <c>pilotType</c> is a single byte at 376;
/// the following <c>hullTypeID</c> short lands on 378 (even) with no pad. The
/// remaining three IDs are all shorts and even-aligned, so no further
/// alignment correction is needed. The float triple at 364/368/372 stays
/// clean across all 6,503 corpus records.
/// </remarks>
public readonly record struct DataDroneTypeIGC(
    int Price,
    uint TimeToBuild,
    string ModelName,
    string IconName,
    string Name,
    string Description,
    byte GroupId,
    byte[] TtbmRequired,
    byte[] TtbmEffects,
    float ShootSkill,
    float MoveSkill,
    float Bravery,
    byte PilotType,
    short HullTypeId,
    short DroneTypeId,
    short EtidLaid)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataDroneTypeIGC)</c>.</summary>
    public const int Size = 384;

    /// <summary>
    /// Decode an <c>OT_droneType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataDroneTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataDroneTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        return new DataDroneTypeIGC(
            Price: BinaryPrimitives.ReadInt32LittleEndian(b[0..]),
            TimeToBuild: BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            ModelName: Fix(b, 8, 14),
            IconName: Fix(b, 22, 13),
            Name: Fix(b, 35, 25),
            Description: Fix(b, 60, 201),
            GroupId: b[261],
            TtbmRequired: b.Slice(262, 50).ToArray(),
            TtbmEffects: b.Slice(312, 50).ToArray(),
            ShootSkill: BinaryPrimitives.ReadSingleLittleEndian(b[364..]),
            MoveSkill: BinaryPrimitives.ReadSingleLittleEndian(b[368..]),
            Bravery: BinaryPrimitives.ReadSingleLittleEndian(b[372..]),
            PilotType: b[376],
            HullTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[378..]),
            DroneTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[380..]),
            EtidLaid: BinaryPrimitives.ReadInt16LittleEndian(b[382..]));
    }
}
