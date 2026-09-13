namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_REQPINGDATA</c> (id 195, <c>Messages.h:1012-1013</c>), a
/// <b>client-initiated</b> (<c>C</c>) zero-field trigger: the client asks the
/// server for every connected player's latency roster (the w0dk4 player-pings
/// feature, handler <c>FedSrv.CPP:7743-7808</c>, <c>case
/// FM_C_REQPINGDATA</c>). The server answers with one <see cref="PingData"/>
/// (194) per live player, followed by a terminator marker — all sent only to
/// this requester — or, if the requester is inside the 5-second debounce
/// window, a single unchanged-window marker.
/// <code>
/// DEFINE_FEDMSG(C, REQPINGDATA, 195)
/// END_FEDMSG
/// </code>
/// Header-only marker (zero fields), the same as <see cref="RandomizeTeams"/>/
/// <see cref="StartGame"/>. Direction is <c>C</c> (client-initiated).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 195
///   = 4 B fixed, no fields → cbmsg = 4
/// </code>
public sealed class ReqPingData : IFedMessage
{
    public const ushort Fmid = 195;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:1012-1013)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"REQPINGDATA internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ReqPingData Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"REQPINGDATA needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected REQPINGDATA fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new ReqPingData();
    }
}
