using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>ServerLightShipUpdate</c> wire record inside the
/// <see cref="LightShipsUpdate"/> (175) message's
/// <c>FM_VAR_ITEM(rgLightShipUpdates)</c> array. The native struct is declared
/// at <c>igc.h:2423-2429</c>:
/// <code>
///   class ServerLightShipUpdate   //8 bytes
///   {
///       ShipID              shipID;     //2
///       CompactState        stateM;     //2  (unsigned short bitmask, igc.h:2258)
///       CompactControlData  controls;   //4  (four char/127 axes, igc.h:2333)
///   };
/// </code>
/// <code>
///   offset 0: short  shipID
///   offset 2: ushort stateM
///   offset 4: byte   controls (yaw)
///   offset 5: byte   controls (pitch)
///   offset 6: byte   controls (roll)
///   offset 7: byte   controls (throttle)
///   = 8 bytes, no alignment gaps (the enclosing igc.h block is pack(1))
/// </code>
/// This is the "cheap" per-ship payload: id + state bits + commanded controls
/// only — no time/position/orientation. Those travel in the heavy variant
/// (<see cref="HeavyShipUpdateEntry"/>), which shares the same first 8 bytes.
/// <para><b>Layout source.</b> Unlike the arc's earlier usage-inferred structs,
/// this one is <b>declared outright</b> in the native reference (<c>igc.h:2423-2429</c>)
/// and cross-checked against the client decode (<c>shipIGC.cpp:3097-3102</c>
/// <c>ProcessShipUpdate(const ServerLightShipUpdate&amp;)</c> reads
/// <c>stateM</c> then <c>controls</c>) and the reference client
/// (<c>appmsg.cpp:712-731</c> iterates <c>ServerLightShipUpdate</c> elements
/// of stride <c>sizeof(ServerLightShipUpdate)</c>).</para>
/// </summary>
public readonly record struct LightShipUpdateEntry(
    short ShipId, ushort StateM,
    byte CtrlYaw, byte CtrlPitch, byte CtrlRoll, byte CtrlThrottle)
{
    /// <summary>Wire width of one record = native <c>sizeof(ServerLightShipUpdate)</c>
    /// under the pack(1) igc.h block = 8.</summary>
    public const int Size = 8;

    public void Write(ref WireWriter w)
    {
        w.WriteI16(ShipId);      // offset 0
        w.WriteU16(StateM);      // offset 2
        w.WriteByte(CtrlYaw);    // offset 4
        w.WriteByte(CtrlPitch);  // offset 5
        w.WriteByte(CtrlRoll);   // offset 6
        w.WriteByte(CtrlThrottle);// offset 7
    }

    public static LightShipUpdateEntry Read(ReadOnlySpan<byte> b) => new(
        ShipId: BinaryPrimitives.ReadInt16LittleEndian(b),
        StateM: BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(2)),
        b[4], b[5], b[6], b[7]);

    /// <summary>Build from rich ship state: the id, the <c>stateM</c> bitmask
    /// (stored unsigned, matching <c>CompactState::Set</c> igc.h:2264-2267), and
    /// the four commanded joystick axes quantized via
    /// <see cref="CompactQuant.ControlAxis"/> (<c>CompactControlData</c>,
    /// igc.h:2339-2343) — the exact pair of conversions the heavier
    /// <see cref="ServerShipUpdate"/> payload already uses for these slots.</summary>
    public static LightShipUpdateEntry From(short shipId, int stateM, ControlData controls) => new(
        shipId,
        unchecked((ushort)stateM),
        CompactQuant.ControlAxis(controls.Yaw),
        CompactQuant.ControlAxis(controls.Pitch),
        CompactQuant.ControlAxis(controls.Roll),
        CompactQuant.ControlAxis(controls.Throttle));
}

/// <summary>
/// <c>FM_S_LIGHT_SHIPS_UPDATE</c> (id 175), the server's cheap half of the
/// periodic multi-ship visibility sync (<c>Messages.h:890-893</c>), quoted in
/// full:
/// <code>
///   DEFINE_FEDMSG(S, LIGHT_SHIPS_UPDATE, 175)
///     FM_VAR_ITEM(rgInactiveTurretUpdates);
///     FM_VAR_ITEM(rgLightShipUpdates);
///   END_FEDMSG
/// </code>
/// Two var items, no scalar region:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 175
///   offset  4: ushort ib (rgInactiveTurretUpdates)
///   offset  6: ushort cb (rgInactiveTurretUpdates)   // packed ShipIDs (short each)
///   offset  8: ushort ib (rgLightShipUpdates)
///   offset 10: ushort cb (rgLightShipUpdates)        // n × LightShipUpdateEntry(8)
///   offset 12: blob — inactive-turret ShipIDs, then light ship updates
///   = 12 + blob bytes total
/// </code>
/// <para><b>Wire semantics</b> (P136, investigated from the native builder
/// <c>SendShipUpdates</c>, <c>FedSrv.CPP:3662-4228</c>): a <b>periodic</b>
/// per-cluster broadcast — every <c>cLightUpdates</c> light entries selected by
/// the deviation-sorted bandwidth budget go here, alongside the ShipIDs of
/// recently-inactivated turrets. This port's <b>foundation slice</b> (mirroring
/// P114/P131) deliberately builds the <b>codec only</b> — no broadcast/sim-loop
/// wiring, and <b>no deviation sort or per-interval budget</b>: the builder
/// simply includes every supplied ship (cited simplification; the real budget
/// algorithm is documented in <c>MULTISHIP_VISIBILITY_INVESTIGATION_P136.md</c>
/// as a separate later task).</para>
/// <para><b>Inactive-turret blob.</b> Native packs <c>ShipID</c> shorts here
/// (<c>FedSrv.CPP:4009-4020</c>). It is <b>not</b> P109's codec reused — the
/// S→C relay of other ships' turret states is a genuinely separate concept from
/// P109's direct C→S <c>ACTIVE_TURRET_UPDATE</c>(99)/<c>INACTIVE_TURRET_UPDATE</c>(100)
/// (see the MD). This slice carries the blob as <b>opaque passthrough</b>
/// (byte-for-byte round-trip, populated empty by <see cref="FromShips"/>); a
/// dedicated turret-relay task will type it.</para>
/// </summary>
public sealed class LightShipsUpdate : IFedMessage
{
    public const ushort Fmid = 175;

