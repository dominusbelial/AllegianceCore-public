namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ADD_PART</c> (id 164, <c>Inc/Messages.h</c>:
/// <c>DEFINE_FEDMSG(S, ADD_PART, 164) ShipID shipID; PartData newPartData;
/// END_FEDMSG</c>) — the server telling every ship in a sector that a part was
/// added to a ship's loadout. This is the server→client half of the
/// part-treasure pickup round-trip (P145): after the server merges the picked
/// part into the target mount (or mounts it fresh), it broadcasts this
/// sector-wide via <c>GetGroupSectorFlying(pcluster)</c> (<c>FedSrv.CPP:6109-6119</c>)
/// exactly like <c>SWAP_PART</c>/<c>DROP_PART</c>, so every observer renders the
/// newly added part on the picking ship. It is <b>only ever sent by the
/// server</b> (an S message, never emitted by a client), so — unlike the CS
/// relay codecs — there is a single direction and no shipID-fill-in game.
/// </summary>
/// <remarks>
/// <para>Field widths, matching the port's existing loadout model:</para>
/// <list type="bullet">
/// <item><b>ShipID</b> = <c>short</c> (ObjectID/ShipID, <c>igc.h:477,480</c>),
/// the picking ship's id — same as every ship-id field in this port.</item>
/// <item><b>newPartData</b> = the <em>plain</em> 5-byte
/// <see cref="PartData"/> (<c>partID</c> short / <c>amount</c> short /
/// <c>mountID</c> char, <c>igc.h:1674-1679</c>) — the same observer-facing
/// shape <c>ExportShip</c>/<c>NewShipInstance</c> already use, <em>not</em> the
/// 6-byte <see cref="ExpandedPartData"/> with its requester-only
/// <c>fractionMounted</c> tail (established by the P61 loadout work and
/// confirmed for <c>ADD_PART</c> in that report). <c>partID</c> is the part
/// <em>type</em> id, <c>amount</c> is the quantity the pickup granted (the
/// buffered treasure amount, not a merged total — native fills it from the
/// buffered <c>amount</c>, <c>FedSrv.CPP:6116</c>), and <c>mountID</c> is the
/// slot the part now occupies.</item>
/// </list>
/// </remarks>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 164
///   offset 4: short  shipID      (ShipID)
///   offset 6: short  partID      (PartData.partID)
///   offset 8: short  amount      (PartData.amount)
///   offset 10: char  mountID     (PartData.mountID)
///   offset 11: 1 byte alignment pad (C++ struct align 2 → sizeof 12)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
public sealed class AddPart : IFedMessage
{
    public const ushort Fmid = 164;
    public const int FixedSize = 12;   // header + shipID short @4 + 5-byte PartData @6 + 1 pad @11

    /// <summary>The id of the ship the part was added to (the picking player's ship).</summary>
    public short ShipID;

    /// <summary>The added part's observer-facing <see cref="PartData"/> entry —
    /// <c>partID</c> = the part-type id, <c>amount</c> = the granted quantity,
    /// <c>mountID</c> = the destination mount slot.</summary>
    public PartData NewPartData;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipID);                  // offset 4: shipID (ShipID short)
        NewPartData.Write(ref w);            // offsets 6-10: partID/amount/mountID (5 bytes)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 padding (offset 11)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ADD_PART internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AddPart Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ADD_PART needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ADD_PART fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                        // header
        short shipID = r.ReadI16();
        var partData = PartData.Read(ref r);
        return new AddPart { ShipID = shipID, NewPartData = partData };
    }
}
