namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_STATION_CAPTURE</c> (id 58), the server's "this station changed
/// sides" notice (<c>Messages.h:429-434</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, STATION_CAPTURE, 58)
///     StationID stationID;
///     SideID    sidOld;
///     SideID    sidNew;
///     ShipID    shipIDCredit;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 58
///   offset 4: short  stationID    (StationID — the captured station)
///   offset 6: short  sidOld       (SideID   — the station's old owner)
///   offset 8: short  sidNew       (SideID   — the station's new owner)
///   offset 10: short shipIDCredit (ShipID   — the capturing ship)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// All four fields are 2-byte ids back-to-back with no alignment gap (shorts are
/// 2-aligned, so offsets 4,6,8,10), so the fixed size is exactly
/// <c>header(4) + 4×short = 12</c>. <c>sidOld</c>/<c>sidNew</c> come straight
/// from native <c>CFSShip::CaptureStation</c> (<c>fsship.cpp:626-627</c>:
/// <c>sidOld = psideOld-&gt;GetObjectID()</c> read before re-siding,
/// <c>sidNew = iSide</c> = the capturing side) and <c>shipIDCredit</c> = the
/// capturing ship's id (<c>fsship.cpp:628</c>).
/// <b>Wire only.</b> The port's sim-side capture path (the hostile-capture
/// branch of <c>Model.Sector.ResolveDockCollisions</c> → the host's
/// <c>CaptureStationEvent</c> seam → <c>Server.BroadcastStationCapture</c>,
/// P126) is what drives a real <c>STATION_CAPTURE</c>; this codec just defines
/// the message shape, the same split as <see cref="StationDestroyed"/> (P125).
/// </summary>
public sealed class StationCapture : IFedMessage
{
    public const ushort Fmid = 58;

    /// <summary>Wire width of the whole message: header(4) + stationID(2) +
    /// sidOld(2) + sidNew(2) + shipIDCredit(2). No alignment gaps — the four
    /// 2-byte ids trail back-to-back at offsets 4,6,8,10.</summary>
    public const int FixedSize = 12;

    public short StationId;        // the captured station
    public short SidOld;           // the station's previous owner
    public short SidNew;           // the station's new owner (the capturing side)
    public short ShipIdCredit;     // the capturing ship's id

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(StationId);
        w.WriteI16(SidOld);
        w.WriteI16(SidNew);
        w.WriteI16(ShipIdCredit);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"STATION_CAPTURE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static StationCapture Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"STATION_CAPTURE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected STATION_CAPTURE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new StationCapture
        {
            StationId = r.ReadI16(),
            SidOld = r.ReadI16(),
            SidNew = r.ReadI16(),
            ShipIdCredit = r.ReadI16(),
        };
    }
}
