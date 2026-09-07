using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>MissileLaunchData</c> (<c>Messages.h:539-544</c>), the per-missile entry
/// in <c>FM_CS_FIRE_MISSILE</c>(70)'s variable-length <c>missileLaunchData</c>
/// array — one entry per missile in a salvo, each with its own slightly
/// dispersed <c>vecPosition</c>/<c>vecForward</c> and a shared inherited
/// <c>vecVelocity</c>. Mirrors <see cref="PartData"/>'s wire-type style
/// (a <c>readonly record struct</c> with <c>Write</c>/<c>Read</c> taking
/// <c>ref</c> cursors and a <c>Size</c> constant).
/// <code>
///   offset  0: Vector vecPosition   (12 bytes, 4-aligned throughout)
///   offset 12: Vector vecForward    (12 bytes)
///   offset 24: Vector vecVelocity   (12 bytes)
///   offset 36: short  missileID     (MissileID = ObjectID = short, igc.h:477,493)
///   offset 38: pad   (2 bytes)      — see Size/padding note below
///   = 40 bytes total
/// </code>
/// </summary>
/// <remarks>
/// <b>Trailing-padding resolution.</b> Logically the struct is 38 bytes
/// (3 × 12 = 36 + one 2-byte <c>short</c> = 38). But <c>MissileLaunchData</c>
/// is only ever carried as the elements of an <b>array</b> (the
/// <c>missileLaunchData[]</c> var-item blob), and every consumer derives the
/// element count by dividing the blob <c>cb</c> by
/// <c>sizeof(MissileLaunchData)</c>: the server at <c>FedSrv.CPP:5447</c>
/// (<c>cbmissileLaunchData / sizeof(MissileLaunchData)</c>) and the client at
/// <c>appmsg.cpp:1611</c>. Under MSVC's default (no <c>#pragma pack</c>)
/// alignment, a struct's <c>sizeof</c> is rounded up to a multiple of its
/// <i>largest</i> member's alignment — here 4 (from the <c>Vector</c> floats,
/// and from no wider member) — so <c>sizeof(MissileLaunchData) = 40</c>, with
/// 2 trailing pad bytes after <c>missileID</c> giving each array element its
/// correct 4-byte-aligned stride. This is the same trailing-pad-to-struct-
/// alignment model already documented in <c>DataStationIGC.cs:13-20</c>
/// ("the only wasted byte is the trailing pad at 87 rounding the struct to
/// 88"); applying it here yields <see cref="Size"/> = 40.
/// </remarks>
public readonly record struct MissileLaunchData(
    Vector3 VecPosition,
    Vector3 VecForward,
    Vector3 VecVelocity,
    short MissileId)
{
    /// <summary>
    /// Wire width of one entry: 40 bytes = 36 B of fields + 2 trailing pad
    /// bytes (see the padding note in the type doc comment). This is the
    /// divisor every native consumer uses to count array entries.
    /// </summary>
    public const int Size = 40;

    /// <summary>Encode this entry at the writer's current position, in declaration order.</summary>
    /// <remarks>Pass <paramref name="w"/> by <c>ref</c> so the caller's shared
    /// buffer position actually advances (same ref-struct-by-value trap
    /// <see cref="PartData.Write"/> documents).</remarks>
    public readonly void Write(ref WireWriter w)
    {
        WriteVec(ref w, VecPosition);
        WriteVec(ref w, VecForward);
        WriteVec(ref w, VecVelocity);
        w.WriteI16(MissileId);
        w.WriteZeros(2);            // trailing pad to the 4-aligned 40-byte element stride
    }

    /// <summary>Read one entry from the reader's current position, in declaration order.</summary>
    public static MissileLaunchData Read(ref WireReader r)
    {
        var pos = ReadVec(ref r);
        var fwd = ReadVec(ref r);
        var vel = ReadVec(ref r);
        short id = r.ReadI16();
        r.Advance(2);               // trailing pad
        return new MissileLaunchData(pos, fwd, vel, id);
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
