using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_VIEW_CLUSTER</c> (id 177), the server's answer to a docked
/// player's <c>FM_C_VIEW_CLUSTER(104)</c> — the acknowledged preview of the
/// requested cluster (the map/spectator handshake). Quoted native shape
/// (Messages.h, answered in the task's <c>FedSrv.CPP:6420-6454</c> handler):
/// <code>
///   DEFINE_FEDMSG(S, VIEW_CLUSTER, 177)
///     Vector   position;      // the centered object's real position (or zero when bUsePosition=false)
///     SectorID clusterID;     // the cluster being previewed
///     Bool     bUsePosition;  // true when position is meaningful (target resolved+visible)
///   END_FEDMSG
/// </code>
/// Packed layout, Vector at 4-15 (4-aligned, same as <see cref="MissileDestroyed"/>),
/// then the sector short, then the 1-byte bool (<see cref="KillShip"/> proves
/// this codebase treats native Bool as 1 byte, live-verified):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 177
///   offset  4: Vector position  (12 bytes, 4-aligned)
///   offset 16: short  clusterID (SectorID = short)
///   offset 18: bool   bUsePosition (1 byte)
///   = 19 B fixed, no var items → cbmsg = 19
/// </code>
/// Sent <b>only</b> to the requesting player (<c>pfsPlayer-&gt;GetConnection()</c>,
/// not a sector/mission broadcast), whenever the request named a real (non-NA)
/// cluster. <c>BUsePosition=false</c> unless the named target resolved to a real
/// object in that cluster visible to the requester's side, in which case the
/// handler echoes the object's real position with <c>BUsePosition=true</c>.
/// </summary>
public sealed class ViewClusterReply : IFedMessage
{
    public const ushort Fmid = 177;
    public const int FixedSize = 19;

    public Vector3 Position;
    public short ClusterId;
    public bool BUsePosition;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        WriteVec(ref w, Position);
        w.WriteI16(ClusterId);
        w.WriteByte(BUsePosition ? (byte)1 : (byte)0);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"VIEW_CLUSTER_REPLY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ViewClusterReply Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"VIEW_CLUSTER_REPLY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected VIEW_CLUSTER_REPLY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new ViewClusterReply
        {
            Position = ReadVec(ref r),
            ClusterId = r.ReadI16(),
            BUsePosition = r.ReadByte() != 0,
        };
    }

    // Same established 12-byte three-float LE helper as every vector-bearing wire codec.
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
