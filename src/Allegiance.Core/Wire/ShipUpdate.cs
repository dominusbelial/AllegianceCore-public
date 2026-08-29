namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_SHIP_UPDATE</c> (id 24), the client's repeated control input,
/// sent roughly every <c>c_dsecUpdateClient = 0.15s</c>
/// (<c>Messages.h:225-231</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 24
///   offset 4: uint32 timeUpdate
///   offset 8: byte    cookie
///   offset 9: 3 pad bytes (align ClientShipUpdate to 4)
///   offset12: ClientShipUpdate shipupdate  (39 B)
///   = 51 → rounded to 52 (4-aligned); no var items → cbmsg = 52
/// </code>
/// The server reads only <c>StateM</c> + <c>Controls</c>; the rest of the
/// payload is the client's prediction echo and is ignored as authority.
/// </summary>
public sealed class ShipUpdate : IFedMessage
{
    public const ushort Fmid = 24;
    public const int FixedSize = 52;

    public uint Time;
    public byte Cookie;
    public ClientShipUpdate Payload = null!;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(Time);
        w.WriteByte(Cookie);
        w.Advance(3);             // pad: align ClientShipUpdate (contains float) to 4
        Payload.EncodeBody(ref w);
        w.Advance(FixedSize - w.Position);   // trailing pad to the struct's aligned size
        w.WriteU16At(0, (ushort)FixedSize);  // cbmsg = sizeof(FMD_C_SHIP_UPDATE) = 52
        return buf;
    }

    public static ShipUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SHIP_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SHIP_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                     // header
        uint time = r.ReadU32();
        byte cookie = r.ReadByte();
        r.Advance(3);                                     // pad
        var payload = ClientShipUpdate.DecodeBody(ref r);

        return new ShipUpdate { Time = time, Cookie = cookie, Payload = payload };
    }
}
