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
///   offset 9: ClientShipUpdate shipupdate  (39 B, NOT 4-aligned)
///   = 48; no var items → cbmsg = 48
/// </code>
/// The 3-byte-pad-then-4-align layout this codec originally assumed (52
/// bytes total) does not match the real client: a live capture from a real
/// direct-connected native client (P33, 2026-08-31) showed cbmsg=48 with
/// zero padding — <c>ClientShipUpdate</c> starts immediately at offset 9,
/// confirmed by hand-decoding the 39-byte body (its own leading <c>time</c>
/// field byte-for-byte matches the outer <c>timeUpdate</c> at offset 4, as
/// expected since both derive from the same <c>GetMyLastUpdate()</c> source,
/// clintlib.cpp:1861-1863).
/// The server reads only <c>StateM</c> + <c>Controls</c>; the rest of the
/// payload is the client's prediction echo and is ignored as authority.
/// </summary>
public sealed class ShipUpdate : IFedMessage
{
    public const ushort Fmid = 24;
    public const int FixedSize = 48;

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
        Payload.EncodeBody(ref w);            // starts immediately, no alignment pad
        w.WriteU16At(0, (ushort)FixedSize);   // cbmsg = sizeof(FMD_C_SHIP_UPDATE) = 48
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
        var payload = ClientShipUpdate.DecodeBody(ref r);  // starts immediately, no alignment pad

        return new ShipUpdate { Time = time, Cookie = cookie, Payload = payload };
    }
}
