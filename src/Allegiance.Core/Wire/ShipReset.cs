namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SHIP_RESET</c> (id 183), the server's reply when it rejects a
/// client-claimed ship pose (<c>Messages.h:944-949</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 183
///   offset 4: ServerSingleShipUpdate shipupdate  (47 B, NOT 4-aligned)
///   offset51: byte    cookie                     (the NEW cookie to echo)
///   = 52 B fixed; no var items → cbmsg = 52
/// </code>
/// Native builds it in <c>CFSPlayer::SetShipUpdate</c> on
/// <c>c_susRejected</c> (<c>FedSrv/fsship.cpp:1192-1200</c>): it exports the
/// server's own authoritative pose, stamps a fresh <c>NewCookie()</c>, and sends
/// it GUARANTEED+FLUSH. The client must apply the pose and echo the new cookie;
/// updates are ignored until then (<c>FedSrv.CPP:4851-4864</c>). The codec is
/// packed with no alignment pad between the 47-byte ship update and the cookie —
/// the same no-padding invariant this codebase established for <see cref="SetCluster"/>
/// (P36) and <see cref="ShipUpdate"/> (P33).
/// </summary>
public sealed class ShipReset : IFedMessage
{
    public const ushort Fmid = 183;
    public const int FixedSize = 4 + ServerShipUpdate.Width + 1;   // 52

    public ServerShipUpdate Ship = null!;
    public byte Cookie;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        Ship.EncodeBody(ref w);   // starts immediately, no alignment pad
        w.WriteByte(Cookie);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SHIP_RESET internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ShipReset Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SHIP_RESET needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SHIP_RESET fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                       // header
        var ship = ServerShipUpdate.DecodeBody(ref r);      // starts immediately, no alignment pad
        byte cookie = r.ReadByte();

        return new ShipReset { Ship = ship, Cookie = cookie };
    }
}
