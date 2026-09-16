using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// A single live-missile state record inside <see cref="MissileUpdate"/> (205):
/// one per in-flight missile, carrying the id, type and cluster plus the
/// missile's <em>current</em> position and heading. Mirrors
/// <see cref="MissileLaunchData"/>'s wire-record style (a <c>readonly record
/// struct</c> with <c>Write</c>/<c>Read</c> taking <c>ref</c> cursors and a
/// <c>Size</c> constant).
/// <code>
///   offset  0: short  missileId
///   offset  2: short  typeId       (ExpendableTypeID = short, igc.h:497)
///   offset  4: short  clusterId    (SectorID = short, igc.h:487)
///   offset  6: pad 2               (align the Vectors to 4)
///   offset  8: Vector position     (12 bytes, 4-aligned)
///   offset 20: Vector forward      (12 bytes)
///   = 32 bytes total
/// </code>
/// </summary>
/// <remarks>
/// This is a <b>this-port-only</b> record — there is no native
/// <c>DEFINE_FEDMSG</c> element for it (native has no per-tick missile-position
/// message at all; see <see cref="MissileUpdate"/>'s class doc for why this
/// port adds one). Its layout is therefore our own, laid out under the same
/// natural-4-byte-alignment convention every other ported codec here uses.
/// </remarks>
public readonly record struct MissileStateRecord(
    short MissileId,
    short TypeId,
    short ClusterId,
    Vector3 Position,
    Vector3 Forward)
{
    /// <summary>Wire width of one record: 32 bytes = 6 B of ids/pads + 24 B of vectors.</summary>
    public const int Size = 32;

    /// <summary>Encode this record at the writer's current position, in declaration order.</summary>
    /// <remarks>Pass <paramref name="w"/> by <c>ref</c> so the caller's shared
    /// buffer position actually advances (same ref-struct-by-value trap
    /// <see cref="PartData.Write"/> documents).</remarks>
    public readonly void Write(ref WireWriter w)
    {
        w.WriteI16(MissileId);
        w.WriteI16(TypeId);
        w.WriteI16(ClusterId);
        w.WriteZeros(2);                        // pad: align Vectors to 4
        WriteVec(ref w, Position);
        WriteVec(ref w, Forward);
    }

    /// <summary>Read one record from the reader's current position, in declaration order.</summary>
    public static MissileStateRecord Read(ref WireReader r)
    {
        short id = r.ReadI16();
        short typeId = r.ReadI16();
        short clusterId = r.ReadI16();
        r.Advance(2);                           // pad
        var pos = ReadVec(ref r);
        var fwd = ReadVec(ref r);
        return new MissileStateRecord(id, typeId, clusterId, pos, fwd);
    }

    // The established 12-byte three-float LE encode/decode shared throughout
    // this project's wire codecs (same BitConverter triple-projectile technique
    // ProjectileInstance.cs:127-137 uses) — reused here, not reinvented.
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

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P234 — the per-tick,
/// same-side delivery of every in-flight missile's current position and heading
/// to the MonoGame client, so the renderer can place a real missile model at the
/// missile's actual location instead of extrapolating a straight line.
///
/// Why this message exists at all is an architecture decision documented in
/// <c>docs/MISSILE_RENDERING_P234.md</c>; the short version: native Allegiance
/// has <b>no</b> per-tick missile-position message — its client ran its own
/// local IGC simulation (including homing/turning), so the creation
/// <c>FM_CS_FIRE_MISSILE(70)</c> plus <c>FM_S_MISSILE_DESTROYED(83)</c> /
/// <c>FM_S_MISSILE_SPOOFED(110)</c> were all it needed. This port's MonoGame
/// client is a pure renderer of server-authoritative state and has no static
/// core to simulate homing with, yet the missiles P179/P181 make it home —
/// turning and accelerating every tick — so a straight-line extrapolation from
/// the launch velocity would visibly diverge. Rather than let the renderer
/// drift, the server streams the authoritative missile state once per tick on a
/// brand-new, clearly-labelled extension message (the same decision and FMID
/// placement as P230's <see cref="MissileLockUpdate"/>, 204).
///
/// FMID <b>205</b> sits above native's entire real FMID range (which tops out at
/// 200 in <c>src/Inc/Messages.h</c>), so it can never collide with a native
/// message this port might later add faithfully. It is the <em>ongoing
/// position</em> half of the missile story: creation identity + type arrives on
/// <see cref="FireMissile"/>(70) and destruction on <see cref="MissileDestroyed"/>
/// (83), while this message keeps a live missile's position/orientation current
/// every tick.
///
/// The layout is our own (there is no native <c>DEFINE_FEDMSG</c> to be
/// byte-faithful to), a variable-length <c>MissileStateRecord[]</c> array carried
/// behind the same FM_VAR_ITEM <c>ib/cb</c> descriptor convention FIRE_MISSILE
/// (70) uses for its <c>MissileLaunchData[]</c> tail:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 205
///   offset  4: IB ib (var descriptor)     = 8 (start of the array blob)
///   offset  6: CB cb (var descriptor)     = 32 · n
///   offset  8: MissileStateRecord[n]      each 32 B (see MissileStateRecord.Size)
///   = 8 + 32·n B message total
/// </code>
/// </summary>
public sealed class MissileUpdate : IFedMessage
{
    public const ushort Fmid = 205;

    /// <summary>Fixed region width: header(4) + var descriptor(4) = 8; the
    /// records follow in the var-item blob.</summary>
    public const int FixedSize = 8;

    /// <summary>The live missile states carried by this update, in no particular
    /// order. May be empty (cb = 0, no blob — a tick with no in-flight missiles).</summary>
    public IReadOnlyList<MissileStateRecord> Records { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int n = Records.Count;
        int total = FixedSize + n * MissileStateRecord.Size;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Position; w.Advance(2);   // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)
        if (w.Position != FixedSize) throw new InvalidOperationException($"MISSILE_UPDATE fixed-region drift: {w.Position} != {FixedSize}");

        int blobStart = w.Position;
        foreach (var r in Records) r.Write(ref w);
        int blobEnd = w.Position;
        if (blobEnd - blobStart != n * MissileStateRecord.Size)
            throw new InvalidOperationException($"MISSILE_UPDATE array drift: {blobEnd - blobStart} != {n} × {MissileStateRecord.Size}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"MISSILE_UPDATE layout drift: {w.Position} != {total}");
        return buf;
    }

    public static MissileUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MISSILE_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSILE_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb % MissileStateRecord.Size != 0)
            throw new ArgumentException($"missileState cb={vi.Cb} is not a whole multiple of MissileStateRecord({MissileStateRecord.Size})");
        int n = vi.Cb / MissileStateRecord.Size;

        WireReader r = new(buf);
        r.Advance(FixedSize);                       // past header + var descriptor
        var records = new List<MissileStateRecord>(n);
        for (int i = 0; i < n; i++) records.Add(MissileStateRecord.Read(ref r));

        return new MissileUpdate { Records = records };
    }
}
