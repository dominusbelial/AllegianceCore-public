namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_RIPCORD_DENIED</c> (id 186, <c>Messages.h:962-963</c>:
/// <c>DEFINE_FEDMSG(S, RIPCORD_DENIED, 186) END_FEDMSG</c>) — the server's
/// refusal of a ripcord request that named a target cluster with no valid
/// anchor. Sent to the <b>requester alone</b> (<c>RequestRipcord</c>,
/// <c>FedSrv.CPP:13371-13376</c>), never broadcast. The struct has <b>no
/// fields at all</b> — the message is exactly the 4-byte header
/// (<c>cbmsg</c>=4, <c>fmid</c>=186).
/// </summary>
public sealed class RipcordDenied : IFedMessage
{
    public const ushort Fmid = 186;
    public const int FixedSize = 4;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    public static RipcordDenied Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RIPCORD_DENIED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RIPCORD_DENIED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new RipcordDenied();
    }
}
