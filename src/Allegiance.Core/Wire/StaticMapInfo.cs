using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>Native Inc/Messages.h:120-130. Native struct size 44:
/// file[13], name[25], two alignment bytes, int teams.</summary>
public readonly record struct StaticMapRecord(string FileName, string FriendlyName, int Teams);

public sealed class StaticMapInfo : IFedMessage
{
    public const ushort Fmid = 11;
    public const int FixedSize = 8;
    public const int RecordSize = 44;
    public StaticMapRecord[] Maps = [];
    public ushort GetFmid() => Fmid;
    public byte[] Encode()
    {
        int size = checked(FixedSize + Maps.Length * RecordSize);
        if (size > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(Maps));
        byte[] bytes = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), FixedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)(size - FixedSize));
        for (int i = 0; i < Maps.Length; i++)
        {
            var record = bytes.AsSpan(FixedSize + i * RecordSize, RecordSize);
            WriteString(record[..13], Maps[i].FileName);
            WriteString(record.Slice(13, 25), Maps[i].FriendlyName);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], Maps[i].Teams);
        }
        return bytes;
    }
    private static void WriteString(Span<byte> target, string value)
    {
        byte[] encoded = Encoding.Latin1.GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, target.Length - 1)).CopyTo(target);
    }
    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
    public static StaticMapInfo Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FixedSize) throw new ArgumentException("STATIC_MAP_INFO fixed header truncated");
        var header = FedMessageHeader.Read(bytes);
        if (header.Fmid != Fmid || header.CbMsg != bytes.Length) throw new ArgumentException("Invalid STATIC_MAP_INFO header");
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        // Native appmsg.cpp:3360 floors cbmaps/sizeof(StaticMapInfo), then
        // copies only those complete records. No blob is read for an empty table.
        int records = count / RecordSize;
        if (records == 0) return new StaticMapInfo();
        if (offset < FixedSize || offset > bytes.Length || records * RecordSize > bytes.Length - offset)
            throw new ArgumentException("Invalid STATIC_MAP_INFO array");
        var result = new StaticMapInfo { Maps = new StaticMapRecord[records] };
        for (int i = 0; i < result.Maps.Length; i++)
        {
            var record = bytes.Slice(offset + i * RecordSize, RecordSize);
            result.Maps[i] = new(ReadString(record[..13]), ReadString(record.Slice(13, 25)),
                BinaryPrimitives.ReadInt32LittleEndian(record[40..]));
        }
        return result;
    }
}
