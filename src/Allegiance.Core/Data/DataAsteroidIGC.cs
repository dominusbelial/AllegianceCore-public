using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataAsteroidIGC</c> payload for <c>OT_asteroid</c> (5). Fixed 144 bytes.
/// Source layout: <c>DataAsteroidIGC</c> (<c>src/Igc/igc.h:1891</c>) embedding
/// <c>AsteroidDef</c> (<c>src/Igc/igc.h:1709</c>), C++ default alignment. See
/// wiki page <c>allegiance-igc-file-format</c> §OT_asteroid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Corpus-measured correction:</b> the shipped files place <c>clusterID</c>
/// at <b>116</b>, <c>name</c> at <b>118</b> and <c>fraction</c> at <b>143</b>,
/// with <b>three uninterpreted bytes at 113-115</b> sitting between
/// <c>iconName</c> (ending at 112) and <c>clusterID</c>. The current
/// <c>igc.h</c> struct would put clusterID at 114 (1 pad at 113) and name at
/// 115; the on-disk layout is two bytes later, so the shipped maps were
/// written by an older revision whose struct carried three extra bytes here.
/// Verified across all 973 records: every <c>clusterID</c> at 116 names a real
/// sector in the same file, so the offset is certain despite the gap. The
/// 113-115 bytes are overwhelmingly uninitialized (<c>0xCC</c> debug fill or
/// zero) and are deliberately <b>not</b> exposed as a field.
/// </para>
/// <para>The fixed-size 144 offsets are otherwise padding-free: <c>ore</c>,
/// <c>oreMax</c>, <c>hitPoints</c> are 4-aligned, the three 13-char names are
/// byte-aligned, and the 25-char name + fraction tail is byte-aligned.</para>
/// </remarks>
public readonly record struct DataAsteroidIGC(
    float Signature,
    float[] Position,
    float[] Up,
    float[] Forward,
    float[] Rotation,
    float Ore,
    float OreMax,
    short AabmCapabilities,
    short AsteroidId,
    float HitPoints,
    short Radius,
    string ModelName,
    string TextureName,
    string IconName,
    short ClusterId,
    string Name,
    byte Fraction)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataAsteroidIGC)</c>.</summary>
    public const int Size = 144;

    /// <summary><c>c_aabmMineHe3 = 0x01</c> (<c>src/Igc/igc.h:761</c>) — has
    /// minable helium-3 ore.</summary>
    public const short AabmMineHe3 = 0x01;
    /// <summary><c>c_aabmMineLotsHe3 = 0x02</c> (<c>src/Igc/igc.h:762</c>) —
    /// has a dense vein of helium-3 ore.</summary>
    public const short AabmMineLotsHe3 = 0x02;
    /// <summary><c>c_aabmMineGold = 0x04</c> (<c>src/Igc/igc.h:763</c>) —
    /// has minable gold ore.</summary>
    public const short AabmMineGold = 0x04;
    /// <summary><c>c_aabmBuildable = 0x08</c> (<c>src/Igc/igc.h:765</c>) — a
    /// station can be built on this rock (Garrison/Shipyard/etc.).</summary>
    public const short AabmBuildable = 0x08;
    /// <summary><c>c_aabmSpecial = 0x10</c> (<c>src/Igc/igc.h:766</c>) — a
    /// special building can be built here; the subtype is the shift
    /// <c>c_aabmSpecial &lt;&lt; n</c>: &lt;&lt;0 uranium (Expansion), &lt;&lt;1
    /// silicon (Tactical), &lt;&lt;2 carbon (Supremacy).</summary>
    public const short AabmSpecial = 0x10;

    /// <summary>
    /// <c>IasteroidIGC::HasCapability(aabm)</c>
    /// (<c>src/Igc/asteroidigc.h:259-262</c>:
    /// <c>return m_pbuildingEffect ? false : ((m_asteroidDef.aabmCapabilities
    /// &amp; aabm) != 0)</c>) — whether this asteroid's buildability/mine
    /// capability mask satisfies <paramref name="aabm"/>. P280 reads it (via
    /// <see cref="DataStationTypeIGC.AabmBuild"/>) to enforce native's rule
    /// that a techbase can only be constructed on the matching asteroid type
    /// (<c>shipIGC.h:1695-1707</c>). The <c>m_pbuildingEffect</c> exclusion —
    /// a rock already hosting a build is not buildable — is out of scope (the
    /// port's <c>Sector</c> tracks in-progress builds separately).
    /// </summary>
    public readonly bool HasCapability(short aabm) => (AabmCapabilities & aabm) != 0;

    /// <summary>
    /// Decode an <c>OT_asteroid</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataAsteroidIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataAsteroidIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));
        static float[] Vec(ReadOnlySpan<byte> s, int off) =>
        [
            BinaryPrimitives.ReadSingleLittleEndian(s[off..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 8)..]),
        ];

        return new DataAsteroidIGC(
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[0..]),
            Position: Vec(b, 4),
            Up: Vec(b, 16),
            Forward: Vec(b, 28),
            Rotation:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[44..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            ],
            Ore: BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            OreMax: BinaryPrimitives.ReadSingleLittleEndian(b[60..]),
            AabmCapabilities: BinaryPrimitives.ReadInt16LittleEndian(b[64..]),
            AsteroidId: BinaryPrimitives.ReadInt16LittleEndian(b[66..]),
            HitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[68..]),
            Radius: BinaryPrimitives.ReadInt16LittleEndian(b[72..]),
            ModelName: Fix(b, 74, 13),
            TextureName: Fix(b, 87, 13),
            IconName: Fix(b, 100, 13),
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[116..]),
            Name: Fix(b, 118, 25),
            Fraction: b[143]);
    }

    /// <summary>
    /// Encode an <c>OT_asteroid</c> payload — the inverse of
    /// <see cref="Decode"/> — producing exactly <see cref="Size"/> bytes.
    /// Field layout mirrors <c>Decode</c> verbatim (offsets 0-143), including
    /// the corpus-measured 113-115 gap: those three uninterpreted bytes
    /// between <c>iconName</c> (ending at 112) and <c>clusterID</c> (at 116)
    /// are written as zeros, matching the dominant on-disk content for the
    /// gap (<c>0x00</c>/<c>0xCC</c>) and because <c>Decode</c> does not expose
    /// them as a field they neither participate in nor break round-trips.
    /// Everything else is raw passthrough — there is no always-false-on-export
    /// field (contrast <c>DataClusterIGC.activeF</c>), mirroring
    /// <see cref="DataStationIGC.Encode"/>.
    /// <list type="number">
    /// <item><b>Numeric fields pass through raw.</b> the four geometry groups
    /// (three triples + a quad), the three 4-byte scalars, the three 2-byte
    /// shorts and the final fraction byte are written exactly as held.</item>
    /// <item><b>String fields (13/13/13/25) are zero-padded, not NUL-forced</b>
    /// — fixed <c>char[]</c> buffers, exactly as
    /// <see cref="DataClusterIGC.Encode"/> and <see cref="DataStationIGC.Encode"/>
    /// write theirs. <see cref="Decode"/> trims via <c>CutAtNull</c>, so
    /// round-trips hold for names shorter than their buffer.</item>
    /// </list>
    /// The resulting bytes satisfy <c>Decode(Encode(this)) == this</c> for
    /// names shorter than their buffer (the sole asymmetry: a full-length name
    /// cannot round-trip, matching <see cref="Decode"/>'s trim semantics).
    /// </summary>
    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), Signature);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4 + 4 * i),
                Position is { Length: 3 } p ? p[i] : 0f);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16 + 4 * i),
                Up is { Length: 3 } u ? u[i] : 0f);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28 + 4 * i),
                Forward is { Length: 3 } f ? f[i] : 0f);
        }
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(40 + 4 * i),
                Rotation is { Length: 4 } r ? r[i] : 0f);

        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(56), Ore);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(60), OreMax);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(64), AabmCapabilities);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(66), AsteroidId);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(68), HitPoints);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(72), Radius);

        WriteFixed(b, ModelName, 74, 13);
        WriteFixed(b, TextureName, 87, 13);
        WriteFixed(b, IconName, 100, 13);
        // 113-115: the corpus-measured uninterpreted gap, zero-filled.
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(116), ClusterId);
        WriteFixed(b, Name, 118, 25);
        b[143] = Fraction;

        return b;

        static void WriteFixed(byte[] buf, string? s, int off, int len)
        {
            int n = Math.Min(s?.Length ?? 0, len);
            if (n > 0) Encoding.ASCII.GetBytes(s![..n]).CopyTo(buf, off);
            // buf[off+n..off+len] remain zero (zero-pad, no forced NUL)
        }
    }
}
