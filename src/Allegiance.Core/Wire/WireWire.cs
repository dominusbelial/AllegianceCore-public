using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// Little-endian cursor for building a single message in place, mirroring the
/// C++ <c>PFedMsgCreate</c> placement algorithm (<c>_Utility/Messages.cpp:358-438</c>):
/// the fixed region (header + var-item ib/cb pairs + fixed fields) is written
/// first, then the variable blobs are appended after it. <c>ib</c> values are
/// offsets measured from message byte 0, matching <c>*pib = pBlobDst - pbFM</c>.
/// All integers are little-endian.
/// </summary>
public ref struct WireWriter
{
    private readonly Span<byte> _buf;
    private int _pos;

    public WireWriter(Span<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    /// <summary>Current write position (distance from message byte 0).</summary>
    public readonly int Position => _pos;

    /// <summary>Total capacity available in the backing buffer.</summary>
    public readonly int Capacity => _buf.Length;

    public void WriteU16(ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(At(_pos), v);
        _pos += 2;
    }

    public void WriteI16(short v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(At(_pos), unchecked((ushort)v));
        _pos += 2;
    }

    public void WriteU32(uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(At(_pos), v);
        _pos += 4;
    }

    public void WriteI32(int v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(At(_pos), unchecked((uint)v));
        _pos += 4;
    }

    public void WriteU64(ulong v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(At(_pos), v);
        _pos += 8;
    }

    public void WriteByte(byte v)
    {
        At(_pos)[0] = v;
        _pos += 1;
    }

    public void Advance(int n)
    {
        if (n < 0 || _pos + n > _buf.Length)
            throw new ArgumentOutOfRangeException(nameof(n), "cursor advanced past end of buffer");
        _pos += n;
    }

    /// <summary>Advance past a region and report its start (used to reserve ib/cb slots).</summary>
    public int Reserve(int n)
    {
        int start = _pos;
        Advance(n);
        return start;
    }

    /// <summary>Write <paramref name="n"/> zero bytes.</summary>
    public void WriteZeros(int n)
    {
        Ensure(n);
        _buf.Slice(_pos, n).Clear();
        _pos += n;
    }

    /// <summary>Append raw bytes at the current position.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf[_pos..]);
        _pos += data.Length;
    }

    /// <summary>Patch a little-endian ushort at an absolute offset.</summary>
    public void WriteU16At(int absOff, ushort v)
    {
        if (absOff < 0 || absOff + 2 > _buf.Length)
            throw new ArgumentOutOfRangeException(nameof(absOff),
                $"absolute write at {absOff}+2 exceeds buffer length {_buf.Length}");
        BinaryPrimitives.WriteUInt16LittleEndian(At(absOff), v);
    }

    private readonly void Ensure(int n)
    {
        if (_pos + n > _buf.Length)
            throw new InvalidOperationException(
                $"message writer overran buffer: need {_pos + n}, have {_buf.Length}");
    }

    private readonly Span<byte> At(int off) => _buf.Slice(off);
}

/// <summary>
/// Little-endian cursor for decoding a received message buffer. Validates that
/// indexed reads stay in bounds. Mirrors the fixed/variable split of the wire.
/// </summary>
public ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public WireReader(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public readonly int Position => _pos;
    public readonly int Length => _buf.Length;
    public bool HasRemaining => _pos < _buf.Length;

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Slice(2));
    public short ReadI16() => unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(Slice(2)));
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Slice(4));
    public int ReadI32() => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(Slice(4)));
    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Slice(8));
    public byte ReadByte() => Slice(1)[0];

    /// <summary>Read <paramref name="n"/> raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int n) => Slice(n);

    /// <summary>Skip <paramref name="n"/> bytes (bounds-checked).</summary>
    public void Advance(int n)
    {
        if (n < 0 || _pos + n > _buf.Length)
            throw new ArgumentOutOfRangeException(nameof(n),
                $"reader advanced past end: {_pos} + {n} > {_buf.Length}");
        _pos += n;
    }

    /// <summary>Return a span starting at an absolute offset (bound-checked).</summary>
    public readonly ReadOnlySpan<byte> At(int absOff) => _buf[absOff..];

    private ReadOnlySpan<byte> Slice(int n)
    {
        if (_pos + n > _buf.Length)
            throw new ArgumentOutOfRangeException(nameof(n),
                $"reader past end: need {_pos + n}, have {_buf.Length}");
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }
}
