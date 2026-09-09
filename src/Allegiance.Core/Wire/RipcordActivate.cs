namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_RIPCORD_ACTIVATE</c> (id 185, <c>Messages.h:955-960</c>:
/// <c>DEFINE_FEDMSG(S, RIPCORD_ACTIVATE, 185) ShipID shipidRipcord; ObjectType
/// otRipcord; ObjectID oidRipcord; SectorID sidRipcord; END_FEDMSG</c>) — the
/// server's acknowledgement that a ripcord countdown has begun (or been
/// re-targeted) toward a valid anchor <c>(otRipcord, oidRipcord)</c> in the
/// target sector <c>sidRipcord</c>. Broadcast sector-wide to everyone flying
/// in the requester's current sector on a FIRST activation, or sent to the
/// requester alone on a re-target (<c>RequestRipcord</c>,
/// <c>FedSrv.CPP:13356-13369</c>). Field widths follow the typedefs
/// (<c>igc.h:477,487</c>: <c>ObjectID</c>=<c>ShipID</c>=<c>SectorID</c>=short;
/// <c>ObjectType</c>=short, <c>igc.h:115</c>), so the payload is six
/// 2-byte shorts after the header.
/// </summary>
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 185
///   offset  4: short  shipidRipcord  (ShipID)
///   offset  6: short  otRipcord      (ObjectType)
///   offset  8: short  oidRipcord     (ObjectID)
///   offset 10: short  sidRipcord     (SectorID — the REQUESTED target cluster)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
public sealed class RipcordActivate : IFedMessage
{
    public const ushort Fmid = 185;
    public const int FixedSize = 12;   // header + four shorts — no padding (all 2-aligned)

    /// <summary>The ship whose ripcord was activated/re-targeted.</summary>
    public short ShipidRipcord;
    /// <summary>The anchor object's type (OT_station=1 / OT_ship=0 / ...).</summary>
    public short OtRipcord;
    /// <summary>The anchor object's id.</summary>
    public short OidRipcord;
    /// <summary>The REQUESTED target cluster (not necessarily the anchor's own sector when warp-adjacency is involved).</summary>
    public short SidRipcord;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipidRipcord);
        w.WriteI16(OtRipcord);
        w.WriteI16(OidRipcord);
        w.WriteI16(SidRipcord);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"RIPCORD_ACTIVATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static RipcordActivate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RIPCORD_ACTIVATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RIPCORD_ACTIVATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new RipcordActivate
        {
            ShipidRipcord = r.ReadI16(),
            OtRipcord = r.ReadI16(),
            OidRipcord = r.ReadI16(),
            SidRipcord = r.ReadI16(),
        };
    }
}
