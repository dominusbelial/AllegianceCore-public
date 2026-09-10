using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// The compact quantization rules shared by the ship-update payloads. Each
/// helper reproduces the C++ store/retrieve pair exactly (source field
/// references in the XML doc for each). Wire widths are fixed by
/// <c>#pragma pack(push,1)</c> in <c>igc.h</c> (no padding) and by the C++
/// struct field order.
/// </summary>
internal static class CompactQuant
{
    // ── BytePercentage (Utility.h:1307) ───────────────────────────────────
    // Divisor = 240 (Utility.h:1312), not 255. Store clamps to 0..240; the
    // div-240 retrieve yields the fraction.

    /// <summary>Fraction 0..1 → 0..240. Clamps like <c>BytePercentage::operator=</c> (Utility.h:1342-1358).</summary>
    public static byte Percent(float f)
    {
        double v = (double)f * 240.0 + 0.5;
        if (v < 0.0) return 0;
        if (v >= 241.0) return 240;
        return (byte)v;
    }

    /// <summary>Byte 0..240 → fraction 0..1 (div-240 retrieve).</summary>
    public static float DePercent(byte p) => p / 240.0f;

    // ── CompactControlData (igc.h:2333) ───────────────────────────────────
    // Per axis: char = floor(v*127 + 0.5); retrieve char/127.

    public static byte ControlAxis(float v)
    {
        int x = (int)MathF.Floor(v * 127.0f + 0.5f);
        x = Math.Clamp(x, -128, 127);
        return unchecked((byte)x);          // two's complement on the wire
    }

    public static float DeControlAxis(byte b) => unchecked((sbyte)b) / 127.0f;

    // ── CompactTurnRates (igc.h:2314) ─────────────────────────────────────
    // Per axis: char = floor(rate*127/(2π) + 0.5); retrieve char*(2π)/127.

    public static byte TurnRate(float radPerSec)
    {
        int x = (int)MathF.Floor(radPerSec * 127.0f / (2.0f * MathF.PI) + 0.5f);
        x = Math.Clamp(x, -128, 127);
        return unchecked((byte)x);
    }

    public static float DeTurnRate(byte b) => unchecked((sbyte)b) * (2.0f * MathF.PI) / 127.0f;

    // ── CompactVelocity (igc.h:2275) ──────────────────────────────────────
    // 5 bytes: ushort speed/flags + 3 component BytePercentages.

    /// <summary>Encode a world velocity into the 5-byte CompactVelocity slot.</summary>
    public static void EncodeVelocity(Vector3 v, Span<byte> dst)
    {
        float speed = v.Length();
        ushort sp;
        if (speed < 1.0f)
        {
            sp = 0;
            dst[2] = dst[3] = dst[4] = 0;
        }
        else
        {
            sp = (ushort)(((ushort)(speed * 8.0f))
               | (v.X >= 0.0f ? (ushort)0 : (ushort)0x2000)
               | (v.Y >= 0.0f ? (ushort)0 : (ushort)0x4000)
               | (v.Z >= 0.0f ? (ushort)0 : (ushort)0x8000));
            float inv = 1.0f / speed;
            dst[2] = Percent(MathF.Abs(v.X) * inv);
            dst[3] = Percent(MathF.Abs(v.Y) * inv);
            dst[4] = Percent(MathF.Abs(v.Z) * inv);
        }
        // ushort little-endian
        dst[0] = (byte)(sp & 0xFF);
        dst[1] = (byte)((sp >> 8) & 0xFF);
    }

    /// <summary>Decode a 5-byte CompactVelocity slot back to a world velocity.</summary>
    public static Vector3 DecodeVelocity(ReadOnlySpan<byte> b)
    {
        ushort sp = (ushort)(b[0] | (b[1] << 8));
        float speed = 0.125f * (float)(sp & 0x1fff);
        float sx = (sp & 0x2000) != 0 ? -speed : speed;
        float sy = (sp & 0x4000) != 0 ? -speed : speed;
        float sz = (sp & 0x8000) != 0 ? -speed : speed;
        return new Vector3(
            DePercent(b[2]) * sx,
            DePercent(b[3]) * sy,
            DePercent(b[4]) * sz);
    }

    // ── CompactOrientation (igc.h:2137) ───────────────────────────────────
    // 8 bytes: ushort qa, short qx, qy, qz. Backed by Quaternion
    // (zlib/quaternion.cpp). Helper methods live in CompactOrientationCodec.

