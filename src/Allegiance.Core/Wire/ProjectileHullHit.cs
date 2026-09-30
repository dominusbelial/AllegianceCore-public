using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P364 — the server
/// telling the MonoGame client that a projectile strike connected on a
/// damagable visible target, carrying the real impact point, the real
/// hit-surface normal, and the target's radius (which fixes the spark size).
/// Native delivers this through <c>pModel-&gt;GetThingSite()-&gt;AddHullHit(pos,
/// pos.Normalize())</c> (<c>src/Igc/projectileIGC.cpp:220-226</c>), a same-process
/// call into the client's render attach-site; here the sim is server-authoritative
/// and the client is a pure renderer, so the same data crosses the wire as an
/// explicit message instead — exactly the pattern P263's
/// <see cref="ExplosionEvent"/> established for the blast VFX.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (projectileIGC.cpp:220-226).</b> On impact with
/// a non-mine target <c>CprojectileIGC::HandleCollision</c> computes
/// <c>position1</c> (the round at the collision instant) and <c>position2</c>
/// (the target at that instant), calls <c>ReceiveDamage</c>, then — when the
/// round has no blast power, dealt hull-or-worse damage (<c>dr &lt;=
/// c_drHullDamage</c>), the target is visible &amp; in a cluster, and the damage
/// type lacks the <c>c_dmgidNoDebris</c> flag — fires
/// <c>AddHullHit(pos = position1 − position2, pos.Normalize())</c>. The client
/// <c>ThingGeo::AddHullHit</c> (<c>effect/thinggeo.cpp:1324-1345</c>) then launches
/// two debris <b>bits</b> from the world impact point along the hit normal with a
/// ±0.25 random spread, sized <c>radius/2</c>, plus a short-lived white
/// tangent/normal decal (<c>HullHitGeoImpl::Render</c>,
/// <c>effect/efgeo.cpp:674-749</c>). This message carries the <em>input</em> of
/// that call: the world impact position (<c>position1</c>), the world normal
/// (<c>normalize(position1 − position2)</c>), and the target's collision radius
/// (the port reproduces <c>size = radius/2</c> client-side from it — the client
/// has no static ship-type core to resolve the radius itself). The decal half is
/// not portable to this renderer (it paints on the target's mesh surface, which
/// this billboard-only particle pipeline has no projected-decal pass for), so the
/// ported visual is the bits; that substitution is documented in
/// <c>Rendering/HullHitSparkParticles.cs</c> and the P364 writeup.</para>
/// <para><b>Layout.</b> Our own (no native <c>DEFINE_FEDMSG</c> to be faithful
/// to), fixed-size under the project's natural-4-byte-alignment convention.
/// P463 widened the message by one discriminator: it now also covers
/// <em>shield-absorbed</em> strikes (the shield-hit sound / flash signal),
/// carried as a one-byte <c>AbsorbedByShield</c> flag padded to the following
/// 4-byte-aligned float:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 213
///   offset  4: short targetId   (object id of the struck target; -1 = NA)
///   offset  6: short clusterId  (the sector the strike occurred in)
///   offset  8: byte  absorbedByShield (1 = shield-absorbed, 0 = hull/killed)
///   offset  9: byte  pad0 (alignment)
///   offset 10: ushort pad1 (alignment)
///   offset 12: float radius     (target's collision radius — spark size/2)
///   offset 16: Vector3 position (world impact point, 12 B)
///   offset 28: Vector3 normal   (world hit-surface normal, 12 B)
///   = 40 B fixed, no var items → cbmsg = 40
/// </code>
/// </para>
/// <para>FMID <b>213</b> sits above native's entire real FMID range (which tops
/// out at 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a
/// native message this port might later add faithfully — the same convention as
/// P230's MISSILE_LOCK_UPDATE(204), P234's MISSILE_UPDATE(205), P255's
/// OWN_SHIP_MINING_UPDATE(206) and P263's EXPLOSION_EVENT(207).</para>
/// </remarks>
public sealed class ProjectileHullHit : IFedMessage
{
    public const ushort Fmid = 213;

    /// <summary>Wire width of the whole message: header(4) + targetId(2) +
    /// clusterId(2) + absorbedByShield(1) + pad(3) + radius(4) + two Vector3
    /// (24) = 40 B. All members 4-byte aligned (the targetId/clusterId/
    /// flag/pad quartet aligns to the following float).</summary>
    public const int FixedSize = 40;

    /// <summary>The object id of the struck target (ship/probe/station), or -1
    /// (NA) for a target with no portable id (asteroid). Lets the client
    /// associate/annotate the spark; not required for rendering.</summary>
    public short TargetId;

    /// <summary>The sector the strike occurred in — lets the client render only
    /// sparks in the local ship's own sector (the same cluster gate the
    /// explosion renderer applies).</summary>
    public short ClusterId;

    /// <summary>P463 — whether the strike was <em>absorbed by the target's
    /// shield</em> (the server's <c>DamageResult.ShieldDamage</c>) rather than
    /// dealing hull-or-worse damage (<c>Killed</c>/<c>HullDamage</c>). Keyed on
    /// native's <c>bAbsorbedByShield</c> argument to <c>RegisterHit</c>
    /// (trekigc.cpp:2368), which chooses my/other × shield/hull hit sound. The
    /// hull-impact spark must spawn only when this is false (native fires
    /// <c>AddHullHit</c> only for hull-or-worse damage,
    /// projectileIGC.cpp:193); P463b's shield-flare visual consumes the true
    /// case.</summary>
    public bool AbsorbedByShield;

    /// <summary>The struck target's collision radius in world units; the client
    /// sizes each spark bit <c>radius / 2</c> (native <c>thinggeo.cpp:1328</c>).</summary>
    public float Radius;

    /// <summary>The world-space impact point — native <c>position1</c>, the
    /// round's position at the collision instant (<c>projectileIGC.cpp:174</c>).</summary>
    public Vector3 Position;

    /// <summary>The world-space hit-surface normal — native
    /// <c>normalize(position1 − position2)</c> (<c>projectileIGC.cpp:224-225</c>);
    /// the spark bits are launched roughly along it.</summary>
    public Vector3 Normal;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(TargetId);
        w.WriteI16(ClusterId);
        w.WriteByte(AbsorbedByShield ? (byte)1 : (byte)0);
        w.WriteByte(0);             // pad0
        w.WriteU16(0);              // pad1
        w.WriteU32(BitConverter.SingleToUInt32Bits(Radius));
        WriteVec(ref w, Position);
        WriteVec(ref w, Normal);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PROJECTILE_HULL_HIT internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ProjectileHullHit Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROJECTILE_HULL_HIT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROJECTILE_HULL_HIT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        short target = r.ReadI16();
        short cluster = r.ReadI16();
        byte absorbed = r.ReadByte();
        r.Advance(3);                                   // pad0 + pad1
        float radius = BitConverter.UInt32BitsToSingle(r.ReadU32());
        var pos = ReadVec(ref r);
        var normal = ReadVec(ref r);
        return new ProjectileHullHit { TargetId = target, ClusterId = cluster, AbsorbedByShield = absorbed != 0, Radius = radius, Position = pos, Normal = normal };
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
