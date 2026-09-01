namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SET_CLUSTER</c> (id 7), the server's "here is your ship" message —
/// sent to a client when it launches/undocks (<c>Messages.h:83-87</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 7
///   offset 4: short   sectorID
///   offset 6: ServerSingleShipUpdate shipupdate  (47 B, NOT 4-aligned)
///   offset53: byte    cookie
///   = 54 B fixed; no var items → cbmsg = 54
/// </code>
/// The 2-byte alignment pad this codec originally assumed between
/// <c>sectorID</c> and <c>shipupdate</c> does not exist on the real wire —
/// the native <c>FEDMESSAGE</c> structs are packed, not host-compiler-aligned
/// (P36, 2026-08-31): a live capture showed the client decoding the ship's
/// position from 2 bytes early, landing on garbage
/// (<c>(0, -2.4e-41, +2.4e-41)</c> instead of the real spawn position),
/// which is the reason the ship's client-side position never left the
/// origin through the whole P30→P35 chain — a wire-format bug, not (as
/// assumed until P36) a native-client position-application bug. Same class
/// of mistake as <see cref="ShipUpdate"/>'s original 52-vs-48-byte bug
/// (P33) — this codebase's structs are consistently packed with no
/// alignment padding; assume that by default for any new wire type instead
/// of re-deriving it per message.
/// </summary>
public sealed class SetCluster : IFedMessage
{
    public const ushort Fmid = 7;
    public const int FixedSize = 54;

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
        Ship.EncodeBody(ref w);   // starts immediately, no alignment pad
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
        var ship = ServerShipUpdate.DecodeBody(ref r);       // starts immediately, no alignment pad
        byte cookie = r.ReadByte();

        return new SetCluster { SectorId = sectorId, Ship = ship, Cookie = cookie };
    }
}
