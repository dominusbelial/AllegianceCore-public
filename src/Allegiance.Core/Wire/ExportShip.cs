using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26, <c>Messages.h:240-243</c>) — the server's
/// generic object-export message, and the <em>real</em> object-creation path
/// on the C++ client (<c>appmsg.cpp:80-105</c> calls
/// <c>m_pCoreIGC->CreateObject(objecttype, exportData, cb)</c>, which for
/// <c>OT_ship</c>=0 news a <c>CshipIGC</c> and <c>AddShip</c>s it so the
/// client's <c>GetShip()</c>/<c>GetBaseHullType()</c>/<c>GetPosition()</c>
/// read from a non-null native backing — the fix T14/P26 sought). Carries the
/// peer's own ship in <c>exportData</c>, the same <c>DataShipIGC</c> shape as
/// <see cref="NewShipInstance"/>, plus a separately-typed <c>objecttype</c>
/// field. Wire layout (macros convention; var-item descriptor first, then
/// fixed fields, then the appended blob region):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; <c>[v1_enum]</c> forces int32 width)
///   offset 12: exportData blob = DataShipIGC (52 B fixed body) + nParts × PartData (5 B each):
///     0(+12) uint   dwPrivate     0
///     4 short  partsOffset   52 (= sizeof(DataShipIGC); constant regardless of nParts —
///                                it locates where the PartData[] tail begins, not its length)
///     6 short  hullID        ship.HullType.HullId
///     8 short  shipID        the assigned ship id
///    10 char[25] name        CharacterName, NUL-terminated, zero-padded
///    35 char   nParts        Parts.Count (0..255)
///    36 short  sideID        live.Ship.SideId (must be non-null)
///    38 uchar  pilotType     10 (c_ptPlayer)
///    39 —      pad           0
///    40 short  abmOrders     0
///    42 short  baseObjectID  -1 (NA: spawned directly in space, not docked)
///    44 short  nDeaths       0
///    46 short  nEjections    0
///    48 short  nKills        0
///    50 —      pad           0
///    = 52 bytes fixed body
///    52 PartData[nParts]     each 5 B (see <see cref="PartData"/>), declaration order
///   = 64 + 5·nParts B message total, no further blobs.
/// </code>
/// <c>objecttype</c> is a <c>[v1_enum]</c> <c>AGCObjectType</c>
/// (<c>AGCIDL.idl:303</c>), i.e. a 4-byte int32 — <em>not</em> a short.
/// <c>OT_ship</c> = <c>AGC_Ship</c> = 0 (<c>AGCIDL.idl:308</c>). Compared to
/// <see cref="NewShipInstance"/> (fmid 31), which has no dispatch case on the
/// real client and is inert, this is the message the client actually handles.
/// See <c>P27_EXPORT.md</c> in the repo root.
///
/// The <c>PartData[]</c> tail (added after P27/P85; previously always
/// <c>nParts=0</c>, a documented simplification) is what makes an already-
/// mounted teammate's loadout visible to another peer at all — see
/// <c>CshipIGC::ReInitialize</c> (<c>shipIGC.cpp:150-160</c>), which iterates
/// <c>dataShip->nParts</c> and calls <c>CreateAndAddPart</c> for each entry on
/// EITHER initial creation OR an update to an already-known ship (re-sending
/// this same message for a ship id the client already has is how the real
/// client learns a loadout changed post-creation — no separate "loadout
/// changed" message exists for a non-owning observer). <c>nParts=0</c> stays
/// byte-for-byte identical to every prior encoding.
/// </summary>
public sealed class ExportShip : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_ship</c> = <c>AGC_Ship</c> = 0 — the only object type this codec emits.</summary>
    public const int OtShip = 0;

    /// <summary>Wire width of <c>DataShipIGC</c>'s fixed body (52 B), before any <c>PartData[]</c> tail.</summary>
    public const int DataShipSize = 52;

    /// <summary>Wire width of the whole message when <c>nParts = 0</c>: header(4) + one var item(4) + objecttype(4) + DataShipIGC(52). Each additional <see cref="PartData"/> adds <see cref="PartData.Size"/> (5).</summary>
    public const int FixedSize = 12 + DataShipSize;

    /// <summary><c>c_ptPlayer</c> — the pilot type clintlib.cpp:3869's own ship export uses.</summary>
    public const byte PilotTypePlayer = 10;

    // --- Fields -----------------------------------------------------------
    public int ObjectType = OtShip;          // int32 (not short) — [v1_enum] AGCObjectType

    // --- DataShipIGC fields ------------------------------------------------
    public string CharacterName = "";      // char[25], NUL-terminated + zero-padded
    public short HullId;                   // ship.HullType.HullId
    public short ShipId;                   // the assigned ship id
    public short SideId;                   // live.Ship.SideId (must be non-null by SET_CLUSTER time)
    public byte PilotType = PilotTypePlayer;

    /// <summary>
    /// The <c>PartData[]</c> tail (<c>nParts = Parts.Count</c>), in mount
    /// order. Empty by default (the historical hull-only export). Every entry
    /// is a part the exported ship already has mounted — this message has no
    /// requester-side "asked for" concept the way <c>BUY_LOADOUT</c> does, so
    /// there is no <c>fractionMounted</c> tail (see <see cref="PartData"/>'s
    /// own doc comment for why it's the smaller 5-byte base struct, not
    /// <see cref="ExpandedPartData"/>).
    /// </summary>
    public IReadOnlyList<PartData> Parts { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int nParts = Parts.Count;
        if (nParts > byte.MaxValue)
            throw new InvalidOperationException($"EXPORT nParts={nParts} exceeds the wire's 1-byte nParts field (max {byte.MaxValue})");
        int blobSize = DataShipSize + nParts * PartData.Size;
        int total = 12 + blobSize;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Position; w.Advance(2);   // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        w.WriteI32(ObjectType);     // objecttype — int32 at offset 8, already 4-aligned, no pad

        // exportData blob (offset 12) = DataShipIGC fixed body + nParts × PartData.
        int blobStart = w.Position;
        w.WriteU32(0);              // dwPrivate
        w.WriteI16(DataShipSize);   // partsOffset = sizeof(DataShipIGC) — constant, locates the tail
        w.WriteI16(HullId);         // hullID
        w.WriteI16(ShipId);         // shipID
        WriteFixedName(ref w, CharacterName);   // char[25]
        w.WriteByte((byte)nParts);  // nParts
        w.WriteI16(SideId);         // sideID
        w.WriteByte(PilotType);     // pilotType
        w.WriteByte(0);             // pad
        w.WriteI16(0);              // abmOrders
        w.WriteI16(-1);             // baseObjectID (NA)
        w.WriteI16(0);              // nDeaths
        w.WriteI16(0);              // nEjections
        w.WriteI16(0);              // nKills
        w.WriteI16(0);              // pad
        foreach (var p in Parts) p.Write(ref w);
        int blobEnd = w.Position;
        if (blobEnd - blobStart != blobSize)
            throw new InvalidOperationException($"EXPORT DataShipIGC layout drift: {blobEnd - blobStart} != {blobSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"EXPORT layout drift: {w.Position} != {total}");
        return buf;
    }

    public static ExportShip Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtShip)
            throw new ArgumentException($"objecttype={objectType} != OT_ship {OtShip} (this codec only emits ships)");
        if (vi.Cb < DataShipSize || (vi.Cb - DataShipSize) % PartData.Size != 0)
            throw new ArgumentException($"exportData cb={vi.Cb} is not DataShipIGC({DataShipSize}) + a whole PartData({PartData.Size}) tail");
        int nPartsFromCb = (vi.Cb - DataShipSize) / PartData.Size;

        WireReader r = new(buf.Slice(vi.Ib, vi.Cb));
        _ = r.ReadU32();            // dwPrivate
        int partsOffset = r.ReadI16();
        short hullId = r.ReadI16();
        short shipId = r.ReadI16();
        string name = ReadFixedName(ref r);
        byte nParts = r.ReadByte();
        short sideId = r.ReadI16();
        byte pilotType = r.ReadByte();
        r.Advance(1);               // pad
        _ = r.ReadI16();            // abmOrders
        _ = r.ReadI16();            // baseObjectID
        _ = r.ReadI16();            // nDeaths
        _ = r.ReadI16();            // nEjections
        _ = r.ReadI16();            // nKills
        r.Advance(2);               // pad

        if (partsOffset != DataShipSize) throw new ArgumentException($"partsOffset={partsOffset} != DataShipSize {DataShipSize}");
        if (nParts != nPartsFromCb) throw new ArgumentException($"nParts field={nParts} disagrees with tail length implied by cb ({nPartsFromCb})");
        if (pilotType != PilotTypePlayer) throw new ArgumentException($"pilotType={pilotType} != c_ptPlayer {PilotTypePlayer}");

        var parts = new List<PartData>(nParts);
        for (int i = 0; i < nParts; i++) parts.Add(PartData.Read(ref r));

        return new ExportShip
        {
            ObjectType = objectType,
            CharacterName = name,
            HullId = hullId,
            ShipId = shipId,
            SideId = sideId,
            PilotType = pilotType,
            Parts = parts,
        };
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Write a char[25]: up to 24 chars as Latin-1 bytes + a trailing NUL, then zero-pad the rest. Longer names are truncated to fit (room for the NUL), matching a fixed C <c>char[25]</c>.</summary>
    private static void WriteFixedName(ref WireWriter w, string s)
    {
        int n = Math.Min(s.Length, 24);
        if (n > 0) w.WriteBytes(Latin1.GetBytes(s[..n]));
        w.WriteByte(0);                                  // NUL
        w.WriteZeros(25 - n - 1);                        // zero-pad
    }

    /// <summary>Read a char[25] field, chomping trailing NULs (fixed arrays; trailing NULs are not meaningful, like <c>LogonRequest.ChompZeros</c>).</summary>
    private static string ReadFixedName(ref WireReader r)
    {
        var raw = r.ReadBytes(25);
        int n = raw.Length;
        while (n > 0 && raw[n - 1] == 0) n--;
        return Latin1.GetString(raw[..n]);
    }
}
