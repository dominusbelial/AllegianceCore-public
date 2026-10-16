using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataLauncherTypeIGC</c> payload for <c>OT_partType</c> (30) when the
/// record is 24 bytes. This is the odd one out among part-type records: a
/// launcher, not a part. The engine chooses <c>ClauncherTypeIGC</c> over
/// <c>CpartTypeIGC</c> purely by record size
/// (<c>src/Igc/missionigc.cpp:3344-3355</c>, asserted
/// <c>sizeof(DataLauncherTypeIGC) &lt; sizeof(DataPartTypeIGC)</c>). Source:
/// <c>src/Igc/igc.h:1881</c>, C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_partType.
/// </summary>
/// <remarks>This is the compact launcher form referenced by
/// <c>magazine</c>/<c>dispenser</c>/<c>chaff</c> part loads — the expendable
/// it launches is named by <see cref="ExpendabletypeId"/>.</remarks>
public readonly record struct DataLauncherTypeIGC(
    short Amount,
    short PartId,
    short SuccessorPartId,
    short LaunchCount,
    short ExpendabletypeId,
    string InventoryLineMdl) : Sim.IPartTypeIgc
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataLauncherTypeIGC)</c>.</summary>
    public const int Size = 24;

    /// <summary>
    /// Decode a 24-byte <c>OT_partType</c> launcher payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataLauncherTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataLauncherTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        return new DataLauncherTypeIGC(
            Amount: BinaryPrimitives.ReadInt16LittleEndian(b[0..]),
            PartId: BinaryPrimitives.ReadInt16LittleEndian(b[2..]),
            SuccessorPartId: BinaryPrimitives.ReadInt16LittleEndian(b[4..]),
            LaunchCount: BinaryPrimitives.ReadInt16LittleEndian(b[6..]),
            ExpendabletypeId: BinaryPrimitives.ReadInt16LittleEndian(b[8..]),
            InventoryLineMdl: Fix(b, 10, 13));
    }
}
