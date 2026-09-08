namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ENTER_LIFEPOD</c> (id 166, <c>Messages.h:886-888</c>) — the
/// server's \"you are now a docked Lifepod at a friendly station\" notice,
/// sent to the <b>dying player's own connection only</b>, never broadcast
/// (<c>FedSrv.CPP:12059-12062</c>: <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), FM_GUARANTEED, FM_FLUSH)</c>).
/// Emitted by the death-respawn slice (P105) on the exact seam P103 wired:
/// <c>ShipKilled</c> → <c>BroadcastKillShip</c>, alongside the existing
/// <c>SHIP_DELETE(20)</c> broadcast.
/// <para>
/// The struct declares a <c>ShipID shipID</c> field (a <c>short</c>:
/// <c>ObjectID</c> typedef, <c>igc.h:477,480</c>) on the wire — despite the
/// prior task brief claiming the message was header-only. Confirmed against
/// the native sources: <c>Messages.h:886-888</c> shows <c>ShipID shipID;</c>,
/// the client casts the struct (<c>appmsg.cpp:1950</c>), and the managed
/// wrapper writes it (<c>AllMessages.h:6470-6479</c> <c>pfm-&gt;shipID =
/// shipID</c>). The native <b>sender</b> leaves it unset (the
/// <c>//pfmEnterLifepod-&gt;shipID = shipID;</c> comment,
/// <c>FedSrv.CPP:12061</c>) — but we populate it with the dying ship's id,
/// since the field exists on the wire and carrying the correct value is
/// strictly more useful than garbage.
/// </para>
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 166
///   offset  4: short  shipID   (ShipID = ObjectID = short)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
/// </summary>
public sealed class EnterLifepod : IFedMessage
{
    public const ushort Fmid = 166;
    public const int FixedSize = 6;   // header + shipID short — no padding (short is 2-aligned)

    public short ShipId;             // the dying player's ship (Messages.h:887)

    /// <summary>Build the <c>ENTER_LIFEPOD</c> sent to a dying player's own
    /// connection, naming their ship.</summary>
    public static EnterLifepod ForPlayer(short shipId) => new() { ShipId = shipId };

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);         // shipID (short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ENTER_LIFEPOD internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static EnterLifepod Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ENTER_LIFEPOD needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ENTER_LIFEPOD fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new EnterLifepod { ShipId = r.ReadI16() };
    }
}
