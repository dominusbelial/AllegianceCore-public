namespace Allegiance.Core.Wire;

/// <summary>
/// Mirrors the C++ <c>FEDMESSAGE</c> base (<c>messagecore.h:35-46</c>): the first
/// four bytes of every message on the wire.
///   offset 0: ushort cbmsg — total message size (incl. this header + var blobs)
///   offset 2: ushort fmid  — the message id (numeric, e.g. 1 = LOGONREQ).
/// Both little-endian, as native x86/x64 (<c>messagecore.h:21,27,28</c>).
/// </summary>
public readonly record struct FedMessageHeader(ushort CbMsg, ushort Fmid)
{
    /// <summary>Wire width of the base header, in bytes.</summary>
    public const int HeaderSize = 4;

    public static FedMessageHeader Read(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderSize)
            throw new ArgumentException($"FedMessageHeader needs >= {HeaderSize} bytes");
        ushort cbmsg = BitConverter.ToUInt16(buf);
        ushort fmid  = BitConverter.ToUInt16(buf.Slice(2));
        return new FedMessageHeader(cbmsg, fmid);
    }
}

/// <summary>
/// One variable-length item in a message: an <c>ib</c>/<c>cb</c> pair, both
/// <c>unsigned short</c> (<c>messagecore.h:27,693</c>).
///   ib = byte offset of the item's data from the start of the message
///   cb = length of the item's data (0 == item absent / NULL; includes the
///        trailing NUL for zero-terminated-string items).
/// A cb of zero also means "referenced but empty" on decode (<c>FM_VAR_REF</c>,
/// <c>messagecore.h:696</c>: returns NULL).
/// </summary>
public readonly record struct VarItem(ushort Ib, ushort Cb)
{
    /// <summary>Wire width of one ib/cb pair, in bytes.</summary>
    public const int ItemSize = 4;

    public bool IsPresent => Cb != 0;

    public static VarItem Read(ReadOnlySpan<byte> buf)
    {
        ushort ib = BitConverter.ToUInt16(buf);
        ushort cb = BitConverter.ToUInt16(buf.Slice(2));
        return new VarItem(ib, cb);
    }

    public void Write(Span<byte> buf)
    {
        BitConverter.GetBytes(Ib).CopyTo(buf);
        BitConverter.GetBytes(Cb).CopyTo(buf.Slice(2));
    }
}
