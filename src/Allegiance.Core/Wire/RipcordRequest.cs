namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_RIPCORD_REQUEST</c> (id 184, <c>Messages.h:951-953</c>:
/// <c>DEFINE_FEDMSG(C, RIPCORD_REQUEST, 184) SectorID sidRipcord; END_FEDMSG</c>)
/// — the client's request to start, re-target, or cancel a ripcord countdown.
/// <c>sidRipcord</c> is the target cluster id; the special value <c>NA</c>
/// (-1) means <b>cancel</b> whatever ripcord the ship currently has active.
/// The server's state machine is <c>RequestRipcord</c>
/// (<c>FedSrv.CPP:13319-13378</c>): a request naming a real target cluster
/// (re)activates the countdown toward a valid anchor there
/// (<c>RIPCORD_ACTIVATE</c>/<c>RIPCORD_DENIED</c>), and a request naming
/// <c>NA</c> aborts an active ripcord (<c>RIPCORD_ABORTED</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 184
///   offset 4: short  sidRipcord  (SectorID = ObjectID = short, igc.h:477,487)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class RipcordRequest : IFedMessage
{
    public const ushort Fmid = 184;
    public const int FixedSize = 6;   // header + sidRipcord short — no padding (short is 2-aligned)

    /// <summary>The special <c>SidRipcord</c> value meaning "cancel whatever
    /// ripcord I currently have active" (<c>NA</c> = -1, igc.h:477).</summary>
    public const short NA = -1;

    /// <summary>The target sector, or <c>NA</c> (-1) to cancel an active ripcord.</summary>
    public short SidRipcord;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidRipcord);     // sidRipcord (SectorID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"RIPCORD_REQUEST internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static RipcordRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RIPCORD_REQUEST needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RIPCORD_REQUEST fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new RipcordRequest { SidRipcord = r.ReadI16() };
    }
}
