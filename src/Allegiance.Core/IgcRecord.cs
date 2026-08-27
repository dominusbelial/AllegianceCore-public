namespace Allegiance.Core;

/// <summary>
/// One record in an <c>.igc</c> stream. The 6-byte header is
/// <c>int16 type</c> + <c>int32 size</c>, where <paramref name="Size"/> excludes
/// the header itself. Written by <c>ExportList</c>
/// (<c>src/Igc/missionigc.cpp:3187</c>), read by <c>CmissionIGC::Import</c>
/// (<c>:3269</c>). Records are contiguous with no inter-record padding.
/// </summary>
/// <param name="Type">The <see cref="IgcObjectType"/> discriminator.</param>
/// <param name="Size">Payload length in bytes, excluding the 6-byte header.</param>
/// <param name="PayloadOffset">Offset of the payload within the unwrapped body.</param>
public readonly record struct IgcRecord(IgcObjectType Type, int Size, int PayloadOffset)
{
    public const int HeaderSize = 6;
}
