using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_LOADOUT_CHANGE</c> (id 96, <c>src/Inc/Messages.h:666-671</c>) — the
/// server's "a ship's loadout changed" broadcast, carrying that ship's
/// <b>current whole</b> loadout (not a delta) plus its boarded-child
/// (gunner/passenger) list. Native declaration:
/// <code>
///   DEFINE_FEDMSG(S, LOADOUT_CHANGE, 96)
///     FM_VAR_ITEM(loadout);       // a ShipLoadout blob: short hullID + ExpandedPartData[]
///     FM_VAR_ITEM(rgPassengers);  // PassengerData[]
///     ShipID sidShip;
///   END_FEDMSG
/// </code>
/// The native sender <c>CFSShip::QueueLoadoutChange</c>
/// (<c>src/FedSrv/fsship.cpp:708-735</c>) builds it fresh from the ship's whole
/// current state every time (via <c>ExportShipLoadout</c>) — no diffing — and
/// appends one <c>PassengerData</c> per current child ship (<c>GetChildShips()</c>),
/// each carrying the child's <c>ObjectID</c> and <c>GetTurretID()</c>
/// (<c>fsship.cpp:726-733</c>).
/// <para><b>Layout</b> (<c>messagecore.h:777-786</c>: all var-item ib/cb
/// descriptors first, in declaration order; fixed fields next; blobs appended —
/// the exact shape <see cref="LightShipsUpdate"/> documents for its two-var-item
/// form). Two var pairs, then one scalar, no padding (every member 2-aligned):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 96
///   offset  4: ushort ibLoadout          (var descriptor 1)
///   offset  6: ushort cbLoadout          (var descriptor 1)  = 2 + 6·nParts
///   offset  8: ushort ibRgPassengers     (var descriptor 2)
///   offset 10: ushort cbRgPassengers     (var descriptor 2)  = 4·nPassengers
///   offset 12: short  sidShip            (ShipID; the port sends the ship's own real id)
///   offset 14: loadout blob = ShipLoadout: short hullID (2) then
///              nParts × ExpandedPartData (6 each, declaration order)
///   then    : passengers blob = nPassengers × PassengerData (4 each)
///   = 14 + (2 + 6·nParts) + 4·nPassengers bytes total
/// </code>
/// The <c>loadout</c> blob is the <b>same</b> <c>ShipLoadout</c> + <see cref="ExpandedPartData"/>[]
/// shape <see cref="BuyLoadoutAck"/>/<see cref="BuyLoadoutRequest"/>/
/// <see cref="RelaunchShip"/> encode — this codec reuses that encoding exactly
/// (identical <c>WireWriter</c> writes and the <see cref="VarItem"/> convention),
/// not reinventing it.
/// <para><b>sidShip semantics</b> (client-only dispatch — see
/// <c>src/clintlib/appmsg.cpp:1991-2032</c>): the client uses <c>sidShip == NA</c>
/// to mean "about my own ship", otherwise resolves another visible ship by id. It
/// does <b>not</b> change what the server sends — the loadout always belongs to the
/// ship whose <c>QueueLoadoutChange</c> fired, and the non-force path
/// (<c>bForce=false</c>) sets <c>sidShip</c> to that ship's own real
/// <c>ObjectID</c> (<c>fsship.cpp:728</c>). This port only exercises that non-force
/// broadcast-to-observers path, so the codec carries the changed ship's real id.</para>
/// </summary>
public sealed class LoadoutChange : IFedMessage
{
    public const ushort Fmid = 96;

    /// <summary>Wire width of <c>ShipLoadout</c> with <c>nParts = 0</c> (just the
    /// 2-byte hullID).</summary>
    public const int ShipLoadoutSize = 2;

    /// <summary>Wire width of the whole message when <c>nParts = 0</c> and
    /// <c>nPassengers = 0</c>: header(4) + two var-item descriptors(8) + sidShip(2)
    /// + ShipLoadout(2) = 16. Each <see cref="ExpandedPartData"/> adds
    /// <see cref="ExpandedPartData.Size"/> (6); each <see cref="PassengerData"/>
    /// adds <see cref="PassengerData.Size"/> (4).</summary>
    public const int FixedSize = 14;   // header(4) + two var pairs(8) + sidShip(2)

    // --- Fields -----------------------------------------------------------
    /// <summary><c>ShipLoadout.hullID</c> — the changed ship's current hull type id
    /// (built fresh at broadcast time, mirroring native <c>ExportShipLoadout</c>).</summary>
    public short HullId;

