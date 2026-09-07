using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MISSILE_DESTROYED</c> (id 83, <c>Messages.h:597-601</c>) — the
/// server's "this missile was destroyed" notice, carrying where it happened
/// (<c>position</c>), which sector (<c>clusterID</c>) and which missile
/// (<c>missileID</c>). Same simple fixed-size shape as <see cref="KillShip"/>:
/// no var items, a flat field list, fixed total size.
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 83
///   offset  4: Vector position     (12 bytes, 4-aligned)
///   offset 16: short  clusterID   (SectorID  = ObjectID = short, igc.h:477,487)
///   offset 18: short  missileID   (MissileID = ObjectID = short, igc.h:477,493)
///   = 20 bytes total
/// </code>
/// No alignment gaps: the 12-byte <c>Vector</c> occupies offsets 4-15
/// (4-aligned throughout) and the two <c>short</c>s sit back-to-back at 16-19.
/// </summary>
public sealed class MissileDestroyed : IFedMessage
{
    public const ushort Fmid = 83;
    public const int FixedSize = 20;

    public Vector3 Position;
    public short ClusterId;     // SectorID
    public short MissileId;     // MissileID

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        WriteVec(ref w, Position);
        w.WriteI16(ClusterId);
        w.WriteI16(MissileId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MISSILE_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MissileDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MISSILE_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSILE_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new MissileDestroyed
        {
            Position = ReadVec(ref r),
            ClusterId = r.ReadI16(),
            MissileId = r.ReadI16(),
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
