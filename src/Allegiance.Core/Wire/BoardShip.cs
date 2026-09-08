namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_BOARD_SHIP</c> (id 103, <c>Messages.h:686-688</c>:
/// <c>DEFINE_FEDMSG(C, BOARD_SHIP, 103) ShipID sidParent; END_FEDMSG</c>) —
/// the client's dual-purpose board/leave request, ported from
/// <c>FedSrv.CPP:6385-6418</c> <c>case FM_C_BOARD_SHIP</c>:
/// <list type="bullet">
/// <item><b><c>sidParent == NA</c> means LEAVE</b> whatever this player is
/// currently boarded on (the wire value <c>NA</c> is -1);</item>
/// <item>any other <c>sidParent</c> means BOARD this ship as a turret gunner
/// or observer.</item>
/// </list>
/// Only failure (<c>LeaveShip</c>/<c>BoardShip</c> returning false, or the
/// named parent ship not found / a ghost) elicits a <c>BOARD_NACK</c> — on
/// success the native flushes only the already-queued <c>SHIP_STATUS</c>
/// updates, with <b>no</b> dedicated success acknowledgement message
/// (confirmed by reading the whole handler, <c>FedSrv.CPP:6385-6418</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 103
///   offset 4: short  sidParent  (ShipID = ObjectID = short, igc.h:477,480)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class BoardShip : IFedMessage
{
    public const ushort Fmid = 103;
    public const int FixedSize = 6;   // header + sidParent short — no padding (short is 2-aligned)

    /// <summary>The ship to board, or <c>NA</c> (-1) to leave the current one.</summary>
    public short SidParent;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidParent);      // sidParent (ShipID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BOARD_SHIP internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BoardShip Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BOARD_SHIP needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BOARD_SHIP fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new BoardShip { SidParent = r.ReadI16() };
    }
}
