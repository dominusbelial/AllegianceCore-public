namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_WARP_BOMB</c> (id 188), the server's "a warp/aleph has been bombed"
/// message (<c>Inc/Messages.h:969-973</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, WARP_BOMB, 188)
///       Time                timeExplosion;
///       WarpID              warpidBombed;
///       ExpendableTypeID    expendableidMissile;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 188
///   offset 4: uint   timeExplosion   (Time, this project's uint-Time wire convention)
///   offset 8: short  warpidBombed    (WarpID — ObjectID typedef, igc.h:488)
///   offset10: short  expendableidMissile  (ExpendableTypeID — ObjectID typedef, igc.h:497)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// <c>Time</c> is the project's uint (<c>uint</c>) convention for every codec's
/// time slot (cf. <c>ProjectileInstance.TimeCreate</c> / <c>ShipUpdate</c> /
/// <c>ServerShipUpdate.Time</c>); <c>WarpID</c> and <c>ExpendableTypeID</c> are
/// both <c>short</c> (derived from <c>ObjectID</c>, <c>igc.h:477,488,497</c>).
/// The timeExplosion is a future server tick index (the detonation instant),
/// not the current tick. Simple fixed-size message, no var-item — same shape as
/// <see cref="MineDestroyed"/>/<see cref="ProbeDestroyed"/>. Emitted by the
/// server's <c>BEGIN_PFM_CREATE(g.fm, pfmWB, S, WARP_BOMB)</c> site
/// (<c>FedSrv.CPP:12553</c>) in <c>FedSrvSiteBase::WarpBombEvent</c>, broadcast
/// to the WHOLE mission (<c>GetGroupRealSides()</c>) — unlike every other
/// ordnance broadcast in this project (P88/P90/P94/P99 same-side only), a
/// warp-bombing is mission-wide news. The client echoes the pending bomb back
/// through <c>AddBomb</c> (<c>clintlib/appmsg.cpp:850-897</c>); this port
/// deliberately does NOT model the <c>WarpBombLink</c>/<c>GetBombs()</c>
/// late-joiner re-export list (out of scope, see the task report).
/// </summary>
public sealed class WarpBomb : IFedMessage
{
    public const ushort Fmid = 188;

    /// <summary>Wire width of the whole message: header(4) + timeExplosion(4) + warpid(2) + expendableid(2).</summary>
    public const int FixedSize = 12;

    /// <summary>The future server tick index at which the explosion resolves
    /// (uint <c>Time</c> — the project's wire-time convention).</summary>
    public uint TimeExplosion;
    /// <summary>The bombed warp's id (<c>WarpID</c>).</summary>
    public short WarpIdBombed;
    /// <summary>The missile type's id (<c>ExpendableTypeID</c> — <c>DataMissileTypeIGC.ExpendableTypeId</c>).</summary>
    public short ExpendableIdMissile;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeExplosion);
        w.WriteI16(WarpIdBombed);
        w.WriteI16(ExpendableIdMissile);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"WARP_BOMB internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static WarpBomb Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"WARP_BOMB needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected WARP_BOMB fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new WarpBomb
        {
            TimeExplosion = r.ReadU32(),
            WarpIdBombed = r.ReadI16(),
            ExpendableIdMissile = r.ReadI16(),
        };
    }
}
