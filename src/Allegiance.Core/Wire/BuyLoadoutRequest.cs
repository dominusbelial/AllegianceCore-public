namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_BUY_LOADOUT</c> (id 4, <c>Messages.h:65-68</c>), the client-to-server
/// request message this port did not previously receive:
/// <code>
/// DEFINE_FEDMSG(C, BUY_LOADOUT, 4)
///   FM_VAR_ITEM(loadout);
///   bool  fLaunch;
/// END_FEDMSG
/// </code>
/// <c>loadout</c>'s variable blob is a <c>ShipLoadout</c> (2-byte hullID) +
/// zero-or-more <c>ExpandedPartData</c> (6 bytes each) entries — the client's
/// <em>requested</em> loadout (which hull, which parts on which mounts).
/// Unlike the ACK (which packs two <c>bool:1</c> into the flags byte), the
/// request's only fixed field is the single <c>bool fLaunch</c>, so bit0 is
/// <c>fLaunch</c> and bits 1..7 stay zero. Same fixed/variable order as the
/// ACK (<c>messagecore.h:777-786</c>): var descriptor first, then the fixed
/// byte, then the blob appended:
/// <code>
///   offset 0: ushort cbmsg = 9 + 6·nParts
///   offset 2: ushort fmid  = 4
///   offset 4: IB   ibLoadout   = 8   (var descriptor)
///   offset 6: CB   cbLoadout   = 2 + 6·nParts
///   offset 8: byte  flags: bit0 = fLaunch
///   offset 9: loadout blob = ShipLoadout: short hullID (2 bytes) then
///             nParts × ExpandedPartData (6 bytes each, declaration order)
///   = 9 + 6·nParts bytes total
/// </code>
/// For <c>nParts = 0</c> this is the 9-byte hull-only request.
/// </summary>
public sealed class BuyLoadoutRequest : IFedMessage
{
    public const ushort Fmid = 4;

    /// <summary>Wire width of <c>ShipLoadout</c> with <c>nParts = 0</c> (the 2-byte hullID).</summary>
    public const int ShipLoadoutSize = 2;

    /// <summary>Wire width of the whole request when <c>nParts = 0</c>: header(4) + var descriptor(4) + one flags byte(1) + ShipLoadout(2). Each additional <see cref="ExpandedPartData"/> adds <see cref="ExpandedPartData.Size"/> (6).</summary>
    public const int FixedSize = 4 + 4 + 1 + ShipLoadoutSize;

    // --- Fields -----------------------------------------------------------
    public short HullId;           // ShipLoadout.hullID — the hull the client wants
    public bool FLaunch;           // bit0 of the flags byte

    /// <summary>The <c>ExpandedPartData[]</c> tail (<c>nParts = Parts.Count</c>) the client wants mounted, in mount order. May be empty — the hull-only case that must still round-trip (P26/P27/P28 behavior).</summary>
    public IReadOnlyList<ExpandedPartData> Parts { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int nParts = Parts.Count;
        int blobSize = ShipLoadoutSize + nParts * ExpandedPartData.Size;
        int total = FixedSize + nParts * ExpandedPartData.Size;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);  // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        byte flags = (byte)(FLaunch ? 1 : 0);   // single bool:1, bit0 (bits 1..7 zero)
        w.WriteByte(flags);

        // loadout blob (offset 9) = ShipLoadout (hullID) + nParts ExpandedPartData.
        int blobStart = w.Position;
        w.WriteI16(HullId);
        foreach (var p in Parts) p.Write(ref w);
        int blobEnd = w.Position;
        if (blobEnd - blobStart != blobSize)
            throw new InvalidOperationException($"BUY_LOADOUT request ShipLoadout layout drift: {blobEnd - blobStart} != {blobSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"BUY_LOADOUT request layout drift: {w.Position} != {total}");
        return buf;
    }

    public static BuyLoadoutRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BUY_LOADOUT request needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BUY_LOADOUT request fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb < ShipLoadoutSize || (vi.Cb - ShipLoadoutSize) % ExpandedPartData.Size != 0)
            throw new ArgumentException($"loadout cb={vi.Cb} is not a ShipLoadout({ShipLoadoutSize}) + whole ExpandedPartData({ExpandedPartData.Size}) tail");
        int nParts = (vi.Cb - ShipLoadoutSize) / ExpandedPartData.Size;

        bool flaunch = (buf[8] & 1) != 0;

        WireReader r = new(buf.Slice(vi.Ib, vi.Cb));
        short hullId = r.ReadI16();
        var parts = new List<ExpandedPartData>(nParts);
        for (int i = 0; i < nParts; i++) parts.Add(ExpandedPartData.Read(ref r));

        return new BuyLoadoutRequest
        {
            HullId = hullId,
            FLaunch = flaunch,
            Parts = parts,
        };
    }
}
