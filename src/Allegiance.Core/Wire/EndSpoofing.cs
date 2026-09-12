namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_END_SPOOFING</c> (id 111, <c>Messages.h:744-746</c>) — a
/// deliberately payload-free marker the server sends after a chaff deployment to
/// close the spoof-resolution window opened by the preceding
/// <see cref="CreateChaff"/>/<see cref="MissileSpoofed"/> messages. Native shape:
/// <code>
///   DEFINE_FEDMSG(S, END_SPOOFING, 111)
///   END_FEDMSG
/// </code>
/// Its only real effect (confirmed via the client's receive handler,
/// <c>clintlib/appmsg.cpp:1686-1690</c>) is telling recipients to forget which
/// chaff object was "last created" for spoof-resolution purposes. In this port's
/// server-authoritative model there is no client-side "last created" pointer to
/// clear, so <c>EndSpoofing</c>'s only job here is being a real, correctly-timed
/// broadcast — the server's <c>HandleFireExpendable</c> sends exactly one of
/// these (regardless of whether anything was spoofed) as the final message of
/// the chaff-deployment flow. Header-only shape, the same as
/// <see cref="Suicide"/>/<see cref="Logoff"/>; the zero-payload-message
/// precedent in this codebase.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 111
///   = 4 B fixed, no fields → cbmsg = 4
/// </code>
public sealed class EndSpoofing : IFedMessage
{
    public const ushort Fmid = 111;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:744-746)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"END_SPOOFING internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static EndSpoofing Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"END_SPOOFING needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected END_SPOOFING fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new EndSpoofing();
    }
}
