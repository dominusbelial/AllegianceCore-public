namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_FIRE_EXPENDABLE</c> (id 82, <c>Messages.h:592-596</c>) — the
/// client→server request to fire its mounted dispenser (the client half of the
/// <c>FIRE_EXPENDABLE</c> pair; the server rebroadcast is the distinct
/// <see cref="FireExpendable"/> shape at id 87). Native shape:
/// <code>
///   DEFINE_FEDMSG(C, FIRE_EXPENDABLE, 82)
///     EquipmentType    et;
///   END_FEDMSG
/// </code>
/// A single <c>EquipmentType</c> short (the type of the mounted dispenser the
/// client wants to fire). The server looks up the mounted part at mount 0 of
/// that type (<c>GetMountedPart(et, 0)</c>, <c>FedSrv.CPP:5652</c>), decrements
/// its ammo, and replies with the <see cref="FireExpendable"/>(87) +
/// <c>CREATE_CHAFF</c> broadcast flow (see the server's
/// <c>HandleFireExpendable</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 82
///   offset 4: short  et        (EquipmentType — a 2-byte short, igc.h:530)
///   = 6 B fixed (2-aligned tail, same C++ sizeof as BoardShip/RipcordRequest)
/// </code>
/// Two distinct message shapes (82 vs 87) share the logical "FIRE_EXPENDABLE"
/// name with different fields — the same split this port already uses for
/// request/broadcast pairs like <c>RipcordRequest</c>/<c>RipcordActivate</c>
/// and <c>BuyLoadoutRequest</c>/<c>BuyLoadoutAck</c>, so they are two codec
/// classes (<see cref="FireExpendableRequest"/> here, <see cref="FireExpendable"/>
/// at 87) rather than one shape fighting to serve both directions.
/// </summary>
public sealed class FireExpendableRequest : IFedMessage
{
    public const ushort Fmid = 82;
    public const int FixedSize = 6;   // header + et short @4 (2-aligned tail)

    /// <summary>The <see cref="Allegiance.Core.EquipmentType"/> of the mounted
    /// dispenser to fire (<c>EquipmentType</c>, a 2-byte short — the same width
    /// <see cref="SwapPart.EtOld"/> already encodes).</summary>
    public EquipmentType Et;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16((short)Et);      // offset 4: et (EquipmentType short)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 tail (offset 5)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"FIRE_EXPENDABLE(82) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static FireExpendableRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"FIRE_EXPENDABLE(82) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected FIRE_EXPENDABLE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        var et = (EquipmentType)r.ReadI16();
        return new FireExpendableRequest { Et = et };
    }
}
