namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PINGDATA</c> (id 194, <c>Messages.h:1006-1010</c>), a
/// <b>server-only</b> (<c>S</c>) per-player latency-roster entry — the port of
/// native's w0dk4 player-pings feature (handler <c>FedSrv.CPP:7743-7808</c>,
/// <c>case FM_C_REQPINGDATA</c>). One PINGDATA is sent <b>to the requester
/// alone</b> for every connection on the whole server that has a live player,
/// then a terminator/"open window" marker reuses this same wire shape with the
/// sentinel <c>shipID = -1</c>.
/// <code>
/// DEFINE_FEDMSG(S, PINGDATA, 194)
///   ShipID          shipID;
///   unsigned short  ping;
///   unsigned short  loss;
/// END_FEDMSG
/// </code>
/// Layout (natural alignment): header(4) + <c>shipID</c>(short, 2, offset 4) +
/// <c>ping</c>(ushort, 2, offset 6) + <c>loss</c>(ushort, 2, offset 8) =
/// <b>10 bytes fixed</b>.
/// <para><b>Value sourcing</b>: native derives <c>ping</c> from DirectPlay's
/// <c>DPN_CONNECTION_INFO.dwRoundTripLatencyMS</c> and <c>loss</c> from its
/// packet-retry/drop counters — transport statistics this port's plain
/// <c>System.Net.Sockets</c> TCP transport exposes no managed access to. Per
/// this port's established precedent for that exact shape of gap (see
/// <c>docs/PINGDATA_REQPINGDATA_P200.md</c>), entries carry <c>ping = 0,
/// loss = 0</c>: wire-complete and decode-correct, deliberately not a measured
/// value.</para></summary>
public sealed class PingData : IFedMessage
{
    public const ushort Fmid = 194;
    public const int FixedSize = 10;   // header 4 + shipID 2 + ping 2 + loss 2

    /// <summary>The ship whose latency this entry reports (<c>ShipID</c> =
    /// short). The terminator marker uses the sentinel <c>-1</c>.</summary>
    public short ShipId;

    /// <summary>Round-trip latency in milliseconds (<c>ping</c> = ushort).
    /// Carries the sentinel <c>1</c> on the terminator/unchanged-window marker.</summary>
    public ushort Ping;

    /// <summary>Packet-loss percentage (<c>loss</c> = ushort). Always 0 in
    /// this port (no transport-level loss counter — see the class doc).</summary>
    public ushort Loss;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);                  // offset 4
        w.WriteU16(Ping);                    // offset 6
        w.WriteU16(Loss);                    // offset 8
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PINGDATA internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PingData Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PINGDATA needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PINGDATA fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short shipId = r.ReadI16();      // offset 4
        ushort ping = r.ReadU16();       // offset 6
        ushort loss = r.ReadU16();       // offset 8
        return new PingData { ShipId = shipId, Ping = ping, Loss = loss };
    }
}
