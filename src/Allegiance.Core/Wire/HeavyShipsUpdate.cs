using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// The wire payload of <c>ServerHeavyShipUpdate</c> — 39 bytes
/// (<c>igc.h:2431-2442</c>), the "expensive" per-ship record inside
/// <c>FM_S_HEAVY_SHIPS_UPDATE</c> (176). It inherits the light record's first 8
/// bytes (shipID/stateM/controls) and adds full kinematic state plus the two
/// delta-compressed time/position slots that distinguish it from the 47-byte
/// absolute <see cref="ServerShipUpdate"/>. Layout (the igc.h block is pack(1)):
/// <code>
///   0  short   shipID
///   2  ushort  stateM
///   4  [4]     controls (CompactControlData)
///   8  short   time (CompactTime — signed delta from the message's timeReference)
///  10  [6]     position (CompactPosition — 3 signed-sqrt deltas from positionReference)
///  16  [8]     orientation (CompactOrientation)
///  24  [5]     velocity (CompactVelocity)
///  29  [3]     turnRates (CompactTurnRates)
///  32  [6]     fractions (CompactShipFractions: hull/shield/ammo/fuel/energy/ore)
///  38  [1]     power (BytePercentage)
///   = 39 bytes (2+2+4+2+6+8+5+3+6+1)
/// </code>
/// <para><b>Delta-vs-absolute (the key design decision, P136).</b> Native
/// ServerHeavyShipUpdate compresses time and position as <em>deltas</em> from
/// per-recipient references (<c>CompactTime</c> 2 bytes, igc.h:2178;
/// <c>CompactPosition</c> 6 bytes, igc.h:2198) — a bandwidth-era optimization
/// compared to the absolute 4+12 bytes of ServerSingleShipUpdate. Unlike the
/// loose suggestions floated during scoping, this port implements the
/// <b>faithful delta form</b>, not a simplified absolute one: this port's
/// established bar is byte-exact native wire fidelity (see P130/P133/P134
/// pack(1) layouts, the div-240 BytePercentage, the exact 47-byte
/// ServerShipUpdate), the layout is <em>declared</em> in the native reference
/// (not inferred), and real-client interoperability (P83) would require the
/// exact shape. The simplification the task did endorse — dropping the
/// deviation-sorted bandwidth budget — lives at the <em>builder/broadcast</em>
/// level, not here (see <see cref="HeavyShipsUpdate.FromShips"/>).</para>
/// <para>Round-trip exactness is by construction: the object stores the
/// canonical 39 bytes (from <see cref="From"/> or the wire) and every rich
/// accessor re-derives from those bytes, so <c>DecodeBody(EncodeBody(x))</c> is
/// a fixed point. Time/position recovery is a function of the message's
/// references: <see cref="Time"/> and <see cref="Position"/> take the
/// <c>timeReference</c>/<c>positionReference</c> from the enclosing
/// <see cref="HeavyShipsUpdate"/> (native <c>ProcessShipUpdate</c>,
/// shipIGC.cpp:3104-3113).</para>
/// </summary>
public sealed class HeavyShipUpdateEntry
{
    /// <summary>Wire width, bytes.</summary>
    public const int Width = 39;

    private byte[]? _bytes;
    private ShipWireSnapshot? _snapshot;
    private uint _snapshotTimeRef;
    private Vector3 _snapshotPosRef;
    private byte[] Bytes => _bytes ??= Materialize();

    private HeavyShipUpdateEntry(bool captured = false) { if (!captured) _bytes = new byte[Width]; }

    /// <summary>The embedded ship id (short).</summary>
    public short ShipId => _snapshot?.Id ?? BinaryPrimitives.ReadInt16LittleEndian(Bytes);

