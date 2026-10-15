using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>P629 port extension (218), owner-only authoritative WarningMask.
/// Native computes this locally (shipIGC.cpp:273,386,416); this client does
/// not simulate the populated cluster. Native ship-update layouts stay intact.
/// Layout: header(4), shipID(2), mask(1), reserved(1).</summary>
public sealed class ShipWarningUpdate : IFedMessage
{
    public const ushort Fmid = 218;
    public const int FixedSize = 8;
    public short ShipId;
    public ShipWarningMask Mask;
    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var bytes = new byte[FixedSize];
        WireWriter w = new(bytes);
        w.WriteU16(FixedSize); w.WriteU16(Fmid); w.WriteI16(ShipId);
        w.WriteByte((byte)Mask); w.WriteByte(0);
        return bytes;
    }

    public static ShipWarningUpdate Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FixedSize) throw new ArgumentException("SHIP_WARNING_UPDATE needs 8 bytes");
        var hdr = FedMessageHeader.Read(bytes);
        if (hdr.Fmid != Fmid || hdr.CbMsg != FixedSize) throw new ArgumentException("Invalid SHIP_WARNING_UPDATE header");
        WireReader r = new(bytes); r.Advance(4);
        return new ShipWarningUpdate { ShipId = r.ReadI16(), Mask = (ShipWarningMask)r.ReadByte() };
    }
}
