namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PLAYER_RESCUED</c> (id 16), the server's "a lifepod was picked up"
/// message (<c>src/Inc/Messages.h:163-166</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, PLAYER_RESCUED, 16)
///     ShipID    shipIDRescuer;
///     ShipID    shipIDRescuee;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 16
///   offset 4: short  shipIDRescuer  (ShipID, igc.h:480)
///   offset 6: short  shipIDRescuee  (ShipID, igc.h:480)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// <b>Wire only.</b> The native sender is the server's rescue handler,
/// <c>FedSrv.CPP:13052</c>, which awards rescue/picked-up achievements then
/// writes <c>shipIDRescuer = pshipRescuer-&gt;GetObjectID()</c> and
/// <c>shipIDRescuee = pshipRescued-&gt;GetObjectID()</c>. Its real trigger is
/// the rescue-probe capability (<c>c_habmRescue</c>/<c>c_habmLifepod</c>), seen
/// but not acted on during Phase 2's probe work — a separate, later mechanic
/// this slice does not wire.
/// </summary>
public sealed class PlayerRescued : IFedMessage
{
    public const ushort Fmid = 16;

    /// <summary>Wire width of the whole message: header(4) + two ShipIDs (2
    /// each). No alignment gaps — both 2-byte IDs sit back-to-back, no member
    /// wider than 2.</summary>
    public const int FixedSize = 8;

    public short ShipIdRescuer;    // the rescuing ship
    public short ShipIdRescuee;    // the rescued ship (the lifepod occupant)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipIdRescuer);
        w.WriteI16(ShipIdRescuee);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PLAYER_RESCUED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PlayerRescued Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PLAYER_RESCUED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PLAYER_RESCUED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new PlayerRescued
        {
            ShipIdRescuer = r.ReadI16(),
            ShipIdRescuee = r.ReadI16(),
        };
    }
}
