using System.Buffers.Binary;
using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P266 — the
/// per-tick, owner-only delivery of the predictive LEAD AIM indicator: where
/// the local ship's selected moving target will be when a round fired at the
/// mounted weapon's projectile speed intercepts it (the "lead point"), plus
/// the two decisions the renderer needs — whether the intercept is within the
/// weapon's lifespan (in range) and the green→red hit-probability.
///
/// Why this message exists at all is the same architecture reality as P230's
/// <see cref="MissileLockUpdate"/>: native's client ran its own local IGC
/// simulation, so it called <c>solveForLead</c> (<c>Igc/common.cpp:121-149</c>)
/// itself and drew the reticle locally (<c>WinTrek/indicator.cpp:199-360</c>).
/// This port's MonoGame client is a pure renderer of server-authoritative state
/// with <b>no</b> static core, so it has neither the shooter's/target's live
/// velocities nor the projectile's speed to solve the quadratic intercept. The
/// server holds all of it — <see cref="Sim.Ship.SolveLead"/> (P169's faithful
/// port) reads the real <c>Ship.CommandTarget</c> (c_cmdCurrent), the mounted
/// weapon's <see cref="Sim.DataProjectileTypeIGC.Speed"/>/<c>AbsoluteF</c> and
/// both ships' velocities — so it computes the lead and streams it, following
/// the exact owner-only per-tick convention P230/P255/P264 established
/// (204/206/208).
///
/// The hull-capability gate (<c>c_habmLeadIndicator = 0x800</c>) and the
/// friendly-relay scanner exception (<c>c_sabmRemoteLeadIndicator</c> /
/// <c>c_habmRemoteLeadIndicator</c>) are applied SERVER-side, matching native's
/// client-side test (<c>indicator.cpp:254-315</c>) — a peer simply receives
/// <c>TargetShipId = NA</c> when the indicator is not granted.
///
/// FMID <b>210</b> sits above native's entire real FMID range (max 200 in
/// <c>src/Inc/Messages.h</c>) and above this port's prior extensions (204-209),
/// so it can never collide. Layout (our own, no native DEFINE_FEDMSG to be
/// byte-faithful to):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 210
///   offset  4: short  shipID           (the indicator owner — the local player)
///   offset  6: short  targetShipID     (NA / -1 when there is no lead indicator)
///   offset  8: byte   inRange          (1 = intercept within the weapon lifespan)
///   offset  9: byte   green            (div-240 of the hit-probability g, 0..240)
///   offset 10: [12]   leadWorldPos     (Vector3 raw floats; the predicted lead point)
///   offset 22: [12]   targetPos        (Vector3 raw floats; the target's current position)
///   = 34 bytes fixed
/// </code>
/// Round-trip exactness is by construction (the same byte[]-backed convention as
/// <see cref="MissileLockUpdate"/>): the object stores the canonical 34 bytes and
/// every rich accessor re-derives from those bytes.
/// </summary>
public sealed class LeadIndicatorUpdate : IFedMessage
{
    /// <summary>This-port-only FMID, above native's real range (max 200).</summary>
    public const ushort Fmid = 210;

    /// <summary>Wire width, bytes.</summary>
    public const int Width = 34;

    private readonly byte[] _b = new byte[Width];

    private LeadIndicatorUpdate() { }