    /// <summary>stateM bitmask; stored unsigned (igc.h:2266).</summary>
    public int StateM => (int)BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(2));

    /// <summary>Decoded controls — four axes each <c>char/127</c>.</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(Bytes[4]),
        CompactQuant.DeControlAxis(Bytes[5]),
        CompactQuant.DeControlAxis(Bytes[6]),
        CompactQuant.DeControlAxis(Bytes[7]));

    /// <summary>The signed CompactTime offset (delta from the message's
    /// timeReference).</summary>
    public short TimeOffset => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(8));

    /// <summary>Recover the update's absolute time from the message's
    /// timeReference (<c>CompactTime::Export</c>, igc.h:2192-2195).</summary>
    public uint Time(uint timeReference) => CompactQuant.DecodeTimeOffset(timeReference, TimeOffset);

    /// <summary>Recover the update's world position from the message's
    /// positionReference (<c>CompactPosition::Export</c>, igc.h:2250-2255).</summary>
    public Vector3 Position(Vector3 positionReference) =>
        CompactQuant.DecodePositionDelta(positionReference, Bytes.AsSpan(10, 6));

    /// <summary>Attitude reconstructed from the compact quaternion.</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(Bytes.AsSpan(16, 8));

    /// <summary>World velocity reconstructed from the compact form.</summary>
    public Vector3 Velocity => CompactQuant.DecodeVelocity(Bytes.AsSpan(24, 5));

    /// <summary>Three turn rates, rad/s, in yaw/pitch/roll order.</summary>
    public float[] TurnRates => new[]
    {
        CompactQuant.DeTurnRate(Bytes[29]),
        CompactQuant.DeTurnRate(Bytes[30]),
        CompactQuant.DeTurnRate(Bytes[31]),
    };

    /// <summary>Hull fraction (0..1), from the 6-byte <c>CompactShipFractions</c>.</summary>
    public float HullFraction => CompactQuant.DePercent(Bytes[32]);
    /// <summary>Shield fraction (0..1).</summary>
    public float ShieldFraction => CompactQuant.DePercent(Bytes[33]);
    /// <summary>Ammo fraction (0..1).</summary>
    public float AmmoFraction => CompactQuant.DePercent(Bytes[34]);
    /// <summary>Fuel fraction (0..1).</summary>
    public float FuelFraction => CompactQuant.DePercent(Bytes[35]);
    /// <summary>Energy fraction (0..1).</summary>
    public float EnergyFraction => CompactQuant.DePercent(Bytes[36]);
    /// <summary>Ore fraction (0..1).</summary>
    public float OreFraction => CompactQuant.DePercent(Bytes[37]);

    /// <summary>Power (0..1).</summary>
    public float Power => CompactQuant.DePercent(Bytes[38]);

    /// <summary>
    /// Quantize a live ship's state into the canonical 39 bytes against the
    /// message's references. Mirrors <see cref="ServerShipUpdate.FromModel"/>
    /// field-for-field (same div-240 fractions, hull=<see cref="Ship.Fraction"/>,
    /// shield=<see cref="Ship.ShieldFraction"/>, ammo/fuel/energy full, ore =
    /// <c>Ore/MineCapacity</c>, power 1.0) but compacts time/position as deltas
    /// from <paramref name="timeRef"/>/<paramref name="posRef"/>. <paramref name="time"/>
    /// is the ship's update instant (the native player path supplies the client's
    /// <c>su.time</c>, FedSrv.CPP:3636); the caller picks it, typically the
    /// server's reference time for a 0-offset foundational sync.</summary>
    public static HeavyShipUpdateEntry From(
        uint timeRef, Vector3 posRef,
        short shipId, int stateM, ControlData controls,
        uint time, Vector3 position,
        Orientation orientation, Vector3 velocity, float[] turnRates,
        float hullFrac, float shieldFrac, float oreFrac)
    {
        var u = new HeavyShipUpdateEntry();
        CompactQuant.WriteShort(u.Bytes, 0, shipId);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Bytes.AsSpan(2), unchecked((ushort)stateM));
        u.Bytes[4] = CompactQuant.ControlAxis(controls.Yaw);
        u.Bytes[5] = CompactQuant.ControlAxis(controls.Pitch);
        u.Bytes[6] = CompactQuant.ControlAxis(controls.Roll);
        u.Bytes[7] = CompactQuant.ControlAxis(controls.Throttle);
        BinaryPrimitives.WriteInt16LittleEndian(u.Bytes.AsSpan(8), CompactQuant.EncodeTimeOffset(timeRef, time));
        CompactQuant.EncodePositionDelta(posRef, position, u.Bytes.AsSpan(10, 6));
        CompactOrientationCodec.Encode(orientation, u.Bytes.AsSpan(16, 8));
        CompactQuant.EncodeVelocity(velocity, u.Bytes.AsSpan(24, 5));
        u.Bytes[29] = CompactQuant.TurnRate(turnRates[0]);
        u.Bytes[30] = CompactQuant.TurnRate(turnRates[1]);
        u.Bytes[31] = CompactQuant.TurnRate(turnRates[2]);
        u.Bytes[32] = CompactQuant.Percent(Math.Clamp(hullFrac, 0f, 1f));
        u.Bytes[33] = CompactQuant.Percent(Math.Clamp(shieldFrac, 0f, 1f));
        u.Bytes[34] = CompactQuant.Percent(1.0f);   // ammo
        u.Bytes[35] = CompactQuant.Percent(1.0f);   // fuel
        u.Bytes[36] = CompactQuant.Percent(1.0f);   // energy
        u.Bytes[37] = CompactQuant.Percent(Math.Clamp(oreFrac, 0f, 1f));
        u.Bytes[38] = CompactQuant.Percent(1.0f);   // power
        return u;
    }

    /// <summary>Convenience wrapper over <see cref="From"/> pulling the fields
    /// straight off a live <see cref="Ship"/> (the P133-135 builder pattern),
    /// bakes the <c>Ore/MineCapacity</c> cargo fraction in.</summary>
    public static HeavyShipUpdateEntry From(uint timeRef, Vector3 posRef, Ship ship, uint time) =>
        From(timeRef, posRef,
            ship.Id, ship.StateM, ship.Controls,
            time, ship.Position,
            ship.Orientation, ship.Velocity, new[] { ship.TurnRates[0], ship.TurnRates[1], ship.TurnRates[2] },
            ship.Fraction, ship.ShieldFraction, ship.Ore / ship.EffectiveMineCapacity());

    /// <summary>Encode the 39 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(Bytes);

    /// <summary>Read a 39-byte body from the reader's current position.</summary>
    public static HeavyShipUpdateEntry DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"HeavyShipUpdateEntry needs {Width} bytes, got {r.Length - r.Position}");
        var u = new HeavyShipUpdateEntry();
        r.ReadBytes(Width).CopyTo(u.Bytes);
        return u;
    }

    /// <summary>Copy encoder inputs without quantizing or retaining the live ship.</summary>
    public static HeavyShipUpdateEntry Capture(uint timeRef, Vector3 posRef, Ship ship, uint time) => new(captured: true)
    {
        _bytes = null,
        _snapshot = ShipWireSnapshot.Capture(ship.Id, ship, time),
        _snapshotTimeRef = timeRef,
        _snapshotPosRef = posRef,
    };

    private byte[] Materialize()
    {
        var s = _snapshot!;
        return From(_snapshotTimeRef, _snapshotPosRef, s.Id, s.State, s.Controls, s.Time,
            s.Position, s.Orientation, s.Velocity, [s.Yaw, s.Pitch, s.Roll],
            s.Hull, s.Shield, s.Ore).Bytes;
    }
}

