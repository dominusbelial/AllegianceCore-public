using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_RELAUNCH_SHIP</c> (id 192, <c>Messages.h:987-995</c>) — the
/// server's \"a ship just relaunched off a carrier landing-bay\" message,
/// broadcast to everyone flying in the lander's cluster
/// (<c>GetGroupSectorFlying(pship-&gt;GetCluster())</c>, <c>FedSrv.CPP:13019</c>).
/// Carries the relaunched ship's whole <c>ShipLoadout</c> (hull + mounted
/// parts), its new position/velocity/orientation after the launch-slot
/// reposition, and a single <c>Cookie</c> (byte):
/// <code>
/// DEFINE_FEDMSG(S, RELAUNCH_SHIP, 192)
///   FM_VAR_ITEM(loadout);
///   ShipID              shipID;
///   ShipID              carrierID;
///   Vector              position;
///   Vector              velocity;
///   CompactOrientation  orientation;
///   Cookie              cookie;
/// END_FEDMSG
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (<c>messagecore.h:777-786</c> — var items first, then
/// fixed fields, blobs appended; MSVC alignment as in <c>Eject.cs</c> and
/// <c>JoinedMission.cs</c>). The fixed region contains two 4-byte-aligned
/// <c>Vector</c> members, so the struct is 4-aligned and <c>sizeof</c> rounds
/// 45 up to <b>48</b>; the var blob starts at that padded offset
/// (<c>_Utility/Messages.cpp:392</c> <c>pBlobDst = pbFM + cbfm</c>, where the
/// <c>PFedMsgCreate</c> size argument is <c>sizeof(FMD_S_RELAUNCH_SHIP)</c>):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid  = 192
///   offset  4: IB    ibLoadout   = 48   (var descriptor)
///   offset  6: CB    cbLoadout   = 2 + 6·nParts
///   offset  8: ShipID shipID            (2)
///   offset 10: ShipID carrierID         (2)
///   offset 12: Vector position          (12, 4-aligned)
///   offset 24: Vector velocity          (12)
///   offset 36: CompactOrientation       (8)
///   offset 44: Cookie cookie            (1)
///   offset 45: 3 pad bytes              (round the fixed region to 4 → 48)
///   offset 48: loadout blob = ShipLoadout: short hullID (2) then
///              nParts × ExpandedPartData (6 each, declaration order)
///   = 48 + 2 + 6·nParts bytes total
/// </code>
/// The <c>loadout</c> blob is a <c>ShipLoadout</c> + <c>ExpandedPartData[]</c>
/// tail — the <b>same</b> blob shape <see cref="BuyLoadoutRequest"/> /
/// <see cref="BuyLoadoutAck"/> encode — so this codec reuses that encoding
/// logic (identical <c>WireWriter</c> writes and the <see cref="VarItem"/>
/// convention) rather than reinventing it.</para>
/// <para><b>Cookie simplification</b> (established precedent): native sets
/// <c>pfmLaunch-&gt;cookie = pfsShip-&gt;GetPlayer()-&gt;NewCookie()</c> for a
/// player-owned ship (<c>FedSrv.CPP:13018</c>); the port's <c>Cookie</c> is a
/// single <c>byte</c> frequently sent as <c>0</c> (<c>Server.cs:3289,4616</c>
/// — \"same simplification OnShipWarped already uses\" / \"no per-peer cookie
/// is stored\"), and this port stores no per-player cookie machinery, so the
/// relaunch broadcast carries <c>Cookie = 0</c> like <c>Eject.ForSurvivor</c>.</para>
/// </remarks>
public sealed class RelaunchShip : IFedMessage
{
    public const ushort Fmid = 192;

    /// <summary>Wire width of <c>ShipLoadout</c> with <c>nParts = 0</c> (just
    /// the 2-byte hullID).</summary>
    public const int ShipLoadoutSize = 2;

    /// <summary>Wire width of the whole message when <c>nParts = 0</c>:
    /// header(4) + var descriptor(4) + shipID(2) + carrierID(2) + position(12)
    /// + velocity(12) + CompactOrientation(8) + cookie(1) + 3 pad bytes = 48.
    /// Each additional <see cref="ExpandedPartData"/> adds
    /// <see cref="ExpandedPartData.Size"/> (6).</summary>
    public const int FixedSize = 48;

    /// <summary>The fixed-field offset where the <c>loadout</c> var blob
    /// begins (<c>= sizeof(FMD_S_RELAUNCH_SHIP)</c>, the 4-aligned 48).</summary>
    public const int LoadoutOffset = 48;

    // --- Fields -----------------------------------------------------------
    public short HullId;            // ShipLoadout.hullID — lander.HullType.HullId
    public short ShipId;            // the relaunched (landing) ship
    public short CarrierId;         // the carrier it landed on
    public Vector3 Position;        // new position after the launch-slot reposition
    public Vector3 Velocity;        // new outward velocity
    public Orientation Orientation = Orientation.Identity; // new attitude (carrier's, or turned toward the slot)
    public byte Cookie;             // 0 in this port (no per-player cookie machinery)

    /// <summary>The <c>ExpandedPartData[]</c> tail (<c>nParts = Parts.Count</c>),
    /// in mount order — the lander's current mounted parts (the port's
    /// ship-level stand-in for the native's <c>ExportShipLoadout</c>). May be
    /// empty (a hull-only lander).</summary>
    public IReadOnlyList<ExpandedPartData> Parts { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int nParts = Parts.Count;
        int blobSize = ShipLoadoutSize + nParts * ExpandedPartData.Size;
        int total = FixedSize + blobSize;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);  // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        w.WriteI16(ShipId);         // offset 8
        w.WriteI16(CarrierId);      // offset 10
        WriteVec(ref w, Position);  // offset 12
        WriteVec(ref w, Velocity);  // offset 24

        Span<byte> oct = new byte[CompactOrientationCodec.Width];
        CompactOrientationCodec.Encode(Orientation, oct);
        w.WriteBytes(oct);          // offset 36 (CompactOrientation, 8 bytes)

        w.WriteByte(Cookie);        // offset 44
        w.WriteZeros(FixedSize - w.Position);   // 3 pad bytes → 4-aligned 48

        // loadout blob (offset 48) = ShipLoadout (hullID) + nParts ExpandedPartData.
        int blobStart = w.Position;
        if (blobStart != LoadoutOffset)
            throw new InvalidOperationException($"RELAUNCH_SHIP loadout offset drift: {blobStart} != {LoadoutOffset}");
        w.WriteI16(HullId);
        foreach (var p in Parts) p.Write(ref w);
        int blobEnd = w.Position;
        if (blobEnd - blobStart != blobSize)
            throw new InvalidOperationException($"RELAUNCH_SHIP ShipLoadout layout drift: {blobEnd - blobStart} != {blobSize}");

        w.WriteU16At(slotIb, (ushort)LoadoutOffset);
        w.WriteU16At(slotCb, (ushort)blobSize);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"RELAUNCH_SHIP layout drift: {w.Position} != {total}");
        return buf;
    }

    public static RelaunchShip Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RELAUNCH_SHIP needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RELAUNCH_SHIP fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb < ShipLoadoutSize || (vi.Cb - ShipLoadoutSize) % ExpandedPartData.Size != 0)
            throw new ArgumentException($"loadout cb={vi.Cb} is not a ShipLoadout({ShipLoadoutSize}) + whole ExpandedPartData({ExpandedPartData.Size}) tail");

        WireReader r = new(buf);
        r.Advance(4);                       // header
        r.Advance(4);                       // ib/cb var descriptor (already validated)
        short shipId = r.ReadI16();         // offset 8
        short carrierId = r.ReadI16();      // offset 10
        var pos = ReadVec(ref r);           // offset 12
        var vel = ReadVec(ref r);           // offset 24
        var orient = CompactOrientationCodec.Decode(r.ReadBytes(CompactOrientationCodec.Width));  // offset 36
        byte cookie = r.ReadByte();         // offset 44
        r.Advance(FixedSize - r.Position);  // 3 pad bytes

        int nParts = (vi.Cb - ShipLoadoutSize) / ExpandedPartData.Size;
        WireReader rb = new(buf.Slice(vi.Ib, vi.Cb));
        short hullId = rb.ReadI16();
        var parts = new List<ExpandedPartData>(nParts);
        for (int i = 0; i < nParts; i++) parts.Add(ExpandedPartData.Read(ref rb));

        return new RelaunchShip
        {
            HullId = hullId,
            ShipId = shipId,
            CarrierId = carrierId,
            Position = pos,
            Velocity = vel,
            Orientation = orient,
            Cookie = cookie,
            Parts = parts,
        };
    }

    // The established 12-byte three-float LE encode/decode shared throughout
    // this project's wire codecs (Eject.cs:103-113) — reused, not reinvented.
    private static void WriteVec(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private static Vector3 ReadVec(ref WireReader r) =>
        new(BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()));
}
