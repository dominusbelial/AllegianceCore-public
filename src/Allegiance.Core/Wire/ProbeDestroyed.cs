namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PROBE_DESTROYED</c> (id 85), the server's \"this probe blew up\"
/// message (<c>Messages.h:608-611</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, PROBE_DESTROYED, 85)
///       SectorID            clusterID;
///       ProbeID             probeID;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 85
///   offset 4: short  clusterID   (SectorID, igc.h:487)
///   offset 6: short  probeID     (ProbeID,  igc.h:496)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Simple fixed-size message, no var-item — same shape as <see cref="MineDestroyed"/>.
/// <b>Wire only.</b> Probe destruction is the <em>direct-fire damage</em>
/// path, not mine-style proximity — a probe carries <c>c_mtDamagable</c>
/// (<c>src/Igc/probeigc.cpp:115</c>) precisely because <c>ResolveCombatCollisions</c>
/// must eventually treat it as a destructible target, not explode-on-contact.
/// That damage/removal modelling is a later task; this codec only defines the
/// message shape.
/// </summary>
public sealed class ProbeDestroyed : IFedMessage
{
    public const ushort Fmid = 85;

    /// <summary>Wire width of the whole message: header(4) + clusterID(2) + probeID(2). No
    /// alignment gaps — both 2-byte IDs sit back-to-back, no member wider than 2.</summary>
    public const int FixedSize = 8;

    public short ClusterId;    // the sector the probe was in
    public short ProbeId;      // the destroyed probe

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(ProbeId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PROBE_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ProbeDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROBE_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROBE_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new ProbeDestroyed
        {
            ClusterId = r.ReadI16(),
            ProbeId = r.ReadI16(),
        };
    }
}
