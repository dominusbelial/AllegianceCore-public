namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PROMOTE</c> (id 189, <c>Messages.h:975-977</c>:
/// <c>DEFINE_FEDMSG(S, PROMOTE, 189) ShipID shipidPromoted; END_FEDMSG</c>) —
/// the server's acknowledgment broadcast confirming a turret gunner was
/// promoted into the command seat of its former carrier. Sent by the native
/// <c>case FM_C_PROMOTE</c> handler (<c>FedSrv.CPP:6377-6381</c>) to
/// <c>GetGroupSectorFlying(...)</c> — the whole sector's peers in this port
/// (see <c>Server.SendSectorWide</c>), or the requesting side's group in the
/// never-reached no-cluster fallback. Carries <c>shipidPromoted</c> = the
/// <b>promoted ship's</b> object id (the gunner that took over), not the old
/// parent's — <c>pfmPromote-&gt;shipidPromoted = pship-&gt;GetObjectID()</c>
/// (<c>FedSrv.CPP:6379</c>) where <c>pship</c> is the matched child.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 189
///   offset 4: short  shipidPromoted  (ShipID = ObjectID = short, igc.h:477,480)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class Promote : IFedMessage
{
    public const ushort Fmid = 189;
    public const int FixedSize = 6;   // header + shipidPromoted short — no padding (short is 2-aligned)

    /// <summary>The id of the ship that was promoted into the command seat.</summary>
    public short ShipidPromoted;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipidPromoted);   // shipidPromoted (ShipID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PROMOTE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Promote Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROMOTE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROMOTE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        return new Promote { ShipidPromoted = r.ReadI16() };
    }
}

/// <summary>
/// <c>FM_C_PROMOTE</c> (id 190, <c>Messages.h:979-981</c>:
/// <c>DEFINE_FEDMSG(C, PROMOTE, 190) Mount mountidPromoted; END_FEDMSG</c>) —
/// the commanding ship's request to promote one of its boarded turret gunners
/// into the command seat. The native handler
/// (<c>FedSrv.CPP:6353-6382</c>) walks the requesting player's own child
/// ships (<c>GetIGCShip()-&gt;GetChildShips()</c>) and promotes the first whose
/// <c>GetTurretID() == mountidPromoted</c>; a mount that matches no real
/// boarded child is a silent no-op (no crash, no ack).
/// <para>
/// <c>Mount</c> is <c>char</c> (<c>igc.h:841</c>) — the same turret-slot
/// concept <see cref="InactiveTurretUpdate"/>/P109's turret mounting already
/// uses, represented in this port on <see cref="Sim.Ship.TurretId"/> as a
/// <c>short?</c>. The wire value is a single byte; it is widened to
/// <c>short</c> in the codec so the handler can compare it directly against
/// <see cref="Sim.Ship.TurretId"/>.
/// </para>
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 190
///   offset 4: char   mountidPromoted  (Mount = char, igc.h:841)
///   offset 5: 1 byte alignment pad (C++ struct align 2 → sizeof 6)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class PromoteRequest : IFedMessage
{
    public const ushort Fmid = 190;
    public const int FixedSize = 6;   // header + mountidPromoted char @4 + 1 pad @5

    /// <summary>The turret mount id whose boarded gunner should be promoted
    /// (widened from the single-byte <c>Mount</c>; compare against
    /// <see cref="Sim.Ship.TurretId"/>).</summary>
    public short MountidPromoted;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                          // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte(unchecked((byte)MountidPromoted));  // offset 4: char mountidPromoted
        w.WriteByte(0);                         // offset 5: alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PROMOTE request internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PromoteRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROMOTE request needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROMOTE request fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        // Mount is a C++ `char` (signed, igc.h:841): sign-extend the byte so
        // e.g. NA (-1) widens to the short -1 the handler compares against
        // Ship.TurretId, exactly as the native short field would read.
        var b = r.ReadByte();
        return new PromoteRequest { MountidPromoted = b >= 128 ? (short)(b - 256) : b };
    }
}
