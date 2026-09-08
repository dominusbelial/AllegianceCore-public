namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SHIP_DELETE</c> (id 20, <c>Messages.h:188-199</c>) — the server's
/// "a ship stopped existing for a peer" message: the victim's <c>shipID</c>
/// and a <c>ShipDeleteReason</c>. Native layout is the C++ struct cast straight
/// to bytes (<c>CASTPFM</c>, <c>appmsg.cpp:457</c>), and <c>cbmsg =
/// sizeof(FMD_S_SHIP_DELETE)</c>. <c>ShipID</c> is a <c>short</c> typedef
/// (<c>ObjectID</c>, <c>igc.h:477,480</c>); <c>ShipDeleteReason</c> is a plain
/// C++ <c>enum</c> with no explicit underlying type, so MSVC sizes it as a
/// full <c>int</c> aligned to 4 — the same 4-byte-enum convention the port
/// already applies to <c>ChatTarget</c> (<c>ChatData.cs:15</c>,
/// <c>IChatRoutingTarget.cs:17</c>). That inserts 2 padding bytes after the
/// <c>short shipID</c>:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 20
///   offset  4: short  shipID   (ShipID = ObjectID = short)
///   offset  6: 2 pad bytes (0) — enum 4-align
///   offset  8: int32  sdr      (ShipDeleteReason — plain enum, 4 bytes)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// This slice emits <b>only</b> <c>SDR_KILLED</c>(1) — the single reason this
/// port's existing kill path can drive honestly. The other six reasons need a
/// per-side visibility-culling system this port does not have (see
/// <c>SUICIDE_SHIP_DELETE_P103.md</c>); they are deliberately not
/// constructible here.
/// </summary>
public sealed class ShipDelete : IFedMessage
{
    public const ushort Fmid = 20;
    public const int FixedSize = 12;

    /// <summary><c>SDR_KILLED</c> — ordinal 1 of <c>ShipDeleteReason</c>
    /// (<c>Messages.h:188-190</c>, the second enumerator).</summary>
    public const int SdrKilled = 1;

    public short ShipId;       // the victim (ShipID = ObjectID = short)
    public int Sdr = SdrKilled; // ShipDeleteReason — this slice only ever emits SDR_KILLED

    /// <summary>Build the <c>SHIP_DELETE</c> the kill path emits: the victim's
    /// id with <c>SDR_KILLED</c>.</summary>
    public static ShipDelete ForKilledShip(short shipId) =>
        new() { ShipId = shipId, Sdr = SdrKilled };

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);
        w.WriteZeros(2);            // enum 4-align pad (native struct layout)
        w.WriteI32(Sdr);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SHIP_DELETE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ShipDelete Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SHIP_DELETE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SHIP_DELETE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        var msg = new ShipDelete { ShipId = r.ReadI16() };
        r.Advance(2);               // enum 4-align pad
        msg.Sdr = r.ReadI32();
        return msg;
    }
}
