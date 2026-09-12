namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MISSILE_SPOOFED</c> (id 110, <c>Messages.h:739-742</c>) — the
/// server's notice that one missile's lock was broken: its target just got
/// retargeted onto a freshly-deployed <see cref="CreateChaff"/> decoy. Carries
/// only the affected missile's id. Native shape:
/// <code>
///   DEFINE_FEDMSG(S, MISSILE_SPOOFED, 110)
///     ObjectID missileID;
///   END_FEDMSG
/// </code>
/// The server's <c>HandleFireExpendable</c> emits one of these per missile
/// spoofed by the chaff, sandwiched between the <see cref="CreateChaff"/> and
/// the trailing <see cref="EndSpoofing"/> marker.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 110
///   offset 4: short  missileID   (MissileID = ObjectID = short, igc.h:477,493)
///   = 6 B fixed (2-aligned tail, same C++ sizeof as BoardShip/LeaveShip)
/// </code>
public sealed class MissileSpoofed : IFedMessage
{
    public const ushort Fmid = 110;
    public const int FixedSize = 6;   // header + missileID short @4

    /// <summary>The id of the missile whose target was spoofed onto the chaff
    /// (<c>MissileID</c> = <c>ObjectID</c> = <c>short</c>).</summary>
    public short MissileId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(MissileId);      // offset 4: missileID (short)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 tail (offset 5)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MISSILE_SPOOFED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MissileSpoofed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MISSILE_SPOOFED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSILE_SPOOFED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short missileId = r.ReadI16();
        return new MissileSpoofed { MissileId = missileId };
    }
}
