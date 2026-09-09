using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ASTEROID_MINED</c> (id 198), the server's "this He3 rock was
/// mined" notice (<c>Messages.h:1027-1031</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, ASTEROID_MINED, 198)  // sent by server when an He3 rock is mined
///       SectorID            clusterID;
///       AsteroidID          asteroidID;
///       BytePercentage      bpOreFraction;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 198
///   offset 4: short  clusterID    (SectorID, igc.h:487)
///   offset 6: short  asteroidID   (AsteroidID, igc.h:492)
///   offset 8: byte   bpOreFraction (BytePercentage, div-240, Utility.h:1307-1312)
///   = 9 B fixed, no var items → cbmsg = 9
/// </code>
/// Same fixed-size, no-var-item shape as <see cref="AsteroidDestroyed"/>
/// (short/short/byte) — a mining tick's ore-report, distinct only in fmid and
/// in carrying the rock's remaining <c>BytePercentage</c> instead of an
/// <c>explodeF</c> bool. <c>bpOreFraction</c> is the rock's remaining ore as a
/// div-240 <c>BytePercentage</c>, produced by <see cref="CompactQuant.Percent"/>
/// (<c>Utility.h:1342-1358</c>), exactly the convention the ship-update slots
/// use (cf. <c>ServerShipUpdate.OreFraction</c>, <c>ServerShipUpdate.cs:127</c>).
/// <b>Wire only.</b> The port's sim-side mining path (<c>Asteroid.MineOre</c> +
/// <c>Model.Sector.ResolveMining</c>, throttled to the native &gt;3.0 ore-change
/// threshold, <c>asteroidigc.h:246-251</c>) is what drives a real
/// <c>ASTEROID_MINED</c>; this codec just defines the message shape.
/// </summary>
public sealed class AsteroidMined : IFedMessage
{
    public const ushort Fmid = 198;

    /// <summary>Wire width of the whole message: header(4) + clusterID(2) +
    /// asteroidID(2) + bpOreFraction(1). No alignment gaps — the byte trails the
    /// two 2-byte IDs back-to-back at offset 8.</summary>
    public const int FixedSize = 9;

    public short ClusterId;      // the sector the asteroid is in
    public short AsteroidId;     // the mined asteroid
    public byte BpOreFraction;   // remaining ore as a div-240 BytePercentage (0..240)

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from a live mined <see cref="Asteroid"/> and the
    /// rock's new ore fraction (<c>asteroidigc.h:249</c>) reported by the seam —
    /// bakes the div-240 <see cref="CompactQuant.Percent"/> conversion in so the
    /// caller never touches the wire convention directly.</summary>
    public static AsteroidMined ForAsteroid(Asteroid a, float newOre)
        => new()
        {
            ClusterId = a.Data.ClusterId,
            AsteroidId = a.Data.AsteroidId,
            BpOreFraction = CompactQuant.Percent(newOre),
        };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(AsteroidId);
        w.WriteByte(BpOreFraction);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ASTEROID_MINED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AsteroidMined Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ASTEROID_MINED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ASTEROID_MINED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new AsteroidMined
        {
            ClusterId = r.ReadI16(),
            AsteroidId = r.ReadI16(),
            BpOreFraction = r.ReadByte(),
        };
    }
}
