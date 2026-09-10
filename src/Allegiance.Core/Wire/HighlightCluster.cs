namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_HIGHLIGHT_CLUSTER</c> (id 199, <c>Messages.h:1033-1036</c>,
/// quoted in full): a commander marking a sector on the map for their own
/// teammates (Xynth #208 — "Notify clients of sector highlight"):
/// <code>
///   DEFINE_FEDMSG(CS, HIGHLIGHT_CLUSTER, 199)
///     SectorID            clusterID;
///     bool                highlight;
///   END_FEDMSG
/// </code>
/// A pure relay at the server: the native handler
/// (<c>FedSrv.CPP:7369-7375</c>, the entire case) decodes nothing beyond
/// casting the message and forwards the exact same bytes to the sender's own
/// side group —
/// <code>
///   g.fm.ForwardMessage(CFSSide::FromIGC(pfsPlayer->GetSide())->GetGroup(), pfmHC, FM_GUARANTEED);
/// </code>
/// No validation, no cluster-existence check, no sender-side check, no
/// <c>STAGE_STARTED</c> gate — genuinely no more logic than the relay itself,
/// so this port adds none.
/// <para>Layout (MSVC default packing, FEDMSG.md §2; <c>SectorID</c> is a
/// <c>short</c>, igc.h — cf. <see cref="SetCluster.SectorId"/>;
/// <c>bool</c> is one C++ byte):</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 199
///   offset 4: short  clusterID    (SectorID, 2 bytes)
///   offset 6: bool   highlight    (1 byte, C++ bool)
///   = 7 B fixed, no var items → cbmsg = 7
/// </code>
/// Identical shape to the sibling <c>TELEPORT_ACK(102)</c>
/// (<see cref="TeleportAck"/>, <c>TeleportAck.cs:9-14</c>) which also carries
/// a <c>short</c> + trailing single-byte <c>bool</c> — no padding, 7 bytes.
/// </summary>
public sealed class HighlightCluster : IFedMessage
{
    public const ushort Fmid = 199;
    public const int FixedSize = 7;   // header + clusterID short + highlight bool — no padding (short is 2-aligned)

    /// <summary>The highlighted sector (<c>SectorID</c> short, igc.h).</summary>
    public short ClusterId;

    /// <summary>True highlights the sector, false clears the highlight.</summary>
    public bool Highlight;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                                    // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);                            // offset 4: clusterID
        w.WriteByte(Highlight ? (byte)1 : (byte)0);       // offset 6: highlight (C++ bool)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"HIGHLIGHT_CLUSTER internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static HighlightCluster Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"HIGHLIGHT_CLUSTER needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected HIGHLIGHT_CLUSTER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                     // header
        return new HighlightCluster
        {
            ClusterId = r.ReadI16(),
            Highlight = r.ReadByte() != 0,
        };
    }
}
