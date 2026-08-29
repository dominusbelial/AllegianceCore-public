namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SET_CLUSTER</c> (id 7), the server's "here is your ship" message —
/// sent to a client when it launches/undocks (<c>Messages.h:83-87</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 7
///   offset 4: short   sectorID
///   offset 6: 2 pad bytes (align ServerSingleShipUpdate to 4)
///   offset 8: ServerSingleShipUpdate shipupdate  (47 B)
///   offset55: byte    cookie
///   = 56 B fixed, 4-aligned; no var items → cbmsg = 56
/// </code>
/// </summary>
public sealed class SetCluster : IFedMessage
{
    public const ushort Fmid = 7;
    public const int FixedSize = 56;

    public short SectorId;
    public ServerShipUpdate Ship = null!;
    public byte Cookie;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SectorId);
        w.Advance(2);             // pad: align ServerSingleShipUpdate (contains float) to 4
        Ship.EncodeBody(ref w);
        w.WriteByte(Cookie);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_CLUSTER internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetCluster Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_CLUSTER needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_CLUSTER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                       // header
        short sectorId = r.ReadI16();
        r.Advance(2);                                       // pad
        var ship = ServerShipUpdate.DecodeBody(ref r);
        byte cookie = r.ReadByte();

        return new SetCluster { SectorId = sectorId, Ship = ship, Cookie = cookie };
    }
}