/// <summary>
/// The wire payload of the heavy message's active-turret relay record —
/// <c>ServerActiveTurretUpdate</c>, 16 bytes (<c>igc.h:2478-2485</c>). This is
/// the <b>server→observer</b> broadcast shape: the server relays <em>other</em>
/// ships' turret states to an observing recipient (<c>rgActiveTurretUpdates</c>,
/// FM_S_HEAVY_SHIPS_UPDATE(176)), so it leads with a <c>ShipID</c> and compacts
/// the timestamp as a <c>CompactTime</c> delta from the message's
/// <c>timeReference</c>. It is <b>genuinely distinct from P109's</b> 16-byte
/// <c>ClientActiveTurretUpdate</c> (<c>igc.h:2470-2476</c>: a full 4-byte
/// absolute <c>Time</c>, no shipID) — that one is the gunner's direct C→S
/// <c>ACTIVE_TURRET_UPDATE</c>(99) input, whereas this one is the S→C relay of
/// it (confirmed by P136 and by the native builder fill site,
/// FedSrv.CPP:4049-4071). Layout (the igc.h block is pack(1)):
/// <code>
///   0 short   shipID            (ShipID)
///   2 short   time              (CompactTime — signed delta from message timeReference)
///   4 [4]     controls          (CompactControlData)
///   8 [8]     orientation       (CompactOrientation)
///   = 16 bytes (2+2+4+8)
/// </code>
/// <para><b>Producer</b> (FedSrv.CPP:4051-4070): the server walks the ships
/// with a recent turret update, skips the recipient itself, and copies the
/// controls/orientation from the turret ship's stored P109
/// <c>ClientActiveTurretUpdate</c> (<c>atu.controls</c>, <c>atu.orientation</c>),
/// computing <c>time</c> as a delta from the message's <c>timeReference</c>
/// (<c>g.timeNow</c>) and stamping the turret ship's <c>shipID</c>. Same
/// round-trip-exact-by-construction pattern as <see cref="HeavyShipUpdateEntry"/>/
/// <see cref="ClientActiveTurretUpdate"/>: the object stores the canonical
/// 16 bytes and every rich accessor re-derives from those bytes.</para>
/// </summary>
public sealed class ServerActiveTurretUpdate
{
    /// <summary>Wire width, bytes.</summary>
    public const int Width = 16;

