using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P463c — the server
/// telling the MonoGame client that two bodies collided in a sector, carrying
/// the two party object ids, the sector, the impact point, and each party's
/// camera-jiggle intensity. Native delivers the same event through a
/// same-process call into the client's in-process IGC simulation —
/// <c>igcsite-&gt;PlaySoundEffect(collisionSound, this)</c> /
/// <c>PlayVisualEffect(effectJiggle, this, …)</c>
/// (<c>src/Igc/shipIGC.cpp:834, 1004, 1189-1195</c>) — so the port sends the
/// data the client needs as an explicit message instead, exactly the pattern
/// P364's <see cref="ProjectileHullHit"/> established for the impact spark and
/// P263's <see cref="ExplosionEvent"/> for the blast VFX.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (shipIGC.cpp:834,1004,1189-1195).</b> Each of
/// the ship↔station, ship↔asteroid (shared bounce block) and ship↔ship
/// collision paths plays <c>collisionSound</c> (<c>colisionlarge</c>,
/// sounddef.mdl:308) for <b>each party</b> at its location, plus the
/// <c>effectJiggle</c> camera shake. The jiggle intensity is computed from the
/// impact damage with <c>c_impactJiggleCoefficient = 1/20</c>
/// (shipIGC.cpp:624): the ship-vs-static-rock forms (station/asteroid) shake
/// only the ship with <c>2·damage1·coeff</c> (shipIGC.cpp:836,1006), while the
/// ship-ship form folds both parties — party1
/// <c>(2·damage1+damage2)·coeff</c>, party2 <c>(damage1+2·damage2)·coeff</c>
/// (shipIGC.cpp:1191,1195). This message carries the <em>result</em> of those
/// computations so the client never re-derives damage; the client keeps only
/// the per-party role: play the positional collision sound in its own sector
/// and feed its own ship's jiggle into P449b's <c>FlightJiggle</c>.</para>
/// <para><b>Party ids.</b> A ship party carries its real object id; a
/// non-ship party (asteroid or station) carries NA(-1), exactly the convention
/// <see cref="ProjectileHullHit.TargetId"/> uses for a rock with no portable
/// short id. The client only compares the ids against its own ship id to decide
/// which jiggle to feed, so a rock/station party (never the local ship, never
/// shaken by native) simply contributes no jiggle. The impact point is shared
/// by both parties — the same location native positions the sound at — so the
/// client plays one positional cue there.</para>
/// <para><b>Layout.</b> Our own (no native <c>DEFINE_FEDMSG</c> to be faithful
/// to), fixed-size under the project's natural-4-byte-alignment convention:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 216
///   offset  4: short party1Id (first party's object id; the ship in a
///                             ship↔rock/station collision)
///   offset  6: short party2Id (second party's object id; NA(-1) for rock/station)
///   offset  8: short clusterId (the sector the collision occurred in)
///   offset 10: short pad0 (alignment)
///   offset 12: float jiggle1 (party1's camera-jiggle intensity)
///   offset 16: float jiggle2 (party2's camera-jiggle intensity)
///   offset 20: Vector3 position (world impact point, 12 B)
///   = 32 B fixed, no var items → cbmsg = 32
/// </code>
/// </para>
/// <para>FMID <b>216</b> sits above native's entire real FMID range (which tops
/// out at 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a
/// native message this port might later add faithfully — the same convention as
/// P230's MISSILE_LOCK_UPDATE(204) … P463's PROJECTILE_HULL_HIT(213).</para>
/// </remarks>
public sealed class CollisionEvent : IFedMessage
{
    public const ushort Fmid = 216;

    /// <summary>Wire width of the whole message: header(4) + three shorts + pad
    /// (8) + two floats (8) + one Vector3 (12) = 32 B. All members 4-byte
    /// aligned (the party1/party2/cluster/pad quartet aligns to the following
    /// float).</summary>
    public const int FixedSize = 32;

    /// <summary>The first party's object id — the ship in a ship↔rock/station
    /// collision, one of the two ships in a ship↔ship collision.</summary>
    public short Party1Id;

    /// <summary>The second party's object id — the other ship in a ship↔ship
    /// collision, or NA(-1) for an asteroid or station (no portable short id,
    /// and never a camera to shake).</summary>
    public short Party2Id;

    /// <summary>The sector the collision occurred in — lets the client render
    /// only collisions in the local ship's own sector (the same cluster gate the
    /// hit-sound/explosion layers apply).</summary>
    public short ClusterId;

    /// <summary>Party1's camera-jiggle intensity (the native
    /// <c>effectJiggle</c> magnitude for that party) — fed into P449b's
    /// <c>FlightJiggle.SetJiggle</c> when the local ship IS party1.</summary>
    public float Jiggle1;

    /// <summary>Party2's camera-jiggle intensity — fed into P449b's
    /// <c>FlightJiggle.SetJiggle</c> when the local ship IS party2 (0 for a
    /// rock/station, which native never shakes).</summary>
    public float Jiggle2;

    /// <summary>The world-space impact point — where the two bodies met,
    /// shared by both parties and the location the positional collision sound
    /// is played at.</summary>
    public Vector3 Position;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(Party1Id);
        w.WriteI16(Party2Id);
        w.WriteI16(ClusterId);
        w.WriteU16(0);              // pad0
        w.WriteU32(BitConverter.SingleToUInt32Bits(Jiggle1));
        w.WriteU32(BitConverter.SingleToUInt32Bits(Jiggle2));
        WriteVec(ref w, Position);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"COLLISION_EVENT internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static CollisionEvent Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"COLLISION_EVENT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected COLLISION_EVENT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        short p1 = r.ReadI16();
        short p2 = r.ReadI16();
        short cluster = r.ReadI16();
        r.Advance(2);                                   // pad0
        float j1 = BitConverter.UInt32BitsToSingle(r.ReadU32());
        float j2 = BitConverter.UInt32BitsToSingle(r.ReadU32());
        var pos = ReadVec(ref r);
        return new CollisionEvent { Party1Id = p1, Party2Id = p2, ClusterId = cluster, Jiggle1 = j1, Jiggle2 = j2, Position = pos };
    }

    private static void WriteVec(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private static Vector3 ReadVec(ref WireReader r) =>
        new(BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()));
}
