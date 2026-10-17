namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ASTEROID_DESTROYED</c> (id 86), the server's \"this asteroid blew
/// up\" message (<c>Messages.h:613-617</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, ASTEROID_DESTROYED, 86)
///       SectorID            clusterID;
///       AsteroidID          asteroidID;
///       bool                explodeF;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 86
///   offset 4: short  clusterID   (SectorID, igc.h:487)
///   offset 6: short  asteroidID  (AsteroidID, igc.h:492)
///   offset 8: byte   explodeF    (bool, 1 byte)
///   = 10 B fixed (one trailing alignment byte), no var items → cbmsg = 10
/// </code>
/// Simple fixed-size message, no var-item — the direct-fire damage path an
/// asteroid undergoes exactly like <see cref="ProbeDestroyed"/> but with the
/// extra <c>explodeF</c> field the native carries. <b>Wire only.</b> The port's
/// sim-side death path (<c>Asteroid.ReceiveDamage</c> +
/// <c>Model.Sector.ResolveCombatCollisions</c>, P102) is what drives a real
/// <c>ASTEROID_DESTROYED</c>; this codec just defines the message shape.
/// </summary>
public sealed class AsteroidDestroyed : IFedMessage
{
    public const ushort Fmid = 86;

    /// <summary>Wire width of the whole message: header(4) + clusterID(2) +
    /// asteroidID(2) + explodeF(1) + trailing pad(1). Native default struct
    /// alignment is 2 (messagecore.h:767-774); packed ProbeState is separate.</summary>
    public const int FixedSize = 10;

    public short ClusterId;      // the sector the asteroid was in
    public short AsteroidId;     // the destroyed asteroid
    public bool ExplodeF;        // true = it exploded (combat death); false = "gone" (station build-over / late-joiner)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(AsteroidId);
        w.WriteByte(ExplodeF ? (byte)1 : (byte)0);
        w.WriteByte(0); // native sizeof(FMD_S_ASTEROID_DESTROYED), align 2
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ASTEROID_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AsteroidDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ASTEROID_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ASTEROID_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new AsteroidDestroyed
        {
            ClusterId = r.ReadI16(),
            AsteroidId = r.ReadI16(),
            ExplodeF = r.ReadByte() != 0,
        };
    }
}
