namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_TREASURE_ACK</c> (id 163, <c>src/Inc/Messages.h:877-879</c>), the
/// client's acknowledgement of an offered part-treasure pickup, quoted in
/// full:
/// <code>
///   DEFINE_FEDMSG(C, TREASURE_ACK, 163)
///     Mount             mountID;      //== c_mountNA
///   END_FEDMSG
/// </code>
/// <para>The <c>//== c_mountNA</c> inline comment is <b>stale/misleading</b>
/// and is <em>not</em> ground truth. The actual native handler
/// (<c>FedSrv/FedSrv.CPP:6094-6152</c>, <c>case FM_C_TREASURE_ACK</c>)
/// explicitly branches on <c>pfmTA-&gt;mountID != c_mountNA</c>:</para>
/// <list type="bullet">
/// <item><b>A real client-supplied mount</b> when accepting — the part is
/// merged into an existing same-type part at that slot or mounted fresh, and
/// an <c>ADD_PART</c>(164) is broadcast sector-wide.</item>
/// <item><b>The <c>c_mountNA</c> sentinel</b> (<see cref="MountNA"/>) when
/// declining — the part is re-created as a treasure at the ship's current
/// position.</item>
/// </list>
/// So <see cref="MountId"/> is a real variable field, decoded/encoded like any
/// other mount (the same plain settable-field pattern <c>SwapPart</c>/
/// <c>DropPart</c> establish); <see cref="MountNA"/> is retained only as the
/// named decline-sentinel constant. P123 originally built this codec believing
/// the header comment (client "not acknowledging a specific cargo slot");
/// P145 corrects that reading against the handler's own logic.</summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 163
///   offset 4: char   mountID   (Mount = char, igc.h:841; c_mountNA = −6 on decline)
///   offset 5: 1 pad byte       (round the struct to its largest-member
///                               alignment 2 → cbmsg = 6)
///   = 6 B total
/// </code>
public sealed class TreasureAck : IFedMessage
{
    public const ushort Fmid = 163;

    /// <summary><c>c_mountNA</c> (−c_maxCargo−1 = −6, igc.h:845-846) — the
    /// sentinel the client sends in <see cref="MountId"/> when it <b>declines</b>
    /// the offered part (the native handler's decline branch,
    /// <c>FedSrv.CPP:6125-6130</c>). Any other value is a real mount choice.</summary>
    public const byte MountNA = unchecked((byte)(-6));

    /// <summary>Wire width: header(4) + Mount char(1) + 1 pad to the 2-align = 6.</summary>
    public const int FixedSize = 6;

    /// <summary>The <c>Mount</c> slot the client chose — a real mount when
    /// accepting the offered part, or <see cref="MountNA"/> when declining.
    /// (Plain settable field, like every other mount in this port.)</summary>
    public byte MountId;

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
