namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_RIPCORD_ABORTED</c> (id 187, <c>Messages.h:965-967</c>:
/// <c>DEFINE_FEDMSG(S, RIPCORD_ABORTED, 187) ShipID shipidRipcord; END_FEDMSG</c>)
/// — the server's notice that a ripcord countdown has been cancelled. Emitted
/// in exactly two places in native: a cancel request (<c>sidRipcord == NA</c>)
/// while active (<c>RequestRipcord</c>, <c>FedSrv.CPP:13327-13339</c>) and a
/// mid-countdown legality failure (<c>ContinueRipcord</c>,
/// <c>FedSrv.CPP:13427-13434</c>). Both broadcast sector-wide to everyone
/// flying in the ship's current sector, naming the ship whose ripcord was
/// aborted (<c>GetGroupSectorFlying(pclusterShip)</c>). This task ports the
/// cancel-request path only; the per-tick revalidation lives with the
/// countdown/teleport driver (see P111 report).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 187
///   offset 4: short  shipidRipcord  (ShipID = ObjectID = short, igc.h:477,480)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class RipcordAborted : IFedMessage
{
    public const ushort Fmid = 187;
    public const int FixedSize = 6;   // header + shipidRipcord short — no padding (short is 2-aligned)

    /// <summary>The ship whose ripcord was aborted.</summary>
    public short ShipidRipcord;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipidRipcord);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"RIPCORD_ABORTED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static RipcordAborted Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RIPCORD_ABORTED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RIPCORD_ABORTED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new RipcordAborted { ShipidRipcord = r.ReadI16() };
    }
}
