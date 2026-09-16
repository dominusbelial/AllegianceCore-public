using System.Buffers.Binary;
using System.Text;
using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P230 — the
/// per-tick, owner-only delivery of a live player's missile-lock quality and
/// lock target to the MonoGame client, so the client can render the three
/// real lock-feedback channels (the <c>missileTone</c> looping beep at full
/// lock, the partial/full/lost console tips, and the target-box overlay).
///
/// Why this message exists at all is an architecture decision documented in
/// <c>docs/MISSILE_LOCK_FEEDBACK_P230.md</c>; the short version: native's
/// client ran its own local IGC simulation, so it could read
/// <c>magazine-&gt;GetLock()</c> directly with zero wire traffic. This port's
/// MonoGame client is a pure renderer of server-authoritative state and has
/// <b>no</b> access to the mission's static core (its <c>MissionInfo</c> holds
/// only the core <i>filename</i>, and the client references only the wire
/// codecs), so <c>Ship.MissileLock</c> — computed server-side per tick by P226 —
/// is simply not visible to it. Rather than widen the native-faithful 47-byte
/// <see cref="ServerShipUpdate"/> (which would silently diverge a byte-for-byte
/// port of native's real <c>ServerSingleShipUpdate</c>, <c>igc.h:2444</c>), we
/// carry the lock state on a brand-new, clearly-labelled extension message that
/// the server sends <b>only to the owning player</b> (following the existing
/// per-owner convention where each peer's own update goes only to that peer),
/// and that carries everything the feedback needs: the lock fraction, the
/// target ship's identity <i>and</i> world position (so the client can draw the
/// bracket even though it never renders enemy ships), and the missile type's
/// display name (for the "acquired" tip text — the client has no static core to
/// look it up from).
///
/// FMID <b>204</b> sits above native's entire real FMID range (which tops out at
/// 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a native
/// message this port might later add faithfully. The layout is our own (there is
/// no native <c>DEFINE_FEDMSG</c> to be byte-faithful to):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 204
///   offset  4: short  shipID           (the lock owner — the local player)
///   offset  6: short  targetShipID     (NA / -1 when there is no lock target)
///   offset  8: byte   lockPercent      (div-240 of MissileLock, 0..240)
///   offset  9: byte   reserved         (=0)
///   offset 10: [12]   targetPosition   (Vector3 raw floats; meaningful iff targetShipID != NA)
///   offset 22: [13]   missileTypeName  (fixed ASCII, NUL-padded)
///   = 35 bytes fixed
/// </code>
/// Round-trip exactness is by construction (the same convention as
/// <see cref="ServerShipUpdate"/>): the object stores the canonical 35 bytes
/// (from <see cref="From"/> or the wire) and every rich accessor re-derives
/// from those bytes, so <c>DecodeBody(EncodeBody(x))</c> is a fixed point.
/// </summary>
public sealed class MissileLockUpdate : IFedMessage
{
    /// <summary>This-port-only FMID, above native's real range (max 200).</summary>
    public const ushort Fmid = 204;

    /// <summary>Wire width, bytes.</summary>
    public const int Width = 35;

    private readonly byte[] _b = new byte[Width];

    private MissileLockUpdate() { }

    /// <summary>The locking ship (always the recipient — the owner).</summary>
    public short ShipId => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(4));

    /// <summary>The lock target's ship id; <c>-1</c> (<c>NA</c>) when there is no
    /// current lock target.</summary>
    public short TargetShipId => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(6));

    /// <summary>True iff a lock target is currently resolved (<see cref="TargetShipId"/>
    /// is not <c>NA</c>), which is when a target-box may be drawn.</summary>
    public bool HasTarget => TargetShipId != -1;

    /// <summary>Raw div-240 percentage of the lock quality.</summary>
    public byte LockPercent => _b[8];

    /// <summary>The lock quality 0..1 (full lock == 1).</summary>
    public float LockFraction => LockPercent / 240f;

    /// <summary>The lock target's world position (raw floats). Meaningful only
    /// when <see cref="HasTarget"/>; otherwise zero.</summary>
    public Vector3 TargetPosition => new(
        BitConverter.ToSingle(_b, 10),
        BitConverter.ToSingle(_b, 14),
        BitConverter.ToSingle(_b, 18));

    /// <summary>The mounted missile type's display name (the <c>DataObjectIGC</c>
    /// <c>name[13]</c> of <c>DataMissileTypeIGC</c>), for the "acquired" tip. Empty
    /// when no launcher is mounted / no type resolved.</summary>
    public string MissileTypeName
    {
        get
        {
            int len = Array.IndexOf(_b, (byte)0, 22, 13);
            if (len < 0) len = 22 + 13;
            return Encoding.ASCII.GetString(_b, 22, len - 22);
        }
    }

    /// <summary>Build the message from a live lock state. <paramref name="lockFraction"/>
    /// is the 0..1 lock quality; <paramref name="missileTypeName"/> is truncated to
    /// the 13-byte wire field. Passing <c>targetShipId = -1</c> and the zero vector
    /// expresses "no lock target".</summary>
    public static MissileLockUpdate From(
        short shipId, short targetShipId, float lockFraction,
        Vector3 targetPosition, string missileTypeName)
    {
        var u = new MissileLockUpdate();
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(4), shipId);
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(6), targetShipId);
        u._b[8] = CompactQuant.Percent(Math.Clamp(lockFraction, 0f, 1f));
        u._b[9] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(u._b.AsSpan(10), BitConverter.SingleToInt32Bits(targetPosition.X));
        BinaryPrimitives.WriteInt32LittleEndian(u._b.AsSpan(14), BitConverter.SingleToInt32Bits(targetPosition.Y));
        BinaryPrimitives.WriteInt32LittleEndian(u._b.AsSpan(18), BitConverter.SingleToInt32Bits(targetPosition.Z));
        WriteName(u._b, 22, missileTypeName);
        return u;
    }

    /// <summary>Encode the 35 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 35-byte body from the reader's current position.</summary>
    public static MissileLockUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"MissileLockUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new MissileLockUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[Width];
        WireWriter w = new(buf);
        w.WriteU16(Width);       // cbmsg
        w.WriteU16(Fmid);
        w.WriteBytes(_b.AsSpan(4));   // body (offsets 4..35)
        return buf;
    }

    /// <summary>Decode a whole message (header + body) from raw bytes, validating
    /// the fmid and length like the other codecs.</summary>
    public static MissileLockUpdate Decode(ReadOnlySpan<byte> m)
    {
        if (m.Length < Width)
            throw new ArgumentException($"MissileLockUpdate needs {Width} bytes, got {m.Length}");
        var hdr = FedMessageHeader.Read(m);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MissileLockUpdate fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != m.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {m.Length}");
        var u = new MissileLockUpdate();
        m.Slice(4, Width - 4).CopyTo(u._b.AsSpan(4));
        return u;
    }

    private static void WriteName(byte[] dst, int off, string name)
    {
        if (string.IsNullOrEmpty(name)) return;   // already NUL-filled
        int n = Math.Min(name.Length, 13);
        Encoding.ASCII.GetBytes(name, 0, n, dst, off);
        // remainder stays NUL (fresh buffer)
    }
}
