namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_POSITIONREQ</c> (id 39, <c>Messages.h:320-323</c>) — the client's
/// request to be seated on a side (the client half of the <c>POSITIONREQ</c>
/// pair; the server's pending-request broadcast is the distinct
/// <see cref="PositionReqBroadcast"/> shape at id 40). Native shape:
/// <code>
///   DEFINE_FEDMSG(C, POSITIONREQ, 39) // client requests position on a side.
///     // Client can issue multiple of these
///     SideID  iSide;
///   END_FEDMSG
/// </code>
/// A single <c>SideID</c> short naming the side the player wants to join. The
/// server either auto-accepts it (seating them immediately when the side has
/// no leader) or broadcasts <c>POSITIONREQ(40)</c> for leader approval
/// (<c>FedSrv.CPP:6658-6707</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 39
///   offset 4: short  iSide        (SideID)
///   = 6 B fixed (2-aligned tail, same C++ sizeof as FireExpendableRequest)
/// </code>
/// </summary>
public sealed class PositionReq : IFedMessage
{
    public const ushort Fmid = 39;
    public const int FixedSize = 6;   // header + iSide short @4 (2-aligned tail)

    /// <summary>The side the player requests (<c>SideID</c> = short).</summary>
    public short SideId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4: iSide (SideID short)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 tail (offset 5)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"POSITIONREQ(39) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PositionReq Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"POSITIONREQ(39) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected POSITIONREQ fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short sideId = r.ReadI16();  // offset 4
        return new PositionReq { SideId = sideId };
    }
}
