namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_PING</c> (id 35, <c>Messages.h:280-284</c>) — a bidirectional
/// keepalive/latency-probe echo. The client stamps its own time in
/// <c>timeClient</c> and sends; the server's handler (FedSrv.CPP:4897-4911)
/// stamps <c>timeServer</c> with the current server time and forwards the
/// whole message back to <b>the same connection it came from</b> (not
/// broadcast). Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, PING, 35) // just send it back to client as soon as you get it
///     Time timeClient;       // client sets this, and server just leaves it
///     Time timeServer;       // Server sets this
///     FMGuaranteed fmg;      // how the client wants the server to send back the echo
///   END_FEDMSG
/// </code>
/// Layout (natural alignment — no pragma pack; <c>Time</c> is a uint32 ms
/// accumulator and <c>FMGuaranteed</c> is a plain 4-byte enum,
/// messagecore.h:299-303):
/// <code>
///   offset 0:  ushort cbmsg
///   offset 2:  ushort fmid = 35
///   offset 4:  uint32 timeClient
///   offset 8:  uint32 timeServer
///   offset 12: int32  fmg
///   = 16 bytes fixed
/// </code>
/// <c>Fmg</c> is decode-round-tripped for fidelity (the server echoes it
/// back unchanged — the client requested the guarantee mode of the echo) but
/// has no transport effect in this port's synchronous echo path.
/// </summary>
public sealed class Ping : IFedMessage
{
    public const ushort Fmid = 35;
    public const int FixedSize = 16;   // header(4) + 3 × 4-byte fields

    /// <summary>The client's own timestamp (raw native ms <c>Time</c>), echoed
    /// back unchanged.</summary>
    public uint TimeClient;

    /// <summary>The server's timestamp when it processed the ping (raw native
    /// ms <c>Time</c>) — stamped by the server on the echo.</summary>
    public uint TimeServer;

    /// <summary><c>FMGuaranteed</c> enum value (<c>FM_GUARANTEED</c>=0,
    /// <c>FM_NOT_GUARANTEED</c>=1) — how the client wants the server to send
    /// the echo back. Round-tripped unchanged.</summary>
    public int Fmg;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);               // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeClient);      // @4
        w.WriteU32(TimeServer);      // @8
        w.WriteI32(Fmg);             // @12
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PING internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Ping Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PING needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PING fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        uint tc = r.ReadU32();       // @4
        uint ts = r.ReadU32();       // @8
        int fmg = r.ReadI32();       // @12
        return new Ping { TimeClient = tc, TimeServer = ts, Fmg = fmg };
    }
}
