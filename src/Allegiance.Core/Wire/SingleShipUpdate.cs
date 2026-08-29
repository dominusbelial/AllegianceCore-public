namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SINGLE_SHIP_UPDATE</c> (id 78), the server's per-ship state
/// broadcast when a ship's state changes — the message the client watches to
/// learn where its ship is (<c>Messages.h:583-588</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 78
///   offset 4: short   otTarget    (ObjectType = short, igc.h:115)
///   offset 6: short   oidTarget   (ObjectID = short, igc.h:477)
///   offset 8: ServerSingleShipUpdate shipupdate  (47 B)
///   offset55: bool    bIsRipcording
///   = 56 B fixed, 4-aligned; no var items → cbmsg = 56
/// </summary>
public sealed class SingleShipUpdate : IFedMessage
{
    public const ushort Fmid = 78;
    public const int FixedSize = 56;

    public short OtTarget;          // ObjectType of the target (0 = OT_ship)
    public short OidTarget;         // the ship id
    public ServerShipUpdate Ship = null!;
    public bool BIsRipcording;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(OtTarget);
        w.WriteI16(OidTarget);
        Ship.EncodeBody(ref w);
        w.WriteByte(BIsRipcording ? (byte)1 : (byte)0);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SINGLE_SHIP_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SingleShipUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SINGLE_SHIP_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SINGLE_SHIP_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                     // header
        short ot = r.ReadI16();
        short oid = r.ReadI16();
        var ship = ServerShipUpdate.DecodeBody(ref r);
        bool rip = r.ReadByte() != 0;

        return new SingleShipUpdate { OtTarget = ot, OidTarget = oid, Ship = ship, BIsRipcording = rip };
    }
}
