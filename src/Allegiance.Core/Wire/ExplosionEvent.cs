using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P263 — the server
/// telling the MonoGame client that a real explosion happened, carrying the
/// visual category of the blast. The native architecture delivers this through
/// the <c>IclusterSite::AddExplosion(position, forward, right, velocity, scale,
/// type)</c> site callback (<c>clusterIGC.cpp:407</c>), which in a real client
/// is the render attach-site that plays the sprite VFX; here the sim is
/// server-authoritative and the client is a pure renderer, so the same data
/// crosses the wire as an explicit message instead of a same-process call.
///
/// The port has <b>no</b> dedicated explosion wire message today — the P97
/// <see cref="Sim.Explosion"/> blast is pure in-memory sim state
/// (<c>src/Allegiance.Core/Sim/Explosion.cs</c> remarks) and destruction events
/// (KILL_SHIP 105, MISSILE_DESTROYED 83, MINE_DESTROYED 84, STATION_DESTROYED
/// 59) are broadcast separately. This message is the small addition that ties
/// the P97 sim's outcome to a rendered VFX.
///
/// FMID <b>207</b> sits above native's entire real FMID range (which tops out
/// at 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a native
/// message this port might later add faithfully — the same convention as P230's
/// MISSILE_LOCK_UPDATE(204), P234's MISSILE_UPDATE(205) and P255's
/// OWN_SHIP_MINING_UPDATE(206).
/// </summary>
/// <remarks>
/// <para><b>Faithfulness of fields.</b> It carries the visually-meaningful half
/// of native's <c>AddExplosion</c> contract: <c>position</c> (blast centre),
/// <c>velocity</c> (drift — the fireball sprites ride the emitter's momentum,
/// <c>effect/explosion.cpp:113-114</c>), <c>scale</c> (the 2× model radius the
/// native caller computes, used there for the audio tier, <c>trekigc.cpp:183</c>)
/// and <c>type</c> (the category that fixes the rendered size/duration via the
/// preset table). The native <c>forward</c>/<c>right</c> orient the shock-wave
/// decal in the emitter's plane; this port renders that ring camera-facing
/// (billboarded), so they are deliberately not carried — the sole documented
/// rendering divergence (see <c>Rendering/ExplosionRenderer.cs</c>).</para>
/// <para><b>Layout.</b> Our own (no native <c>DEFINE_FEDMSG</c> to be faithful
/// to), fixed-size under the project's natural-4-byte-alignment convention:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 207
///   offset  4: int   type        (ExplosionType)
///   offset  8: short clusterId   (blast sector)
///   offset 10: short pad         (align to 4)
///   offset 12: float scale       (2 × model radius — audio tier)
///   offset 16: Vector3 position  (12 B)
///   offset 28: Vector3 velocity  (12 B)
///   = 40 B fixed, no var items → cbmsg = 40
/// </code>
/// </para>
/// </remarks>
public sealed class ExplosionEvent : IFedMessage
{
    public const ushort Fmid = 207;

    /// <summary>Wire width of the whole message: header(4) + type(4) + sector(2)
    /// + pad(2) + scale(4) + two Vector3 (24) = 40 B. All members 4-byte aligned
    /// (the sector/pad pair aligns to the following float).</summary>
    public const int FixedSize = 40;

    /// <summary>The blast category — one of <see cref="Sim.ExplosionType"/>; the
    /// client derives the rendered size/duration from the matching preset.</summary>
    public int Type;

    /// <summary>The sector the blast occurred in — lets the client render only
    /// blasts in the local ship's own sector (native's <c>GetClusterSite()==this</c>
    /// gate, <c>trekigc.cpp:84-88</c>).</summary>
    public short ClusterId;

    /// <summary>2 × the destroyed model's collision radius (native
    /// <c>trekigc.cpp:183</c>) — the audio-size tier (>100 large, &gt;10 medium,
    /// else small, <c>trekigc.cpp:94-104</c>); carried for fidelity.</summary>
    public float Scale;

    /// <summary>The blast centre in world units.</summary>
    public Vector3 Position;

    /// <summary>The emitter's world velocity, units/second — fireball drift.</summary>
    public Vector3 Velocity;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(Type);
        w.WriteI16(ClusterId);
        w.WriteZeros(2);            // pad to 4-byte alignment
        w.WriteU32(BitConverter.SingleToUInt32Bits(Scale));
        WriteVec(ref w, Position);
        WriteVec(ref w, Velocity);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPLOSION_EVENT internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExplosionEvent Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPLOSION_EVENT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPLOSION_EVENT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        int type = r.ReadI32();
        short cluster = r.ReadI16();
        r.Advance(2);                                   // pad
        float scale = BitConverter.UInt32BitsToSingle(r.ReadU32());
        var pos = ReadVec(ref r);
        var vel = ReadVec(ref r);
        return new ExplosionEvent { Type = type, ClusterId = cluster, Scale = scale, Position = pos, Velocity = vel };
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
