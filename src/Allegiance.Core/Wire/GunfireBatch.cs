using System.Buffers.Binary;
using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// P994 port-only GUNFIRE_BATCH(221), above native's 200 ceiling. Ordered tagged
/// records in one FM_VAR_ITEM-style array. Inspiration: Inc/Messages.h:890-900
/// and Igc/igc.h:2198-2255; this is deliberately not native binary compatibility.
/// No recipient-relative reference: positions use the sector's coordinate space.
/// See docs/reports/GUNFIRE_BATCH_P994.md for the version-1 byte layout.
/// </summary>
public sealed class GunfireBatch : IFedMessage
{
    public const ushort Fmid = 221;
    public const int HeaderSize = 16;
    public const int MaxRecordSize = 47;
    public const float PositionError = 1f / 128f;
    public const float VelocityError = 1f / 512f;
    public const float DirectionError = 0.00014f;
    public uint Tick;
    public IFedMessage[] Records = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (Records.Length == 0 || Records.Length > (ushort.MaxValue - HeaderSize) / 25)
            throw new ArgumentException("Gunfire batches need a nonempty bounded record array");
        var buffer = new byte[checked(HeaderSize + Records.Length * MaxRecordSize)];
        int end = HeaderSize;
        foreach (var record in Records)
        {
            if (record is ProjectileInstance projectile && projectile.TimeCreate != Tick)
                throw new ArgumentException("A gunfire batch contains one creation tick");
            end += record switch
            {
                ProjectileInstance p => WriteProjectile(buffer.AsSpan(end), p.ProjectileTypeId,
                    p.LauncherId, p.LauncherType, p.Position, p.Forward, p.Velocity, p.Lifespan),
                ProjectileHullHit h => WriteHit(buffer.AsSpan(end), h.TargetId, h.ClusterId,
                    h.SourceId, h.AbsorbedByShield, h.Radius, h.Position, h.Normal),
                _ => throw new ArgumentException("Only projectile and hull-hit records belong in a gunfire batch"),
            };
        }
        WriteHeader(buffer, end, Tick, Records.Length);
        return buffer.AsSpan(0, end).ToArray();
    }

    public static void WriteHeader(Span<byte> buffer, int length, uint tick, int count)
    {
        if (length < HeaderSize || length > ushort.MaxValue || count <= 0 || count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
        var w = new WireWriter(buffer);
        w.WriteU16((ushort)length);
        w.WriteU16(Fmid);
        w.WriteU16(HeaderSize);                   // FM_VAR_ITEM offset
        w.WriteU16((ushort)(length - HeaderSize)); // FM_VAR_ITEM byte count
        w.WriteU32(tick);
        w.WriteU16((ushort)count);
        w.WriteByte(1);                          // version
        w.WriteByte(0);                          // reserved
    }

    /// <summary>Allocation-free record encode; initial position and lifespan
    /// stay exact, including blast interception overrides and own-echo edges.</summary>
    public static int WriteProjectile(Span<byte> buffer, short type, short launcher,
        IgcObjectType launcherType, Vector3 position, Vector3 forward, Vector3 velocity, float lifespan)
    {
        // Bound accumulated drift too: unusual long-lived/non-finite rounds
        // retain exact velocity and heading instead of magnifying quantization.
        float speedSquared = velocity.LengthSquared();
        // The renderer normalizes velocity above 1e-6, otherwise it uses
        // forward. Preserve that branch and bound heading error for slow rounds.
        bool stableHeading = speedSquared <= 1e-6f || speedSquared >= 64 * 64;
        bool compact = Unit(forward) && Fits(velocity, 256) && stableHeading
            && float.IsFinite(lifespan) && lifespan is >= 0 and <= 16;
        bool narrow = compact && Fits(velocity, 256, short.MinValue, short.MaxValue);
        var w = new WireWriter(buffer);
        w.WriteByte(compact ? narrow ? (byte)0 : (byte)1 : (byte)2);
        w.WriteI16(type);
        w.WriteI16(launcher);
        w.WriteI16((short)launcherType);
        WriteVector(ref w, position);
        if (compact)
        {
            WriteDirection(ref w, forward);
            WriteFixed(ref w, velocity, 256, narrow);
        }
        else
        {
            WriteVector(ref w, forward);
            WriteVector(ref w, velocity);
        }
        WriteFloat(ref w, lifespan);
        return w.Position;
    }

    public static int WriteHit(Span<byte> buffer, short target, short cluster, short source,
        bool shield, float radius, Vector3 position, Vector3 normal)
    {
        bool compact = Unit(normal) && Fits(position, 64);
        var w = new WireWriter(buffer);
        w.WriteByte(compact ? (byte)3 : (byte)4);
        w.WriteI16(target);
        w.WriteI16(cluster);
        w.WriteI16(source);
        w.WriteByte(shield ? (byte)1 : (byte)0);
        WriteFloat(ref w, radius);
        if (compact)
        {
            WriteFixed(ref w, position, 64, false);
            WriteDirection(ref w, normal);
        }
        else
        {
            WriteVector(ref w, position);
            WriteVector(ref w, normal);
        }
        return w.Position;
    }

    public static GunfireBatch Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) throw new ArgumentException("Truncated gunfire header");
        var h = FedMessageHeader.Read(buffer);
        if (h.Fmid != Fmid || h.CbMsg != buffer.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]) != HeaderSize
            || BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]) != buffer.Length - HeaderSize
            || buffer[14] != 1 || buffer[15] != 0)
            throw new ArgumentException("Invalid gunfire header/version/array bounds");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]);
        if (count == 0 || count > (buffer.Length - HeaderSize) / 25)
            throw new ArgumentException("Invalid gunfire count");
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        var records = new IFedMessage[count];
        int offset = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (offset >= buffer.Length) throw new ArgumentException("Truncated gunfire record");
            byte tag = buffer[offset];
            int size = tag switch { 0 => 33, 1 => 36, 2 => 47, 3 => 25, 4 => 36,
                _ => throw new ArgumentException("Unknown gunfire record tag") };
            if (buffer.Length - offset < size) throw new ArgumentException("Truncated gunfire record");
            var r = new WireReader(buffer.Slice(offset, size));
            r.Advance(1);
            if (tag <= 2)
            {
                var p = new ProjectileInstance { TimeCreate = tick, ProjectileTypeId = r.ReadI16(),
                    LauncherId = r.ReadI16(), LauncherType = (IgcObjectType)r.ReadI16(), Position = ReadVector(ref r) };
                p.Forward = tag == 2 ? ReadVector(ref r) : ReadDirection(ref r);
                p.Velocity = tag == 2 ? ReadVector(ref r) : ReadFixed(ref r, 256, tag == 0);
                p.Lifespan = ReadFloat(ref r);
                records[i] = p;
            }
            else
            {
                var hit = new ProjectileHullHit { TargetId = r.ReadI16(), ClusterId = r.ReadI16(), SourceId = r.ReadI16() };
                byte shield = r.ReadByte();
                if (shield > 1) throw new ArgumentException("Invalid shield flag");
                hit.AbsorbedByShield = shield == 1;
                hit.Radius = ReadFloat(ref r);
                hit.Position = tag == 4 ? ReadVector(ref r) : ReadFixed(ref r, 64, false);
                hit.Normal = tag == 4 ? ReadVector(ref r) : ReadDirection(ref r);
                records[i] = hit;
            }
            offset += size;
        }
        if (offset != buffer.Length) throw new ArgumentException("Trailing gunfire bytes");
        return new GunfireBatch { Tick = tick, Records = records };
    }

    private static bool Unit(Vector3 v) => float.IsFinite(v.LengthSquared()) && MathF.Abs(v.LengthSquared() - 1) <= 0.000001f;
    private static bool Fits(Vector3 v, float scale, int min = -8388608, int max = 8388607)
        => Fits(v.X, scale, min, max) && Fits(v.Y, scale, min, max) && Fits(v.Z, scale, min, max);
    private static bool Fits(float f, float scale, int min, int max)
        => float.IsFinite(f) && Math.Round((double)f * scale) >= min && Math.Round((double)f * scale) <= max;
    private static void WriteFixed(ref WireWriter w, Vector3 v, float scale, bool narrow)
    {
        WriteFixed(ref w, v.X, scale, narrow);
        WriteFixed(ref w, v.Y, scale, narrow);
        WriteFixed(ref w, v.Z, scale, narrow);
    }
    private static void WriteFixed(ref WireWriter w, float f, float scale, bool narrow)
    {
        int q = (int)Math.Round((double)f * scale);
        if (narrow) w.WriteI16((short)q);
        else { w.WriteByte((byte)q); w.WriteByte((byte)(q >> 8)); w.WriteByte((byte)(q >> 16)); }
    }
    private static Vector3 ReadFixed(ref WireReader r, float scale, bool narrow)
        => new(ReadFixedComponent(ref r, narrow) / scale, ReadFixedComponent(ref r, narrow) / scale,
            ReadFixedComponent(ref r, narrow) / scale);
    private static int ReadFixedComponent(ref WireReader r, bool narrow)
    {
        if (narrow) return r.ReadI16();
        int q = r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16;
        return q << 8 >> 8;
    }

    // Octahedral unit-vector encoding. Fold the lower hemisphere onto the same
    // square, quantize both coordinates to signed 16-bit, then unfold/normalize.
    private static float Sign(float f) => f < 0 ? -1 : 1;
    private static void WriteDirection(ref WireWriter w, Vector3 v)
    {
        float inverse = 1 / (MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z));
        float x = v.X * inverse, y = v.Y * inverse;
        if (v.Z < 0) (x, y) = ((1 - MathF.Abs(y)) * Sign(x), (1 - MathF.Abs(x)) * Sign(y));
        w.WriteI16((short)MathF.Round(x * 32767));
        w.WriteI16((short)MathF.Round(y * 32767));
    }
    private static Vector3 ReadDirection(ref WireReader r)
    {
        float x = r.ReadI16() / 32767f, y = r.ReadI16() / 32767f;
        float z = 1 - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0) (x, y) = ((1 - MathF.Abs(y)) * Sign(x), (1 - MathF.Abs(x)) * Sign(y));
        return new Vector3(x, y, z).Normalize();
    }
    private static void WriteFloat(ref WireWriter w, float f) => w.WriteU32(BitConverter.SingleToUInt32Bits(f));
    private static float ReadFloat(ref WireReader r) => BitConverter.UInt32BitsToSingle(r.ReadU32());
    private static void WriteVector(ref WireWriter w, Vector3 v)
    { WriteFloat(ref w, v.X); WriteFloat(ref w, v.Y); WriteFloat(ref w, v.Z); }
    private static Vector3 ReadVector(ref WireReader r) => new(ReadFloat(ref r), ReadFloat(ref r), ReadFloat(ref r));
}
