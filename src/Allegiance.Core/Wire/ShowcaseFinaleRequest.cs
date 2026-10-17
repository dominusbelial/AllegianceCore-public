using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>Port-only showcase orchestration: arm a bounded finale for one research epoch.
/// The server ignores this outside research showcase mode. No tech or purchase grants.</summary>
public sealed class ShowcaseFinaleRequest : IFedMessage
{
    public const ushort Fmid = 994;
    public Guid Epoch { get; set; }
    public short Winner { get; set; }
    public ushort GetFmid() => Fmid;
    public byte[] Encode()
    {
        var bytes = new byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 22);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        Epoch.TryWriteBytes(bytes.AsSpan(4));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), Winner);
        return bytes;
    }
    public static ShowcaseFinaleRequest Decode(ReadOnlySpan<byte> bytes)
    {
        var header = FedMessageHeader.Read(bytes);
        if (bytes.Length != 22 || header.CbMsg != 22 || header.Fmid != Fmid)
            throw new ArgumentException("invalid showcase finale request");
        return new() { Epoch = new Guid(bytes.Slice(4, 16)), Winner = BinaryPrimitives.ReadInt16LittleEndian(bytes[20..]) };
    }
}