    private readonly byte[] _b = new byte[Width];

    private ServerActiveTurretUpdate() { }

    /// <summary>The turret ship whose update is being relayed (short).</summary>
    public short ShipId => BinaryPrimitives.ReadInt16LittleEndian(_b);

    /// <summary>The signed <c>CompactTime</c> offset (delta from the message's
    /// timeReference).</summary>
    public short TimeOffset => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(2));

    /// <summary>Recover the update's absolute time from the message's
    /// timeReference (<c>CompactTime::Export</c>, igc.h:2192-2195).</summary>
    public uint Time(uint timeReference) => CompactQuant.DecodeTimeOffset(timeReference, TimeOffset);

    /// <summary>The relayed turret controls — four axes each <c>char/127</c>.</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(_b[4]),
        CompactQuant.DeControlAxis(_b[5]),
        CompactQuant.DeControlAxis(_b[6]),
        CompactQuant.DeControlAxis(_b[7]));

    /// <summary>The relayed turret facing, reconstructed from the compact quaternion.</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(_b.AsSpan(8, 8));

    /// <summary>Build a relay record from rich values — the native producer
    /// side (FedSrv.CPP:4051-4070): the turret ship id, the update instant and
    /// the <paramref name="timeRef"/> it is a delta from (the message's
    /// timeReference, <c>g.timeNow</c>), plus the controls/orientation copied
    /// from the turret ship's P109 <c>ClientActiveTurretUpdate</c>.</summary>
    public static ServerActiveTurretUpdate From(
        short shipId, uint timeRef, uint time,
        ControlData controls, Orientation orientation)
    {
        var u = new ServerActiveTurretUpdate();
        BinaryPrimitives.WriteInt16LittleEndian(u._b, shipId);
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(2), CompactQuant.EncodeTimeOffset(timeRef, time));
        u._b[4] = CompactQuant.ControlAxis(controls.Yaw);
        u._b[5] = CompactQuant.ControlAxis(controls.Pitch);
        u._b[6] = CompactQuant.ControlAxis(controls.Roll);
        u._b[7] = CompactQuant.ControlAxis(controls.Throttle);
        CompactOrientationCodec.Encode(orientation, u._b.AsSpan(8, 8));
        return u;
    }

    /// <summary>Encode the 16 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 16-byte body from the reader's current position.</summary>
    public static ServerActiveTurretUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ServerActiveTurretUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ServerActiveTurretUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }
}

