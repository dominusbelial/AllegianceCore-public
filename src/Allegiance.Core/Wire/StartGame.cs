namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_START_GAME</c> (id 157, <c>Messages.h:868-869</c>) — the mission
/// owner's <b>manual</b> trigger to begin the start countdown, a genuine
/// alternative to the ready-up auto-start (P180). Native shape:
/// <code>
///   DEFINE_FEDMSG(C, START_GAME, 157)
///   END_FEDMSG   // zero fields -- a pure trigger, no payload
/// </code>
/// Header-only marker, the same as <see cref="EndSpoofing"/>/<see cref="Suicide"/>
/// (the zero-payload-message precedent in this codebase). The server's
/// <c>FM_C_START_GAME</c> handler (FedSrv.CPP:7597-7621) gates it on the
/// sender being the mission owner and the mission being <c>NotStarted</c> with
/// all sides ready, then starts the countdown.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 157
///   = 4 B fixed, no fields → cbmsg = 4
/// </code>
public sealed class StartGame : IFedMessage
{
    public const ushort Fmid = 157;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:868-869)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"START_GAME internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static StartGame Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"START_GAME needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected START_GAME fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new StartGame();
    }
}
