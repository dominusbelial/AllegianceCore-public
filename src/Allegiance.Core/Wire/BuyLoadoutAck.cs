namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_BUY_LOADOUT_ACK</c> (id 5, <c>Messages.h:70-74</c>) — the server's
/// acknowledgment of a <c>FM_C_BUY_LOADOUT(4)</c> request, carrying the ship's
/// new <c>ShipLoadout</c> (hull + parts). On the C++ client this drives
/// <c>IshipIGC::ProcessShipLoadout</c> via <c>appmsg.cpp:3176-3221</c>, which
/// mutates the client's own ship object (<c>m_ship</c>) in place — no pointer
/// reassignment — and, critically for T14/P27, rebinds a no-hull dummy ship's
/// hull type: <c>ProcessShipLoadout</c> (<c>shipIGC.h:882-899</c>) fires the
/// "hull changed" branch whenever <c>m_myHullType.GetHullType()</c> is NULL
/// (exactly the dummy's case) and calls
/// <c>SetBaseHullType(GetMyMission()->GetHullType(hullID))</c>, making
/// <c>GetBaseHullType()</c>/<c>GetPosition()</c> stop NRE-ing. This is the
/// real-game transition from the no-hull dummy ship every freshly-connected
/// player starts with (<c>FedSrv.CPP:902-928</c> <c>ProcessConnect</c>) to a
/// real hull — our headless server previously sent only the create
/// (<c>ExportShip</c>, fmid 26) and the launch (<c>SetCluster</c>, fmid 7),
/// skipping this hull-adoption step.
///
/// Empirically confirmed layout (compiler-verified on the Windows VM against
/// the checked-in <c>igc.h</c>; P28 diagnostic probe): <c>sizeof(ShipLoadout)
/// = 2</c>, <c>offsetof(ShipLoadout, hullID) = 0</c>, <c>sizeof(ExpandedPartData)
/// = 6</c>. Source corroboration: <c>ShipLoadout</c> is
/// <c>#pragma pack(push,1)</c> with a single <c>HullID hullID</c> member
/// (<c>igc.h:2120-2135</c>); <c>HullID</c> = <c>ObjectID</c> = <c>short</c>
/// (<c>igc.h:477-483</c>); <c>PartData0()</c> returns <c>(this+1)</c>,
/// implying <c>sizeof(ShipLoadout)=2</c>.
///
/// Wire layout (<c>messagecore.h:777-786</c>: var items first, then fixed
/// fields, blobs appended — <c>ib</c> measured from byte 0), all little-endian:
/// <code>
///   offset 0: ushort cbmsg = 11
///   offset 2: ushort fmid  = 5
///   offset 4: IB   ibLoadout   = 9   (var descriptor)
///   offset 6: CB   cbLoadout   = 2   (= sizeof(ShipLoadout), nParts = 0)
///   offset 8: byte  flags: bit0 = fBoughtEverything, bit1 = fLaunch
///             (MSVC packs the two bool:1 into one byte, lowest bit first —
///             matching the FMD_ struct order fBoughtEverything, fLaunch)
///   offset 9: loadout blob = ShipLoadout: short hullID   (2 bytes, nParts=0)
///   = 11 bytes total
/// </code>
/// nParts = 0 (the deliberate P26/P27-style simplification): a real
/// per-part loadout with <c>ExpandedPartData[]</c> entries (6 bytes each)
/// would represent the full buy/economy machinery this port defers; this
/// message only assigns the hull, which is exactly the step that adopts
/// <c>m_ship</c> to a real hull.
/// </summary>
public sealed class BuyLoadoutAck : IFedMessage
{
    public const ushort Fmid = 5;

    /// <summary>Wire width of <c>ShipLoadout</c> with <c>nParts = 0</c> (just the 2-byte hullID, no <c>ExpandedPartData[]</c> tail).</summary>
    public const int ShipLoadoutSize = 2;

    /// <summary>Wire width of the whole message: header(4) + var descriptor(4) + one flags byte(1) + ShipLoadout(2).</summary>
    public const int FixedSize = 4 + 4 + 1 + ShipLoadoutSize;

    // --- Fields -----------------------------------------------------------
    public short HullId;           // ShipLoadout.hullID — ship.HullType.HullId (same field P26/P27 used)
    public bool FBoughtEverything; // bit0 of the flags byte — honest "false": we assign a hull, buy no parts
    public bool FLaunch;           // bit1 of the flags byte — "false": SET_CLUSTER already launched; no further launch msgs

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);  // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        // Flags byte: MSVC packs two `bool :1` into one byte, lowest-order bit
        // for the first-declared member (fBoughtEverything), next bit for
        // fLaunch. Anything above the two used bits stays zero.
        byte flags = (byte)((FBoughtEverything ? 1 : 0) | (FLaunch ? 2 : 0));
        w.WriteByte(flags);

        // loadout blob (offset 9) = ShipLoadout, nParts = 0.
        int blobStart = w.Position;
        w.WriteI16(HullId);         // hullID
        int blobEnd = w.Position;
        if (blobEnd - blobStart != ShipLoadoutSize)
            throw new InvalidOperationException($"BUY_LOADOUT_ACK ShipLoadout layout drift: {blobEnd - blobStart} != {ShipLoadoutSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BUY_LOADOUT_ACK layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BuyLoadoutAck Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BUY_LOADOUT_ACK needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BUY_LOADOUT_ACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb != ShipLoadoutSize)
            throw new ArgumentException($"loadout cb={vi.Cb} != ShipLoadout {ShipLoadoutSize} (nParts=0)");

        byte flags = buf[8];
        bool fbought = (flags & 1) != 0;
        bool flaunch = (flags & 2) != 0;

        WireReader r = new(buf.Slice(vi.Ib, vi.Cb));
        short hullId = r.ReadI16();

        return new BuyLoadoutAck
        {
            HullId = hullId,
            FBoughtEverything = fbought,
            FLaunch = flaunch,
        };
    }
}
