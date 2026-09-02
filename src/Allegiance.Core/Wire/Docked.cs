namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_DOCKED</c> (id 48), the client→server message with the curious
/// dual-documented history. Its own header comment
/// (<c>Messages.h:399-404</c>) explains both halves:
/// <code>
///   DEFINE_FEDMSG(C, DOCKED, 48) // temporary until server detects docking
///     // If stationID != NA, clients that can see this guy receive a SHIP_DELETE, with SDR_DOCKED
///     // If stationID == NA (undocked), other clients receive SHIP_INSTANCE as appropriate
///     // *** stationID != NA is obsolete and no longer supported.
///     // This is only used for undocking
///     StationID stationID;
///   END_FEDMSG
/// </code>
/// Historically this message carried both a (now-obsolete) dock request half —
/// <c>stationID != NA</c> is explicitly "no longer supported" — and the undock
/// half that survives: a client announces it left a dock with
/// <c>stationID == NA</c>. The reference server treats <c>stationID == NA</c>
/// as the pure-undock case (<c>FedSrv.CPP:7050-7052</c> → <c>SetStation(NULL)</c>,
/// reversing <c>CFSShip::ShipStatusDocked</c>). This server implements
/// <b>only the undock path</b>, matching the native design: docking itself is
/// arrival-triggered by the sim (there is no client-initiated dock request — the
/// obsolete half is deliberately not ported, per task scope).
/// <para>Layout: header(4) + <c>StationID</c>(short, 2) = <b>6 bytes fixed</b>,
/// no padding (the single <c>StationID</c> short has no DWORD member forcing
/// 4-alignment).</para>
/// </summary>
public sealed class Docked : IFedMessage
{
    public const ushort Fmid = 48;
    public const int FixedSize = 6;

    /// <summary>The <c>StationID</c> (StationID = short, igc.h:477,840).
    /// <c>NA</c> (<c>-1</c>) is the undock announce this server understands;
    /// a non-NA value is the obsolete dock-request half and is not implemented.</summary>
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
        if (w.Position != FixedSize) throw new InvalidOperationException($"DOCKED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Docked Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DOCKED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DOCKED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        return new Docked { StationId = r.ReadI16() };
    }
}