/// <summary>
/// <c>FM_S_HEAVY_SHIPS_UPDATE</c> (id 176), the server's expensive half of the
/// periodic multi-ship visibility sync (<c>Messages.h:895-905</c>), quoted in
/// full:
/// <code>
///   DEFINE_FEDMSG(S, HEAVY_SHIPS_UPDATE, 176)
///     FM_VAR_ITEM(rgActiveTurretUpdates);
///     FM_VAR_ITEM(rgHeavyShipUpdates);
///     Time                    timeReference;
///     Vector                  positionReference;
///     BytePercentage          bpTargetHull;
///     BytePercentage          bpTargetShield;
///     CompactShipFractions    fractions;
///   END_FEDMSG
/// </code>
/// Two var items plus a 24-byte scalar region:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 176
///   offset  4: ushort ib (rgActiveTurretUpdates)
///   offset  6: ushort cb (rgActiveTurretUpdates)   // n × ServerActiveTurretUpdate(16)
///   offset  8: ushort ib (rgHeavyShipUpdates)
///   offset 10: ushort cb (rgHeavyShipUpdates)      // n × HeavyShipUpdateEntry(39)
///   offset 12: uint   timeReference                (Time, 4)
///   offset 16: Vector positionReference            (12)
///   offset 28: byte   bpTargetHull
///   offset 29: byte   bpTargetShield
///   offset 30: [6]    fractions (CompactShipFractions)
///   offset 36: blob — active-turret updates, then heavy ship updates
///   = 36 + blob bytes total
/// </code>
/// <para><b>Wire semantics</b> (P136, native builder <c>SendShipUpdates</c>,
/// <c>FedSrv.CPP:3662-4228</c>): periodic per-cluster; each recipient's
/// <c>timeReference</c> is <c>g.timeNow</c> (:3996) and its <c>positionReference</c>
/// is the recipient's own source-ship position (:4128-4133), so every heavy
/// record's CompactTime/CompactPosition deltas are interpreted relative to the
/// receiver's frame. <c>bpTargetHull</c>/<c>bpTargetShield</c> report the
/// recipient's current command target's hull/shield (:4079-4125), and
/// <c>fractions</c> is the recipient's own fractions (:4044-4047) — both
/// per-recipient conveniences.</para>
/// <para><b>Foundation slice scope.</b> The codec round-trips the full native
/// shape (both blobs + scalar region). <see cref="FromShips"/> fills the
/// heavy-ship blob from already-modeled <see cref="Ship"/>s and optionally the
/// active-turret relay list (<see cref="ServerActiveTurretUpdate"/>s, P137) —
/// <b>no</b> deviation-sorted bandwidth budget (every supplied ship included;
/// the real budget is a separate later task). The active-turret record is the
/// S→C relay shape (igc.h:2478-2485), genuinely separate from P109's
/// <c>ClientActiveTurretUpdate</c>. Reference time/position
/// default to the values the builder used so the stored deltas decode back to
/// the source absolutes.</para>
/// </summary>
public sealed class HeavyShipsUpdate : IFedMessage
{
    public const ushort Fmid = 176;

    /// <summary>Wire width of the fixed region before the blobs: header(4) +
    /// two ib/cb var pairs(4 each) + 24 scalar bytes = 36.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + 2 * VarItem.ItemSize + 24;