    /// <summary>The indicator owner (always the recipient — the local player).</summary>
    public short ShipId => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(4));

    /// <summary>The lead target's ship id; <c>-1</c> (<c>NA</c>) when no lead
    /// indicator is currently granted (no command target, no weapon, or the
    /// hull/relay gate failed).</summary>
    public short TargetShipId => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(6));

    /// <summary>True iff a lead indicator is currently shown
    /// (<see cref="TargetShipId"/> is not <c>NA</c>).</summary>
    public bool HasLead => TargetShipId != -1;

    /// <summary>True when the solved intercept is within the weapon's lifespan —
    /// native's <c>bIisInRange = (t &lt;= l)</c> (<c>indicator.cpp:224</c>), which
    /// swaps the in-range/out-of-range reticle surface. Out-of-range is drawn
    /// grey/dimmed.</summary>
    public bool InRange => _b[8] != 0;

    /// <summary>Raw div-240 percentage of the green→red hit-probability
    /// (<c>g</c>).</summary>
    public byte GreenPercent => _b[9];

    /// <summary>The hit-probability <c>g ∈ [0,1]</c>; render colour
    /// <c>(1-g, g, 0)</c>.</summary>
    public float Green => GreenPercent / 240f;

    /// <summary>The predicted lead WORLD point: the position along the solved aim
    /// direction at the target's range where the reticle is drawn
    /// (<c>shooter.Position + direction * dist(shooter, target)</c>), i.e. the
    /// screen-space home of the predictive lead — genuinely offset from the
    /// target's own centre by the lateral lead angle.</summary>
    public Vector3 LeadWorldPos => new(
        BitConverter.ToSingle(_b, 10),
        BitConverter.ToSingle(_b, 14),
        BitConverter.ToSingle(_b, 18));

    /// <summary>The target's CURRENT world position (server-authoritative). Carried
    /// because cross-side ships are not broadcast to this peer (no visibility
    /// coverage), so the client needs it both to project the target marker and to
    /// quantify the on-screen offset from lead point to target centre. Meaningful
    /// only when <see cref="HasLead"/>; otherwise zero.</summary>
    public Vector3 TargetPos => new(
        BitConverter.ToSingle(_b, 22),
        BitConverter.ToSingle(_b, 26),
        BitConverter.ToSingle(_b, 30));

    /// <summary>Build the message from the server-computed lead state.
    /// <paramref name="targetShipId"/>=−1 and zero vectors express "no lead".
    /// <paramref name="green01"/> (0..1) is quantized to the div-240 wire byte.</summary>
    public static LeadIndicatorUpdate From(
        short shipId, short targetShipId, bool inRange, float green01,
        Vector3 leadWorldPos, Vector3 targetPos)
    {
        var u = new LeadIndicatorUpdate();
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(4), shipId);
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(6), targetShipId);
        u._b[8] = inRange ? (byte)1 : (byte)0;
        u._b[9] = CompactQuant.Percent(Math.Clamp(green01, 0f, 1f));
        WriteVec(u._b, 10, leadWorldPos);
        WriteVec(u._b, 22, targetPos);
        return u;
    }

    private static void WriteVec(byte[] dst, int off, Vector3 v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(off + 0), BitConverter.SingleToInt32Bits(v.X));
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(off + 4), BitConverter.SingleToInt32Bits(v.Y));
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(off + 8), BitConverter.SingleToInt32Bits(v.Z));
    }

    /// <summary>Encode the 34 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 34-byte body from the reader's current position.</summary>
    public static LeadIndicatorUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"LeadIndicatorUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new LeadIndicatorUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[Width];
        WireWriter w = new(buf);
        w.WriteU16(Width);          // cbmsg
        w.WriteU16(Fmid);
        w.WriteBytes(_b.AsSpan(4)); // body (offsets 4..34)
        return buf;
    }

    /// <summary>Decode a whole message (header + body) from raw bytes, validating
    /// the fmid and length like the other codecs.</summary>
    public static LeadIndicatorUpdate Decode(ReadOnlySpan<byte> m)
    {
        if (m.Length < Width)
            throw new ArgumentException($"LeadIndicatorUpdate needs {Width} bytes, got {m.Length}");
        var hdr = FedMessageHeader.Read(m);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LeadIndicatorUpdate fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != m.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {m.Length}");
        var u = new LeadIndicatorUpdate();
        m.Slice(4, Width - 4).CopyTo(u._b.AsSpan(4));
        return u;
    }
}
