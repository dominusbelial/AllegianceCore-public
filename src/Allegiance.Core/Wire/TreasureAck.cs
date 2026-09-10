namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_TREASURE_ACK</c> (id 163), the client's acknowledgement that it
/// docked/handled an acquired treasure (<c>src/Inc/Messages.h:877-879</c>),
/// quoted in full:
/// <code>
///   DEFINE_FEDMSG(C, TREASURE_ACK, 163)
///     Mount             mountID;      //== c_mountNA
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 163
///   offset 4: char   mountID   (Mount = char, igc.h:841; == c_mountNA = −6)
///   offset 5: 1 pad byte       (round the struct to its largest-member
///                               alignment 2 → cbmsg = 6)
///   = 6 B total
/// </code>
/// The only field is a <c>Mount</c> that native always sends as
/// <c>c_mountNA</c> (−c_maxCargo−1 = −6, igc.h:845-846) — the client is not
/// acknowledging a specific cargo slot, just signalling \"got it\". This is a
/// client→server (C) message; P123 builds the codec (so the port can recognize
/// the ack) but the server has no consumer to wire it to yet — part-treasure
/// docking handling is the deferred <c>c_tcPart</c> work (see the P123 report).
/// </summary>
public sealed class TreasureAck : IFedMessage
{
    public const ushort Fmid = 163;

    /// <summary><c>c_mountNA</c> (−c_maxCargo−1 = −6, igc.h:845-846) — the
    /// value the native always sends in this field.</summary>
    public const byte MountNA = unchecked((byte)(-6));

    /// <summary>Wire width: header(4) + Mount char(1) + 1 pad to the 2-align = 6.</summary>
    public const int FixedSize = 6;

    /// <summary>The <c>Mount</c> slot — native always <c>c_mountNA</c> (−6).</summary>
    public byte MountId = MountNA;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte(MountId);       // offset 4
        w.WriteByte(0);             // pad @5
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"TREASURE_ACK internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static TreasureAck Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TREASURE_ACK needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TREASURE_ACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        return new TreasureAck { MountId = buf[4] };
    }
}