    /// <summary>The per-cluster heavy ship list, in the sender's collection order.</summary>
    public HeavyShipUpdateEntry[] HeavyShipEntries = [];

    /// <summary>The active-turret relay list: packed <c>ServerActiveTurretUpdate</c>
    /// records (16 B each, igc.h:2478-2485) forwarding other ships' turret
    /// states to the observer. Empty by default.</summary>
    public ServerActiveTurretUpdate[] ActiveTurretUpdates = [];

    /// <summary>Reference time (offset 12) every heavy record's CompactTime is a
    /// delta from (<c>g.timeNow</c>, FedSrv.CPP:3996).</summary>
    public uint TimeReference;

    /// <summary>Reference position (offset 16) every heavy record's
    /// CompactPosition is a delta from (recipient's source-ship position,
    /// FedSrv.CPP:4128-4133).</summary>
    public Vector3 PositionReference;

    /// <summary>Div-240 target-hull percentage (offset 28), 255 = "no target"
    /// (FedSrv.CPP:4079, appmsg.cpp:742).</summary>
    public byte BpTargetHull;
    /// <summary>Div-240 target-shield percentage (offset 29).</summary>
    public byte BpTargetShield;

    // The 6-byte CompactShipFractions scalar (offsets 30..35) — the recipient's
    // own fractions, in hull/shield/ammo/fuel/energy/ore order (igc.h:2352-2361).

    public byte FractionsHull;
    public byte FractionsShield;
    public byte FractionsAmmo;
    public byte FractionsFuel;
    public byte FractionsEnergy;
    public byte FractionsOre;

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from live ships — quantizes each into a
    /// <see cref="HeavyShipUpdateEntry"/> against <paramref name="timeRef"/>/
    /// <paramref name="posRef"/>, which the message also carries so the stored
    /// deltas decode back to the source absolutes. Foundation slice: no
    /// deviation-sorted bandwidth budget (every ship included), active-turret
    /// blob left empty, target fields left at 255/255 ("no target").</summary>
    public static HeavyShipsUpdate FromShips(uint timeRef, Vector3 posRef, IEnumerable<Ship> ships,
        IEnumerable<ServerActiveTurretUpdate>? activeTurretUpdates = null)
    {
        var entries = ships
            .Select(s => HeavyShipUpdateEntry.From(timeRef, posRef, s, timeRef))
            .ToArray();
        return new HeavyShipsUpdate
        {
            TimeReference = timeRef,
            PositionReference = posRef,
            HeavyShipEntries = entries,
            ActiveTurretUpdates = activeTurretUpdates?.ToArray() ?? [],
            BpTargetHull = 255,
            BpTargetShield = 255,
            FractionsHull = 240,
            FractionsShield = 240,
            FractionsAmmo = 240,
            FractionsFuel = 240,
            FractionsEnergy = 240,
            FractionsOre = 240,
        };
    }

