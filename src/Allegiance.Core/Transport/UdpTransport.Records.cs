namespace Allegiance.Core.Transport;

public sealed partial class UdpTransport
{
    private readonly record struct Record(byte Flags, int Key, ulong Sequence, int Size, int Index);

    private static int VarSize(ulong value)
    {
        int n = 1;
        while (value >= 128) { n++; value >>= 7; }
        return n;
    }
    private static int RecordSize(Frame f, int index) => 1 + VarSize(unchecked((uint)f.Key)) + VarSize(f.Sequence)
        + VarSize((uint)f.Data.Length) + (f.Data.Length > Payload ? VarSize((uint)index) : 0);

    private static void WriteVar(Span<byte> buffer, ref int offset, ulong value)
    {
        while (value >= 128) { buffer[offset++] = (byte)((value & 127) | 128); value >>= 7; }
        buffer[offset++] = (byte)value;
    }
    private static bool ReadVar(ReadOnlySpan<byte> buffer, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < buffer.Length; shift += 7)
        {
            byte b = buffer[offset++];
            if (shift == 63 && b > 1) return false;
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return true;
        }
        return false;
    }
    private static void WriteRecord(Span<byte> buffer, ref int offset, Frame f, int index)
    {
        buffer[offset++] = (byte)((f.Reliable ? 1 : 0) | (f.Greet ? 2 : 0));
        WriteVar(buffer, ref offset, unchecked((uint)f.Key));
        WriteVar(buffer, ref offset, f.Sequence);
        WriteVar(buffer, ref offset, (uint)f.Data.Length);
        if (f.Data.Length > Payload) WriteVar(buffer, ref offset, (uint)index);
    }
    private static bool ReadRecord(ReadOnlySpan<byte> buffer, ref int offset, out Record record)
    {
        record = default;
        if (offset >= buffer.Length) return false;
        byte flags = buffer[offset++];
        if (!ReadVar(buffer, ref offset, out ulong key) || key > uint.MaxValue ||
            !ReadVar(buffer, ref offset, out ulong sequence) ||
            !ReadVar(buffer, ref offset, out ulong size) || size > MaxFrame) return false;
        ulong index = 0;
        if (size > Payload && (!ReadVar(buffer, ref offset, out index) || index > ushort.MaxValue)) return false;
        record = new(flags, unchecked((int)(uint)key), sequence, (int)size, (int)index);
        return true;
    }
}
