namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_LOGOFF</c> (id 3, <c>Messages.h</c>) — "sent when the client
/// wants to logoff, mirrored back to client." A <c>CS</c> (client-or-server)
/// direction marker and deliberately empty: the struct is header-only
/// (<c>FMD_CS_LOGOFF</c> = <c>{ ushort cbmsg; ushort fmid; }</c>), so there
/// are no fields to encode or decode and <c>cbmsg = sizeof(FMD_CS_LOGOFF) = 4</c>
/// — the same zero-field shape as <c>FM_C_SUICIDE</c>(80), differing only in
/// its direction marker. The server's handling is <c>FedSrv.CPP:5787-5802</c>:
/// echo the exact same (empty) message back to the same connection
/// (<c>ForwardMessage(..., FM_GUARANTEED)</c>), bump the local logoff counter,
/// then clean up the player. Because the shape is header-only, the identical
/// codec serves both the client's request and the server's mirrored echo.
/// </summary>
public sealed class Logoff : IFedMessage
{
    public const ushort Fmid = 3;
    public const int FixedSize = 4;   // header only — no fields (Messages.h)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"LOGOFF internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Logoff Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOGOFF needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LOGOFF fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new Logoff();
    }
}
