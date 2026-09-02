namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_DOCKED</c> (id 61, <c>Messages.h:448-450</c>) — the server's
/// "you are now docked" notification to the player who just docked. Its
/// single <c>StationID</c> field is the wire trigger that makes the native
/// client's <c>ship.GetStation()</c> resolve.
/// <para>
/// Mechanics (traced in the reference tree, not guessed): when a player
/// docks, the server-side host's <c>FedSrvSiteBase::DockWithStationEvent</c>
/// (<c>FedSrv.CPP:13087-13177</c>) latches the ship's station state AND sends
/// this message to the docking player. The client's <c>FM_S_DOCKED</c> handler
/// (<c>clintlib/appmsg.cpp:1092-1115</c>) is the <b>only</b> call site that
/// calls <c>m_ship-&gt;SetStation(reallyNonNull)</c> (<c>:1113</c>) — which is
/// what fills the member <c>IshipIGC::GetStation()</c> reads
/// (<c>shipIGC.h:417-420</c>). The raw <c>SHIP_STATUS(90)</c> wire fields
/// (<c>StationID</c>/<c>State</c>) alone never populate <c>m_station</c>;
/// the resolved object reference only appears once this message is processed.
/// P53 proved the wire-status half; this message is the missing half that
/// makes the native object model reflect it.
/// </para>
/// <para>Layout: header(4) + <c>StationID</c>(short, 2) = <b>6 bytes fixed</b>,
/// no padding (the single <c>StationID</c> short has no DWORD member forcing
/// 4-alignment — same shape as <see cref="Docked"/>'s 6-byte fmid-48 frame).
/// Sent once per dock transition by <c>ServerHost.DockWithStationEvent</c>
/// through the <c>ShipDocked</c> event, routed only to the docking peer.</para>
/// </summary>
public sealed class DockedNotify : IFedMessage
{
    public const ushort Fmid = 61;
    public const int FixedSize = 6;

    /// <summary>The <c>StationID</c> of the station the ship docked at
    /// (StationID = short, igc.h:477,840). Carried verbatim from the real
    /// <c>DataStationIGC.StationId</c>.</summary>
    public short StationId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(StationId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"DOCKED(61) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static DockedNotify Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DOCKED(61) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DOCKED(61) fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        return new DockedNotify { StationId = r.ReadI16() };
    }
}
