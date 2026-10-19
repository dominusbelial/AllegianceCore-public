using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>Port extension: complete authoritative last-restart-base objectives, independent
/// of scanner discovery. Does not export hidden non-objective stations or change targeting.</summary>
public sealed class LastBaseNotice : IFedMessage
{
    public const ushort Fmid = 995;
    public float FinaleSecondsRemaining { get; init; } = -1;
    public LastBaseTarget[] Targets { get; init; } = [];
    public ushort GetFmid() => Fmid;
    public byte[] Encode()
    {
        if (Targets.Length > 6) throw new ArgumentException("At most six sides");
        var bytes = new byte[10 + Targets.Length * 50];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), FinaleSecondsRemaining);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)Targets.Length);
        for (int i = 0; i < Targets.Length; i++)
        {
            var t = Targets[i]; var span = bytes.AsSpan(10 + i * 50, 50);
            BinaryPrimitives.WriteInt16LittleEndian(span, t.SideId);
            BinaryPrimitives.WriteInt16LittleEndian(span[2..], t.StationId);
            BinaryPrimitives.WriteInt16LittleEndian(span[4..], t.ClusterId);
            BinaryPrimitives.WriteSingleLittleEndian(span[6..], t.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[10..], t.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span[14..], t.Z);
            // Native station names fit 24 bytes; reserve 32, keeping a UTF-8 boundary.
            Encoding.UTF8.GetEncoder().Convert(t.Name.AsSpan(), span[18..49], true, out _, out _, out _);
        }
        return bytes;
    }
    public static LastBaseNotice Decode(ReadOnlySpan<byte> bytes)
    {
        var header = FedMessageHeader.Read(bytes);
        if (bytes.Length < 10 || header.Fmid != Fmid || header.CbMsg != bytes.Length)
            throw new ArgumentException("Invalid last base notice");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        float remaining = BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]);
        if (count > 6 || bytes.Length != 10 + count * 50 || !float.IsFinite(remaining) || remaining < -1)
            throw new ArgumentException("Invalid last base notice payload");
        var targets = new LastBaseTarget[count];
        for (int i = 0; i < count; i++)
        {
            var span = bytes.Slice(10 + i * 50, 50);
            float x = BinaryPrimitives.ReadSingleLittleEndian(span[6..]);
            float y = BinaryPrimitives.ReadSingleLittleEndian(span[10..]);
            float z = BinaryPrimitives.ReadSingleLittleEndian(span[14..]);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new ArgumentException("Invalid last base coordinates");
            var name = span[18..]; int end = name.IndexOf((byte)0);
            targets[i] = new(BinaryPrimitives.ReadInt16LittleEndian(span),
                BinaryPrimitives.ReadInt16LittleEndian(span[2..]), BinaryPrimitives.ReadInt16LittleEndian(span[4..]),
                x, y, z, Encoding.UTF8.GetString(end < 0 ? name : name[..end]));
        }
        return new() { FinaleSecondsRemaining = remaining, Targets = targets };
    }
}

public sealed record LastBaseTarget(short SideId, short StationId, short ClusterId, float X, float Y, float Z, string Name);
