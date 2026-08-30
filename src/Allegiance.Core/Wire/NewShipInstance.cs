using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_NEW_SHIP_INSTANCE</c> (id 31, <c>Messages.h:262-264</c>) — the
/// server's object-creation message for a peer's own ship. Sent to a client
/// when it is assigned a ship, immediately before <c>SET_CLUSTER</c>(7)
/// (<c>src/Allegiance.Server/Server.cs</c>). It carries the peer's ship
/// object in <c>exportData</c>, a <c>DataShip</c> — the exact same binary
/// <c>DataShipIGC</c> + <c>PartData[]</c> format <c>IGCObject::Export</c>
/// writes for <c>OT_ship</c> (0) in .igc files (<c>igc.h:1965</c>), and the
/// same format <c>clintlib.cpp:3869</c> builds locally as a ship export.
/// Layout (wiki <c>allegiance-igc-file-format</c> §OT_ship(0); offsets
/// corrected here):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 31
///   offset 4: VarItem (ib, cb — 2 ushorts) for exportData
///   offset 8: exportData blob = DataShipIGC (52 B fixed since nParts = 0):
///     0   uint   dwPrivate     0
///     4   short  partsOffset   52 (= sizeof(DataShipIGC); constant)
///     6   short  hullID        ship.HullType.HullId
///     8   short  shipID        the assigned ship id
///    10   char[25] name        CharacterName, NUL-terminated, zero-padded
///    35   char   nParts        0  (simplification — no PartData[] appended)
///    36   short  sideID        live.Ship.SideId (must be non-null)
///    38   uchar  pilotType     10 (c_ptPlayer)
///    39   —      pad           0
///    40   short  abmOrders     0
///    42   short  baseObjectID  -1 (NA: spawned directly in space, not docked)
///    44   short  nDeaths       0
///    46   short  nEjections    0
///    48   short  nKills        0
///    50   —      pad           0
///    = 52 bytes total
///   = 60 B message, no further blobs.
/// </code>
/// The message is the <em>only</em> path that causes the C++ client's
/// <c>Import</c>/<c>CshipIGC::Initialize</c> to allocate the ship object its
/// <c>GetShip()</c>/<c>GetBaseHullType()</c>/<c>GetPosition()</c> read from —
/// <c>SET_CLUSTER</c>'s per-tick fields update that object but cannot create
/// it. Previously the server sent only <c>SET_CLUSTER</c> (Server.cs:438-450),
/// so the client's ship wrapper had a null native backing and
/// <c>GetPosition()</c> threw <c>NullReferenceException</c> (T14). See
/// <c>TRANSPORT_T14.md</c> (sibling repo ~/Development/Allegiance).
/// </summary>
public sealed class NewShipInstance : IFedMessage
{
    public const ushort Fmid = 31;

    /// <summary>Wire width of <c>DataShipIGC</c> with <c>nParts = 0</c> (the fixed 52-byte ship body, no <c>PartData[]</c> tail).</summary>
    public const int DataShipSize = 52;

    /// <summary>Wire width of the whole message: header(4) + one var item(4) + DataShipIGC(52).</summary>
    public const int FixedSize = 8 + DataShipSize;

    /// <summary><c>c_ptPlayer</c> — the pilot type clintlib.cpp:3869's own ship export uses.</summary>
    public const byte PilotTypePlayer = 10;

    // --- DataShipIGC fields ------------------------------------------------
    public string CharacterName = "";      // char[25], NUL-terminated + zero-padded
    public short HullId;                   // ship.HullType.HullId
    public short ShipId;                   // the assigned ship id
    public short SideId;                   // live.Ship.SideId (must be non-null by SET_CLUSTER time)
    public byte PilotType = PilotTypePlayer;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Position; w.Advance(2);   // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        // exportData blob (offset 8) = DataShipIGC, nParts = 0.
        int blobStart = w.Position;
        w.WriteU32(0);              // dwPrivate
        w.WriteI16(DataShipSize);   // partsOffset = sizeof(DataShipIGC)
        w.WriteI16(HullId);         // hullID
        w.WriteI16(ShipId);         // shipID
        WriteFixedName(ref w, CharacterName);   // char[25]
        w.WriteByte(0);             // nParts (simplification: no PartData[])
        w.WriteI16(SideId);         // sideID
        w.WriteByte(PilotType);     // pilotType
        w.WriteByte(0);             // pad
        w.WriteI16(0);              // abmOrders
        w.WriteI16(-1);             // baseObjectID (NA)
        w.WriteI16(0);              // nDeaths
        w.WriteI16(0);              // nEjections
        w.WriteI16(0);              // nKills
        w.WriteI16(0);              // pad
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataShipSize)
            throw new InvalidOperationException($"NEW_SHIP_INSTANCE DataShipIGC layout drift: {blobEnd - blobStart} != {DataShipSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"NEW_SHIP_INSTANCE layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static NewShipInstance Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"NEW_SHIP_INSTANCE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected NEW_SHIP_INSTANCE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb != DataShipSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataShipIGC {DataShipSize} (nParts=0)");

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
        if (nParts != 0) throw new ArgumentException($"nParts={nParts} != 0 (PartData[] not implemented; re-encode a single ship)");
        if (pilotType != PilotTypePlayer) throw new ArgumentException($"pilotType={pilotType} != c_ptPlayer {PilotTypePlayer}");

        return new NewShipInstance
        {
            CharacterName = name,
            HullId = hullId,
            ShipId = shipId,
            SideId = sideId,
            PilotType = pilotType,
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
