namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SWAP_PART</c> (id 34, <c>Inc/Messages.h:273-278</c>:
/// <c>DEFINE_FEDMSG(CS, SWAP_PART, 34) ShipID shipID; EquipmentType etOld;
/// Mount mountOld; Mount mountNew; END_FEDMSG</c>) — a flying ship asks to move
/// the mounted part at <c>(etOld, mountOld)</c> to <c>mountNew</c>, swapping it
/// with a <b>different</b> part of the <b>same</b> <c>EquipmentType</c> already
/// occupying <c>mountNew</c> if one is present (otherwise a plain move). Sent
/// client→server by the requesting ship; the native handler
/// (<c>FedSrv.CPP:6459-6501</c>) performs the swap-or-move server-side and then
/// re-broadcasts the <b>original message</b> (now with <c>shipID</c> filled in
/// from the player's ship, <c>FedSrv.CPP:6497</c>) sector-wide via
/// <c>GetGroupSectorFlying(pcluster)</c> — the client sends <c>shipID</c>
/// uninitialized/ignored, exactly like <c>DROP_PART</c>'s. The CS variant is
/// used verbatim in both directions (client sends it, the server forwards the
/// same bytes back with shipID set), so a single codec serves both.
/// </summary>
/// <remarks>
/// Field widths, matching the port's existing loadout model:
/// <list type="bullet">
/// <item><b>ShipID</b> = <c>short</c> (ObjectID/ShipID, <c>igc.h:477,480</c>),
/// same as every ship-id field in this port.</item>
/// <item><b>EquipmentType</b> = native <c>typedef short EquipmentType</c>
/// (<c>Igc/igc.h:530</c>), and this port's own
/// <see cref="Allegiance.Core.EquipmentType"/> is already a <c>short</c> enum
/// (<c>src/Allegiance.Core/Data/DataPartTypeIGC.cs:9</c>) — encoded as a plain
/// little-endian Int16, cast from the enum. (This is the port's first bare
/// wire <c>EquipmentType</c> field: <c>ExpandedPartData</c>/<c>PartData</c>/
/// <c>BuyLoadoutRequest</c> only carry partID/amount/mountID, never a raw
/// EquipmentType.)</item>
/// <item><b>Mount</b> = native <c>char</c> (<c>igc.h:841</c>) — signed, so a
/// <b>negative</b> mount addresses the ship's cargo hold (<c>-1..-c_maxCargo</c>
/// = <c>-1..-5</c>, the separate <c>m_mountedCargos[]</c> array,
/// <c>Igc/shipIGC.h:2567</c>; P192, which also widens <see cref="SwapPart.MountOld"/>
/// /<see cref="SwapPart.MountNew"/> to <c>sbyte</c> so a client can swap an
/// equipped part into/out of cargo). Non-negative values address the equipped
/// equipment/weapon slots (<c>MountedPart.MountId</c> stays <c>byte</c>).</item>
/// </list>
/// </remarks>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 34
///   offset 4: short  shipID       (ShipID)
///   offset 6: short  etOld        (EquipmentType)
///   offset 8: char   mountOld     (Mount)
///   offset 9: char   mountNew     (Mount)
///   offset 10: 1 byte alignment pad (C++ struct align 2 → sizeof 12)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
public sealed class SwapPart : IFedMessage
{
    public const ushort Fmid = 34;
    public const int FixedSize = 12;   // header + 2 shorts @4/6 + 2 chars @8/9 + 1 pad @10

    /// <summary>The id of the requesting ship. Ignored/uninitialized on the
    /// client→server leg (the server fills it in on the echo,
    /// <c>FedSrv.CPP:6497</c>), like <c>DROP_PART</c>'s.</summary>
    public short ShipID;

    /// <summary>The <see cref="Allegiance.Core.EquipmentType"/> of the part to
    /// move — also selects (with <c>mountNew</c>) the occupant to swap against.</summary>
    public EquipmentType EtOld;

    /// <summary>The mount slot the part currently occupies. Signed so a <b>negative</b>
    /// value addresses the ship's cargo hold (<c>Mount</c> is a signed <c>char</c>;
    /// cargo slots run <c>-1..-c_maxCargo</c> = <c>-1..-5</c>, the native
    /// <c>m_mountedCargos[]</c>, <c>Igc/shipIGC.h:2567</c>; non-negative values
    /// address the equipped-mount list <c>_parts</c>).</summary>
    public sbyte MountOld;

    /// <summary>The destination mount slot. Signed for the same cargo-hold range as
    /// <see cref="MountOld"/>; a negative value stores the moved part in a cargo slot.</summary>
    public sbyte MountNew;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipID);                  // offset 4: shipID (ShipID short)
        w.WriteI16((short)EtOld);            // offset 6: etOld (EquipmentType short)
        w.WriteByte(unchecked((byte)MountOld));   // offset 8: mountOld (Mount signed char)
        w.WriteByte(unchecked((byte)MountNew));   // offset 9: mountNew (Mount signed char)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 padding (offsets 10-11)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SWAP_PART internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SwapPart Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SWAP_PART needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SWAP_PART fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short shipID = r.ReadI16();
        var etOld = (EquipmentType)r.ReadI16();
        sbyte mountOld = unchecked((sbyte)r.ReadByte());   // sign-extend the Mount char
        sbyte mountNew = unchecked((sbyte)r.ReadByte());
        return new SwapPart { ShipID = shipID, EtOld = etOld, MountOld = mountOld, MountNew = mountNew };
    }
}