    /// <summary>Wire width of the fixed region before the blobs: header(4) +
    /// two ib/cb var pairs(4 each) = 12. No scalar fields; no explicit count —
    /// the light-ship count is implicit in the blob length over
    /// <see cref="LightShipUpdateEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + 2 * VarItem.ItemSize;

    /// <summary>The inactive-turret blob: packed <c>ShipID</c> shorts, carried
    /// opaquely (typed by the future turret-relay task). Empty by default.</summary>
    public byte[] InactiveTurretBlob = [];

    /// <summary>The per-cluster light ship list, in the sender's collection order.</summary>
    public LightShipUpdateEntry[] LightShipEntries = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from live ships — bakes the stateM/control
    /// quantization into each <see cref="LightShipUpdateEntry"/> and leaves the
    /// inactive-turret blob empty (foundation slice: turret relay deferred).
    /// No bandwidth budget/sorting is applied: every supplied ship is included
    /// (cited simplification).</summary>
    public static LightShipsUpdate FromShips(IEnumerable<Ship> ships)
    {
        var entries = ships
            .Select(s => LightShipUpdateEntry.From(s.Id, s.StateM, s.Controls))
            .ToArray();
        return new LightShipsUpdate { LightShipEntries = entries };
    }

    public byte[] Encode()
    {
        int blobLen = InactiveTurretBlob.Length + LightShipEntries.Length * LightShipUpdateEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int sIb = w.Reserve(2);     // ib @4  (inactive turrets)
        int sCb = w.Reserve(2);     // cb @6
        int lIb = w.Reserve(2);     // ib @8  (light ships)
        int lCb = w.Reserve(2);     // cb @10
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"LIGHT_SHIPS_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blobs at the fixed-region end, in declaration order
        int ib = w.Position;
        w.WriteBytes(InactiveTurretBlob);
        int lightIb = w.Position;
        foreach (var e in LightShipEntries) e.Write(ref w);
        w.WriteU16At(sIb, (ushort)ib);
        w.WriteU16At(sCb, (ushort)InactiveTurretBlob.Length);
        w.WriteU16At(lIb, (ushort)lightIb);
        w.WriteU16At(lCb, (ushort)(w.Position - lightIb));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static LightShipsUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LIGHT_SHIPS_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LIGHT_SHIPS_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var itIn = VarItem.Read(buf.Slice(4));
        var itLi = VarItem.Read(buf.Slice(8));

        byte[] inact = [];
        if (itIn.IsPresent)
        {
            if (itIn.Ib < 0 || itIn.Ib + itIn.Cb > buf.Length)
                throw new ArgumentException($"LIGHT_SHIPS_UPDATE inactive-turret blob [{itIn.Ib},{itIn.Ib + itIn.Cb}) out of range for length {buf.Length}");
            inact = buf.Slice(itIn.Ib, itIn.Cb).ToArray();
        }

        var entries = new List<LightShipUpdateEntry>();
        if (itLi.IsPresent)
        {
            if (itLi.Ib < 0 || itLi.Ib + itLi.Cb > buf.Length)
                throw new ArgumentException($"LIGHT_SHIPS_UPDATE light-ship blob [{itLi.Ib},{itLi.Ib + itLi.Cb}) out of range for length {buf.Length}");
            if (itLi.Cb % LightShipUpdateEntry.Size != 0)
                throw new ArgumentException($"LIGHT_SHIPS_UPDATE light-ship blob length {itLi.Cb} is not a multiple of {LightShipUpdateEntry.Size}");
            var blob = buf.Slice(itLi.Ib, itLi.Cb);
            for (int i = 0; i < itLi.Cb / LightShipUpdateEntry.Size; i++)
                entries.Add(LightShipUpdateEntry.Read(blob.Slice(i * LightShipUpdateEntry.Size, LightShipUpdateEntry.Size)));
        }
        return new LightShipsUpdate { InactiveTurretBlob = inact, LightShipEntries = entries.ToArray() };
    }
}
