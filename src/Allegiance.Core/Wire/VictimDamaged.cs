using System;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P623 — the server
/// telling a VICTIM's own client that its ship just took damage from a
/// ship-launched round, carrying the attacker's ship id and the damage amount.
/// This is the wire analogue of native's same-process DamageBucket population:
/// in native the client simulates its own ship, so the client's copy of the
/// ship knows exactly who damaged it and by how much (<c>DamageTrack::ApplyDamage</c>,
/// <c>src/Igc/common.cpp:2592-2617</c>). Here the sim is server-authoritative
/// and the client is a pure renderer, so the same datum crosses the wire as a
/// targeted unicast to the struck ship's OWN peer — feeding the client's
/// TK_TargetHostile rollings-window tracker (Game1.Targeting.cs, P623), the
/// port of native's <c>WinTrek.cpp:10101-10124</c> "target the thing that has
/// done the most damage to me recently".
/// </summary>
/// <remarks>
/// <para><b>Audience (very different from the other damage messages).</b>
/// Unlike <see cref="ProjectileHullHit"/> (broadcast to the shooter's side +
/// cross-side viewers) and <see cref="CollisionEvent"/>, this message is a
/// single-peer unicast to the VICTIM only — the one client that needs to know
/// who is hurting it. It carries no impact position/normal (those live on the
/// projective-hit spark), only the attribution: attacker id + damage. This
/// keeps the hostile feature zero-cost to every other client and avoids
/// leaking who-hit-who beyond the participant.</para>
/// <para><b>Damage amount.</b> The server passes the actual hull/shield damage
/// the attacker's round dealt (the same <c>DamageResult</c>-gated amount that
/// keys native's bucket), so the client's window ranks damagers by severity —
/// matching native's sort by <c>totalDamage</c>.</para>
/// <para><b>Layout.</b> Our own (no native <c>DEFINE_FEDMSG</c> to be faithful
/// to), fixed-size under the project's natural-4-byte-alignment convention:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 217
///   offset  4: short  attackerId  (the ship id that FIRED the damaging round;
///                                  -1 = not a ship)
///   offset  6: short  pad (alignment)
///   offset  8: float  damage      (hull/shield damage dealt to the victim)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// FMID <b>217</b> sits above native's entire real FMID range (which tops out
/// at 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a native
/// message this port might later add faithfully — the same convention as the
/// other this-port-only FMIDs (213-216).
/// </para>
/// </remarks>
public sealed class VictimDamaged : IFedMessage
{
    public const ushort Fmid = 217;

    /// <summary>Wire width: header(4) + attackerId(2) + pad(2) + damage(4) = 12 B.</summary>
    public const int FixedSize = 12;

    /// <summary>The ship id that fired the damaging round (the attacker /
    /// launcher), or -1 when the round came from a non-ship source.</summary>
    public short AttackerId;

    /// <summary>The hull/shield damage the attacker's round dealt to the local
    /// ship (drives the client's severity-ranked hostile window).</summary>
    public float Damage;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(AttackerId);
        w.WriteU16(0);              // pad
        w.WriteU32(BitConverter.SingleToUInt32Bits(Damage));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"VICTIM_DAMAGED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static VictimDamaged Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"VICTIM_DAMAGED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected VICTIM_DAMAGED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        short attacker = r.ReadI16();
        r.Advance(2);                                   // pad
        float damage = BitConverter.UInt32BitsToSingle(r.ReadU32());
        return new VictimDamaged { AttackerId = attacker, Damage = damage };
    }
}
