namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_STATION_DESTROYED</c> (id 59), the server's "this station was
/// destroyed" notice (<c>Messages.h:436-439</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, STATION_DESTROYED, 59)
///     StationID stationID;
///     ShipID    launcher;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 59
///   offset 4: short  stationID   (StationID, igc.h)
///   offset 6: short  launcher    (ShipID — the destroying ship, or NA (-1)
///                                 when the launcher is not a ship)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Both fields are 2-byte ids back-to-back with no alignment gap, so the fixed
/// size is exactly <c>header(4) + stationID(2) + launcher(2) = 8</c>.
/// <c>launcher</c> is the killing <b>ship's</b> id when the credited launcher
/// is a ship, else <c>NA</c> (<c>FedSrv.CPP:12278</c>:
/// <c>(otLauncher == OT_ship) ? plauncher-&gt;GetObjectID() : NA</c>).
/// <b>Wire only.</b> The port's sim-side death path (<c>Station.ReceiveDamage</c>
/// + <c>Model.Sector.ResolveCombatCollisions</c>, P125) is what drives a real
/// <c>STATION_DESTROYED</c>; this codec just defines the message shape, the
/// same split as <see cref="AsteroidDestroyed"/> (P102).
/// </summary>
public sealed class StationDestroyed : IFedMessage
{
    public const ushort Fmid = 59;

    /// <summary>Wire width of the whole message: header(4) + stationID(2) +
    /// launcher(2). No alignment gaps — the two 2-byte ids trail back-to-back at
    /// offsets 4 and 6.</summary>
    public const int FixedSize = 8;

    public short StationId;      // the destroyed station
    public short Launcher;       // the credited launcher ship id, or NA (-1)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(StationId);
        w.WriteI16(Launcher);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"STATION_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static StationDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"STATION_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected STATION_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new StationDestroyed
        {
            StationId = r.ReadI16(),
            Launcher = r.ReadI16(),
        };
    }
}