    public byte[] Encode()
    {
        int blobLen = ActiveTurretUpdates.Length * ServerActiveTurretUpdate.Width +
                      HeavyShipEntries.Length * HeavyShipUpdateEntry.Width;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int aIb = w.Reserve(2);     // ib @4  (active turrets)
        int aCb = w.Reserve(2);     // cb @6
        int hIb = w.Reserve(2);     // ib @8  (heavy ships)
        int hCb = w.Reserve(2);     // cb @10
        w.WriteU32(TimeReference);  // offset 12
        w.WriteU32(BitConverter.SingleToUInt32Bits(PositionReference.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(PositionReference.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(PositionReference.Z));
        w.WriteByte(BpTargetHull);  // offset 28
        w.WriteByte(BpTargetShield);// offset 29
        w.WriteByte(FractionsHull);
        w.WriteByte(FractionsShield);
        w.WriteByte(FractionsAmmo);
        w.WriteByte(FractionsFuel);
        w.WriteByte(FractionsEnergy);
        w.WriteByte(FractionsOre);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"HEAVY_SHIPS_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blobs at the fixed-region end, in declaration order
        int ib = w.Position;
        foreach (var a in ActiveTurretUpdates) a.EncodeBody(ref w);
        int heavyIb = w.Position;
        foreach (var e in HeavyShipEntries) e.EncodeBody(ref w);
        w.WriteU16At(aIb, (ushort)ib);
        w.WriteU16At(aCb, (ushort)(ActiveTurretUpdates.Length * ServerActiveTurretUpdate.Width));
        w.WriteU16At(hIb, (ushort)heavyIb);
        w.WriteU16At(hCb, (ushort)(w.Position - heavyIb));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static HeavyShipsUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"HEAVY_SHIPS_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected HEAVY_SHIPS_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var itAc = VarItem.Read(buf.Slice(4));
        var itHe = VarItem.Read(buf.Slice(8));
        uint timeRef = BitConverter.ToUInt32(buf.Slice(12));
        var posRef = new Vector3(
            BitConverter.ToSingle(buf.Slice(16)),
            BitConverter.ToSingle(buf.Slice(20)),
            BitConverter.ToSingle(buf.Slice(24)));

        ServerActiveTurretUpdate[] acts = [];
        if (itAc.IsPresent)
        {
            if (itAc.Ib < 0 || itAc.Ib + itAc.Cb > buf.Length)
                throw new ArgumentException($"HEAVY_SHIPS_UPDATE active-turret blob [{itAc.Ib},{itAc.Ib + itAc.Cb}) out of range for length {buf.Length}");
            if (itAc.Cb % ServerActiveTurretUpdate.Width != 0)
                throw new ArgumentException($"HEAVY_SHIPS_UPDATE active-turret blob length {itAc.Cb} is not a multiple of {ServerActiveTurretUpdate.Width}");
            var ab = buf.Slice(itAc.Ib, itAc.Cb);
            var ac = new List<ServerActiveTurretUpdate>(itAc.Cb / ServerActiveTurretUpdate.Width);
            for (int i = 0; i < itAc.Cb / ServerActiveTurretUpdate.Width; i++)
            {
                WireReader r = new(ab.Slice(i * ServerActiveTurretUpdate.Width, ServerActiveTurretUpdate.Width));
                ac.Add(ServerActiveTurretUpdate.DecodeBody(ref r));
            }
            acts = ac.ToArray();
        }

        var entries = new List<HeavyShipUpdateEntry>();
        if (itHe.IsPresent)
        {
            if (itHe.Ib < 0 || itHe.Ib + itHe.Cb > buf.Length)
                throw new ArgumentException($"HEAVY_SHIPS_UPDATE heavy-ship blob [{itHe.Ib},{itHe.Ib + itHe.Cb}) out of range for length {buf.Length}");
            if (itHe.Cb % HeavyShipUpdateEntry.Width != 0)
                throw new ArgumentException($"HEAVY_SHIPS_UPDATE heavy-ship blob length {itHe.Cb} is not a multiple of {HeavyShipUpdateEntry.Width}");
            var blob = buf.Slice(itHe.Ib, itHe.Cb);
            for (int i = 0; i < itHe.Cb / HeavyShipUpdateEntry.Width; i++)
            {
                WireReader r = new(blob.Slice(i * HeavyShipUpdateEntry.Width, HeavyShipUpdateEntry.Width));
                entries.Add(HeavyShipUpdateEntry.DecodeBody(ref r));
            }
        }

        return new HeavyShipsUpdate
        {
            ActiveTurretUpdates = acts,
            HeavyShipEntries = entries.ToArray(),
            TimeReference = timeRef,
            PositionReference = posRef,
            BpTargetHull = buf[28],
            BpTargetShield = buf[29],
            FractionsHull = buf[30],
            FractionsShield = buf[31],
            FractionsAmmo = buf[32],
            FractionsFuel = buf[33],
            FractionsEnergy = buf[34],
            FractionsOre = buf[35],
        };
    }
}
