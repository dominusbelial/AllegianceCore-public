namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_TELEPORT_ACK</c> (id 102, <c>Messages.h:681-684</c>), the server's
/// reply to the <b>non-</b><c>NA</c> half of <c>FM_C_DOCKED(48)</c> — the
/// teleport/relocation dock request that makes a displaced ship actually land
/// somewhere (<c>FedSrv.CPP:7054-7131</c>):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 102
///   offset  4: short  stationID    (StationID, igc.h:477,840)
///   offset  6: bool   bNewHull     (1 byte, C++ bool)
///   = 7 B fixed, no var items → cbmsg = 7
/// </code>
/// Semantics (matching native): <c>stationID = NA</c> means the request was
/// <b>rejected</b> (the named station was nonexistent, hostile, or not
/// restart-capable — the request is ignored, <c>FedSrv.CPP:7063-7073</c>).
/// Otherwise the server echoes the real <c>stationID</c> and sets
/// <c>bNewHull = true</c> when the player's current hull is <b>not</b>
/// sellable there, in which case they are force-converted to their side's
/// Lifepod hull (<c>FedSrv.CPP:7076,7079-7120</c>). Sent to the requesting
/// player's own connection only (<c>SendMessages(pfsPlayer-&gt;GetConnection(),
/// ...)</c>, <c>FedSrv.CPP:7071,7077</c>).
/// <para>The <c>StationID</c> short is 2-aligned right after the 4-byte
/// header; the trailing <c>bool</c> is a single byte (C++ <c>bool</c>), no
/// padding — total 7 bytes, matching how the sibling <c>KILL_SHIP(105)</c>
/// carries its odd-sized bool tail (<c>KillShip.cs:13-15</c>).</para>
/// </summary>
public sealed class TeleportAck : IFedMessage
{
    public const ushort Fmid = 102;
    public const int FixedSize = 7;

    /// <summary>The target <c>StationID</c>, or <c>NA</c> (<c>-1</c>) when the
    /// request was rejected.</summary>
    public short StationId;

    /// <summary>True when the player must be force-converted to their side's
    /// Lifepod hull (their current hull is not sellable at the target).</summary>
    public bool BNewHull;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                    // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(StationId);
        w.WriteByte(BNewHull ? (byte)1 : (byte)0);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"TELEPORT_ACK internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static TeleportAck Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TELEPORT_ACK needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TELEPORT_ACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                     // header
        return new TeleportAck
        {
            StationId = r.ReadI16(),
            BNewHull = r.ReadByte() != 0,
        };
    }
}