    /// <summary><c>sidShip</c> — the changed ship's own real id (the non-force
    /// broadcast-to-observers path; native sets NA only for the out-of-scope
    /// <c>bForce</c> self-notify path).</summary>
    public short SidShip;

    /// <summary>The <c>ExpandedPartData[]</c> tail (<c>nParts = Parts.Count</c>), in
    /// mount order — the ship's <b>current whole</b> loadout (the port's stand-in for
    /// native's <c>ExportShipLoadout</c>). May be empty (a hull-only ship).</summary>
    public IReadOnlyList<ExpandedPartData> Parts { get; init; } = [];

    /// <summary>The <c>PassengerData[]</c> tail (<c>nPassengers = Passengers.Count</c>),
    /// in <c>ChildShips</c> order — one entry per currently-boarded child ship (gunner/
    /// passenger), each its <c>shipID</c> + <c>turretID</c>. May be empty.</summary>
    public IReadOnlyList<PassengerData> Passengers { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int nParts = Parts.Count;
        int nPass = Passengers.Count;
        int loadoutBlobSize = ShipLoadoutSize + nParts * ExpandedPartData.Size;
        int passBlobSize = nPass * PassengerData.Size;
        int total = FixedSize + loadoutBlobSize + passBlobSize;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int li = w.Reserve(2);      // ibLoadout   @4
        int lc = w.Reserve(2);      // cbLoadout   @6
        int pi = w.Reserve(2);      // ibRgPassengers @8
        int pc = w.Reserve(2);      // cbRgPassengers @10

        w.WriteI16(SidShip);        // offset 12

        // loadout blob (offset 14) = ShipLoadout (hullID) + nParts ExpandedPartData.
        int loadoutStart = w.Position;
        w.WriteI16(HullId);
        foreach (var p in Parts) p.Write(ref w);
        int loadoutEnd = w.Position;
        if (loadoutEnd - loadoutStart != loadoutBlobSize)
            throw new InvalidOperationException($"LOADOUT_CHANGE ShipLoadout layout drift: {loadoutEnd - loadoutStart} != {loadoutBlobSize}");

        // passengers blob, appended after the loadout blob.
        int passStart = w.Position;
        foreach (var p in Passengers) p.Write(ref w);
        int passEnd = w.Position;
        if (passEnd - passStart != passBlobSize)
            throw new InvalidOperationException($"LOADOUT_CHANGE PassengerData layout drift: {passEnd - passStart} != {passBlobSize}");

        w.WriteU16At(li, (ushort)loadoutStart);
        w.WriteU16At(lc, (ushort)loadoutBlobSize);
        w.WriteU16At(pi, (ushort)passStart);
        w.WriteU16At(pc, (ushort)passBlobSize);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"LOADOUT_CHANGE layout drift: {w.Position} != {total}");
        return buf;
    }

    public static LoadoutChange Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOADOUT_CHANGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LOADOUT_CHANGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vLoadout = VarItem.Read(buf.Slice(4));
        VarItem vPass = VarItem.Read(buf.Slice(8));

        if (vLoadout.Cb < ShipLoadoutSize || (vLoadout.Cb - ShipLoadoutSize) % ExpandedPartData.Size != 0)
            throw new ArgumentException($"loadout cb={vLoadout.Cb} is not a ShipLoadout({ShipLoadoutSize}) + whole ExpandedPartData({ExpandedPartData.Size}) tail");
        if (vPass.Cb % PassengerData.Size != 0)
            throw new ArgumentException($"rgPassengers cb={vPass.Cb} is not a whole number of PassengerData({PassengerData.Size})");

        short sidShip = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(12));

        int nParts = (vLoadout.Cb - ShipLoadoutSize) / ExpandedPartData.Size;
        int nPass = vPass.Cb / PassengerData.Size;

        WireReader rl = new(buf.Slice(vLoadout.Ib, vLoadout.Cb));
        short hullId = rl.ReadI16();
        var parts = new List<ExpandedPartData>(nParts);
        for (int i = 0; i < nParts; i++) parts.Add(ExpandedPartData.Read(ref rl));

        WireReader rp = new(buf.Slice(vPass.Ib, vPass.Cb));
        var passengers = new List<PassengerData>(nPass);
        for (int i = 0; i < nPass; i++) passengers.Add(PassengerData.Read(ref rp));

        return new LoadoutChange
        {
            HullId = hullId,
            SidShip = sidShip,
            Parts = parts,
            Passengers = passengers,
        };
    }
}
