namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MINE_DESTROYED</c> (id 84), the server's "this mine blew up"
/// message (<c>Messages.h:603-606</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, MINE_DESTROYED, 84)
///       SectorID            clusterID;
///       MineID              mineID;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 84
///   offset 4: short  clusterID   (SectorID, igc.h:487)
///   offset 6: short  mineID      (MineID,   igc.h:495)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Simple fixed-size message, no var-item — same shape as <see cref="KillShip"/>.
/// Emitted once per mine destruction by the server's
/// <c>BEGIN_PFM_CREATE(g.fm, pfmMineDestroyed, S, MINE_DESTROYED)</c> site
/// (<c>FedSrv.CPP:12533</c>) in the mine-removal path. Implements the
/// <c>mineID</c> part of the destroy broadcast only; proximity detonation,
/// mine damage and explosion modelling are sim-layer behaviour for a later
/// task in this arc, not part of this wire-only slice.
/// </summary>
public sealed class MineDestroyed : IFedMessage
{
    public const ushort Fmid = 84;

    /// <summary>Wire width of the whole message: header(4) + clusterID(2) + mineID(2). No
    /// alignment gaps — both 2-byte IDs sit back-to-back, no member wider than 2.</summary>
    public const int FixedSize = 8;

    public short ClusterId;    // the sector the mine was in
    public short MineId;       // the destroyed mine

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(MineId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MINE_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MineDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MINE_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MINE_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new MineDestroyed
        {
            ClusterId = r.ReadI16(),
            MineId = r.ReadI16(),
        };
    }
}