    public static void WriteShort(Span<byte> dst, int off, short v)
    {
        dst[off] = (byte)(v & 0xFF);
        dst[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    // ── CompactTime (igc.h:2178) ─────────────────────────────────────────
    // 2 bytes: short delta = timeBase.clock() - timeNow.clock(); Export:
    // timeNow = timeBase.clock() - offset. Signed delta means a short covers
    // ±32767 clock units (±~5.5 min at the 100 Hz clock) around the reference.
    // Used only by the ServerHeavyShipUpdate payload (HEAVY_SHIPS_UPDATE 176).

    /// <summary>Offset from a reference time to the update's time (short =
    /// timeBase − timeNow), mirroring <c>CompactTime::Set</c> (igc.h:2184-2190).</summary>
    public static short EncodeTimeOffset(uint timeBase, uint timeNow) =>
        unchecked((short)((long)timeBase - (long)timeNow));

    /// <summary>Recover the update's time from the reference plus the signed
    /// offset, mirroring <c>CompactTime::Export</c> (igc.h:2192-2195).</summary>
    public static uint DecodeTimeOffset(uint timeBase, short offset) =>
        unchecked((uint)((long)timeBase - (long)offset));

    // ── CompactPosition (igc.h:2198) ─────────────────────────────────────
    // 6 bytes: three short deltas relative to a positionReference, each stored
    // under the nonlinear signed-sqrt mapping Displacement(delta)=delta²/±120²
    // (igc.h:2203-2240). The inversion gives ~0.5-unit accuracy out to 1000
    // units from the reference. Used only by the ServerHeavyShipUpdate payload.

    private static float Displacement(short delta)
    {
        float f = delta;
        return f * f / ((f > 0.0f) ? (120.0f * 120.0f) : -(120.0f * 120.0f));
    }

    private static short Delta(float displacement)
    {
        double sad = Math.Sqrt(Math.Abs(displacement));
        if (sad > 0x7fff / 120.0)
            return displacement > 0.0f ? (short)0x7fff : (short)-0x7fff;
        return displacement > 0.0f
            ? (short)(sad * 120.0 + 0.5)
            : (short)(sad * -120.0 - 0.5);
    }

    /// <summary>Encode a position as three signed-sqrt deltas from a reference,
    /// mirroring <c>CompactPosition::Set</c> (igc.h:2243-2248).</summary>
    public static void EncodePositionDelta(Vector3 reference, Vector3 pos, Span<byte> dst)
    {
        WriteShort(dst, 0, Delta(pos.X - reference.X));
        WriteShort(dst, 2, Delta(pos.Y - reference.Y));
        WriteShort(dst, 4, Delta(pos.Z - reference.Z));
    }

    /// <summary>Recover a position from a reference plus the three deltas,
    /// mirroring <c>CompactPosition::Export</c> (igc.h:2250-2255).</summary>
    public static Vector3 DecodePositionDelta(Vector3 reference, ReadOnlySpan<byte> src)
    {
        short dx = (short)(src[0] | (src[1] << 8));
        short dy = (short)(src[2] | (src[3] << 8));
        short dz = (short)(src[4] | (src[5] << 8));
        return new Vector3(
            reference.X + Displacement(dx),
            reference.Y + Displacement(dy),
            reference.Z + Displacement(dz));
    }
}

/// <summary>
/// Orientation ↔ compact-quaternion quaternion (zlib/quaternion.cpp) ↔ the
/// 8-byte CompactOrientation slot. The two conversions are the interesting
/// part; quantization is <c>qa=ushort(a*65535+0.5)</c>,
/// <c>qx/y/z=short(q*32767)</c> (truncating), with <c>a≥0</c> enforced by
/// flipping the sign of the whole quaternion (which preserves the rotation —
/// <c>(-a,-x,-y,-z)</c> is the same orientation).
/// </summary>
public static class CompactOrientationCodec
{
    /// <summary>Width of the CompactOrientation slot, bytes.</summary>
    public const int Width = 8;

    /// <summary>Encode an orientation into its 8-byte slot (quaternion → quantize).</summary>
    public static void Encode(Orientation o, Span<byte> dst)
    {
        (float a, float x, float y, float z) q = ToQuaternion(o);
        if (q.a < 0.0f) { q = (-q.a, -q.x, -q.y, -q.z); }   // ensure a ≥ 0 for CompactOrientation.Set (igc.h:2144)
        var qa = (ushort)(q.a * 65535.0f + 0.5f);
        CompactQuant.WriteShort(dst, 2, (short)(q.x * 32767.0f));
        CompactQuant.WriteShort(dst, 4, (short)(q.y * 32767.0f));
        CompactQuant.WriteShort(dst, 6, (short)(q.z * 32767.0f));
        dst[0] = (byte)(qa & 0xFF);
        dst[1] = (byte)((qa >> 8) & 0xFF);
    }

    /// <summary>Decode an 8-byte CompactOrientation slot to an orientation matrix.</summary>
    public static Orientation Decode(ReadOnlySpan<byte> b)
    {
        ushort qa = (ushort)(b[0] | (b[1] << 8));
        short qx = (short)(b[2] | (b[3] << 8));
        short qy = (short)(b[4] | (b[5] << 8));
        short qz = (short)(b[6] | (b[7] << 8));
        if (qa == 0 && qx == 0 && qy == 0 && qz == 0)
            return new Orientation();                  // all-zero ⇒ identity (igc.h:2155-2165)
        return FromQuaternion(qa / 65535.0f, qx / 32767.0f, qy / 32767.0f, qz / 32767.0f);
    }

    /// <summary>Port of <c>Quaternion::Quaternion(const Orientation&amp;)</c> (quaternion.cpp:63-105).</summary>
    private static (float A, float X, float Y, float Z) ToQuaternion(Orientation o)
    {
        Vector3 r0 = o.GetRight(), r1 = o.GetUp(), r2 = o.GetBackward();
        float m00 = r0.X, m01 = r0.Y, m02 = r0.Z;
        float m10 = r1.X, m11 = r1.Y, m12 = r1.Z;
        float m20 = r2.X, m21 = r2.Y, m22 = r2.Z;

        float trace = m00 + m11 + m22;
        if (trace > 0.001f)
        {
            float scale = MathF.Sqrt(1.0f + trace);
            float a = scale * 0.5f;
            scale = -0.5f / scale;
            float x = scale * (m21 - m12);
            float y = scale * (m02 - m20);
            float z = scale * (m10 - m01);
            return (a, x, y, z);
        }
        else
        {
            int i, j, k;
            if (m11 > m00)
                if (m22 > m11) { i = 2; j = 0; k = 1; }
                else           { i = 1; j = 2; k = 0; }
            else
                if (m22 > m00) { i = 2; j = 0; k = 1; }
                else           { i = 0; j = 1; k = 2; }

            float dojk = C(j, k) - C(k, j);
            float scale = dojk >= 0.0f
                ? MathF.Sqrt(1.0f + C(i, i) - C(j, j) - C(k, k))
                : -MathF.Sqrt(1.0f + C(i, i) - C(j, j) - C(k, k));

            var vec = new float[3];
            vec[i] = scale * 0.5f;
            scale = 0.5f / scale;
            float a = scale * dojk;
            vec[j] = scale * (C(j, i) + C(i, j));
            vec[k] = scale * (C(k, i) + C(i, k));
            return (a, vec[0], vec[1], vec[2]);

            float C(int row, int col)
            {
                Vector3 r = row switch { 0 => r0, 1 => r1, _ => r2 };
                return col switch { 0 => r.X, 1 => r.Y, _ => r.Z };
            }
        }
    }

    /// <summary>Port of <c>Quaternion::operator Orientation()</c> (quaternion.cpp:121-179).</summary>
    private static Orientation FromQuaternion(float a, float x, float y, float z)
    {
        double s = a, X = x, Y = y, Z = z;
        double i2 = 2.0 * X, j2 = 2.0 * Y, k2 = 2.0 * Z;
        double l2 = s * s + X * X + Y * Y + Z * Z;
        double ii2 = (X * i2) / l2, jj2 = (Y * j2) / l2, kk2 = (Z * k2) / l2;
        double ij2 = (X * j2) / l2, sk2 = (s * k2) / l2;
        double jk2 = (Y * k2) / l2, si2 = (s * i2) / l2;
        double ki2 = (Z * i2) / l2, sj2 = (s * j2) / l2;

        var m = new[,]
        {
            { (float)(1.0 - jj2 - kk2), (float)(ij2 + sk2), (float)(ki2 - sj2) },
            { (float)(ij2 - sk2),       (float)(1.0 - kk2 - ii2), (float)(jk2 + si2) },
            { (float)(ki2 + sj2),       (float)(jk2 - si2), (float)(1.0 - ii2 - jj2) },
        };

        var o = new Orientation();
        o.SetRight(new Vector3(m[0, 0], m[0, 1], m[0, 2]));
        o.SetUp(new Vector3(m[1, 0], m[1, 1], m[1, 2]));
        o.SetForward(new Vector3(-m[2, 0], -m[2, 1], -m[2, 2]));   // backward row 2
        o.Renormalize();
        return o;
    }
}
