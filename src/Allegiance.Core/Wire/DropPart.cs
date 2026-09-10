namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_DROP_PART</c> (id 33, <c>Inc/Messages.h:266-270</c>:
/// <c>DEFINE_FEDMSG(CS, DROP_PART, 33) ShipID shipID; EquipmentType et;
/// Mount mount; END_FEDMSG</c>) — a flying ship drops a mounted part into
/// space, which becomes a real part-type treasure. Sent client→server by the
/// requesting ship; the native handler (<c>FedSrv.CPP:6296-6330</c>) performs
/// the removal server-side and then re-broadcasts the <b>original message</b>
/// (now with <c>shipID</c> filled in from the player's ship,
/// <c>FedSrv.CPP:6312</c>) sector-wide via <c>GetGroupSectorFlying(pcluster)</c>
/// — the client sends <c>shipID</c> uninitialized/ignored, exactly like
/// <c>SWAP_PART</c>'s. The CS variant is used verbatim in both directions
/// (client sends it, the server forwards the same bytes back with shipID set),
/// so a single codec serves both — the same arrangement <see cref="SwapPart"/>
/// established.
/// </summary>
/// <remarks>
/// Field widths, matching the port's existing loadout model (identical
/// conventions to <see cref="SwapPart"/>'s):
/// <list type="bullet">
/// <item><b>ShipID</b> = <c>short</c> (ObjectID/ShipID, <c>igc.h:477,480</c>),
/// same as every ship-id field in this port.</item>
/// <item><b>EquipmentType</b> = native <c>typedef short EquipmentType</c>
/// (<c>Igc/igc.h:530</c>), and this port's own
/// <see cref="Allegiance.Core.EquipmentType"/> is already a <c>short</c> enum —
/// encoded as a plain little-endian Int16, cast from the enum (cf.
/// <see cref="SwapPart"/>).</item>
/// <item><b>Mount</b> = native <c>char</c> (<c>igc.h:841</c>); this port has
/// never modeled native's negative cargo-bay mount range
/// (<c>-c_maxCargo..-1</c>) — only the positive equipment/weapon slots exist
/// (<c>MountedPart.MountId</c> is already a <c>byte</c>), the same
/// simplification the P61 loadout work established. So <c>mount</c> is decoded
/// as a non-negative byte, no sign extension.</item>
/// </list>
/// </remarks>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 33
///   offset 4: short  shipID       (ShipID)
///   offset 6: short  et           (EquipmentType)
///   offset 8: char   mount        (Mount)
///   offset 9: 1 byte alignment pad (C++ struct align 2 → sizeof 10)
///   = 10 B fixed, no var items → cbmsg = 10
/// </code>
public sealed class DropPart : IFedMessage
{
    public const ushort Fmid = 33;
    public const int FixedSize = 10;   // header + 2 shorts @4/6 + char @8 + 1 pad @9

    /// <summary>The id of the requesting ship. Ignored/uninitialized on the
    /// client→server leg (the server fills it in on the echo,
    /// <c>FedSrv.CPP:6312</c>), like <c>SWAP_PART</c>'s.</summary>
    public short ShipID;

    /// <summary>The <see cref="Allegiance.Core.EquipmentType"/> of the part to drop.</summary>
    public EquipmentType Et;

    /// <summary>The mount slot the part currently occupies (non-negative byte).</summary>
    public byte Mount;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipID);                  // offset 4: shipID (ShipID short)
        w.WriteI16((short)Et);               // offset 6: et (EquipmentType short)
        w.WriteByte(Mount);                  // offset 8: mount (Mount char)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 padding (offset 9)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"DROP_PART internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static DropPart Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DROP_PART needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DROP_PART fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short shipID = r.ReadI16();
        var et = (EquipmentType)r.ReadI16();
        byte mount = r.ReadByte();
        return new DropPart { ShipID = shipID, Et = et, Mount = mount };
    }
}
