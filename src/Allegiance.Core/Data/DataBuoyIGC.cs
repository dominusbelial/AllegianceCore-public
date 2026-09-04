using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>
/// <c>DataBuoyIGC</c> — the payload behind the <c>db</c> member of
/// <c>FM_CS_CHATBUOY</c>(108) (<c>src/Inc/Messages.h:716-722</c>). Found in the
/// native source at <c>src/Igc/igc.h:1913-1922</c>:
/// <code>
///   struct DataBuoyIGC
///   {
///       Vector   position;    // offset 0  — float x,y,z (src/zlib/vector.h:14-20)
///       SectorID clusterID;   // offset 12 — ObjectID = short (agcidl.idl:229, igc.h:487)
///       BuoyType type;        // offset 14 — char (igc.h:552)
///       BuoyID   buoyID;      // offset 16 — ObjectID = short (igc.h:481); 1 pad byte at 15
///       bool     visible;     // offset 18 — 1 byte; 1 pad byte at 19 → struct aligns to 4
///   };
/// </code>
/// Under MSVC default packing (FEDMSG.md §2) the struct is 20 bytes: 12
/// (Vector) + 2 (clusterID) + 1 (type) + 1 pad + 2 (buoyID) + 1 (visible) + 1
/// pad, aligned to its largest member (float, 4). Unlike ships/asteroids/
/// stations/warps, buoys have <b>no separate server-side object lifecycle</b>:
/// they are pure client-side ephemeral target markers, constructed via
/// <c>ClientConnection.CreateBuoy(...)</c> (<c>WinTrek/console.cpp</c>,
/// <c>Wopr/.../ClientConnection.h</c>) and carried inside this chat message as
/// its own export payload (<c>clintlib.cpp:2460-2469</c> writes it via
/// <c>pmodelTarget-&gt;Export(&amp;(pfmChatBuoy-&gt;db))</c> —
/// <c>buoyigc.cpp:87-98</c> exports position/clusterID/type/buoyID/visible in
/// that order). This server never exports a buoy as a synced object; it only
/// sees the <c>db</c> block relayed verbatim inside CHATBUOY.
/// </summary>
public readonly record struct DataBuoyIGC(
    float[] Position,
    short ClusterId,
    byte Type,
    short BuoyId,
    bool Visible)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataBuoyIGC)</c>.</summary>
    public const int Size = 20;

    /// <summary>Offset of the <c>buoyID</c> field within the block.</summary>
    public const int BuoyIdOffset = 16;

    public static DataBuoyIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
            throw new ArgumentException($"DataBuoyIGC needs >= {Size} bytes, got {b.Length}");
        return new DataBuoyIGC(
            Position:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[0..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[8..]),
            ],
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[12..]),
            Type: b[14],
            BuoyId: BinaryPrimitives.ReadInt16LittleEndian(b[16..]),
            Visible: b[18] != 0);
    }

    /// <summary>Serialize the 20-byte block (the inverse of <see cref="Decode"/>),
    /// with the two pad bytes (offsets 15 and 19) written as zero.</summary>
    public byte[] Encode()
    {
        var b = new byte[Size];
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4 * i), Position is { Length: 3 } p ? p[i] : 0f);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), ClusterId);
        b[14] = Type;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(16), BuoyId);
        b[18] = Visible ? (byte)1 : (byte)0;
        return b;
    }
}
