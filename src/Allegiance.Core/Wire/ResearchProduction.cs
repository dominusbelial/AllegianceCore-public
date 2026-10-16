using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>P703 read-only port extension. Identifies a production ship unambiguously when
/// different drone types share a hull. Native CFSDrone retains this identity (FedSrv.CPP:2220-2236).
/// It confers no authority: movement, death, funding and orders use their ordinary messages.</summary>
public sealed class ResearchProduction : IFedMessage
{
    public const ushort Fmid = 993;
    public short SideId { get; set; }
    public short ShipId { get; set; }
    public short DroneTypeId { get; set; }
    public short StationTypeId { get; set; } = -1;
    public ushort GetFmid() => Fmid;
    public byte[] Encode()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Fmid);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), SideId);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), ShipId);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), DroneTypeId);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(10), StationTypeId);
        return bytes;
    }
    public static ResearchProduction Decode(ReadOnlySpan<byte> bytes)
    {
        var header = FedMessageHeader.Read(bytes);
        if (bytes.Length != 12 || header.CbMsg != 12 || header.Fmid != Fmid)
            throw new ArgumentException("invalid research production message");
        return new() { SideId = BinaryPrimitives.ReadInt16LittleEndian(bytes[4..]),
            ShipId = BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]),
            DroneTypeId = BinaryPrimitives.ReadInt16LittleEndian(bytes[8..]),
            StationTypeId = BinaryPrimitives.ReadInt16LittleEndian(bytes[10..]) };
    }
}
