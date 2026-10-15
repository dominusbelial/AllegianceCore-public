using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// The wire payload of <c>ServerSingleShipUpdate</c> — 47 bytes
/// (<c>igc.h:2444</c>), the game-data block embedded in <c>FM_S_SET_CLUSTER</c>
/// (7) and <c>FM_S_SINGLE_SHIP_UPDATE</c> (78). Layout (see BOT.md §Step 1):
/// <code>
///   0 short   shipID           6 → 8+4+12+8+5+3+6+1 = 47
///   2 ushort  stateM
///   4 [4]     controls (CompactControlData)
///   8 uint    time
///  12 [12]    position (Vector, raw floats)
///  24 [8]     orientation (CompactOrientation)
///  32 [5]     velocity (CompactVelocity)
///  37 [3]     turnRates (CompactTurnRates)
///  40 [6]     fractions (CompactShipFractions: hull/shield/ammo/fuel/energy/ore)
///  46 [1]     power (BytePercentage)
/// </code>
/// Round-trip exactness is by construction: the object stores the canonical
/// 47 compact bytes (from <see cref="FromModel"/> or the wire) and every rich
/// accessor re-derives from those bytes, so <c>DecodeBody(EncodeBody(x))</c> is
/// a fixed point. Orientation/velocity/controls are lossy through their compact
/// forms; <c>FromModel</c> re-quantizes immediately so the stored rich values
/// already equal what a wire decode would produce.
/// </summary>
public sealed class ServerShipUpdate
{
    /// <summary>Wire width, bytes.</summary>
    public const int Width = 47;

    private byte[]? _bytes;
    private ShipWireSnapshot? _snapshot;
    private byte[] Bytes => _bytes ??= Materialize();

    private ServerShipUpdate(bool captured = false) { if (!captured) _bytes = new byte[Width]; }

    /// <summary>The embedded ship id (short).</summary>
    public short ShipId => BinaryPrimitives.ReadInt16LittleEndian(Bytes);

    /// <summary>stateM bitmask; values stored in the ushort as an unsigned mask (igc.h:2266).</summary>
    public int StateM => (int)BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(2));

    /// <summary>Decoded controls — four axes each <c>char/127</c>.</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(Bytes[4]),
        CompactQuant.DeControlAxis(Bytes[5]),
        CompactQuant.DeControlAxis(Bytes[6]),
        CompactQuant.DeControlAxis(Bytes[7]));

    /// <summary>Server time (uint).</summary>
    public uint Time => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8));

    /// <summary>World position — raw full-precision floats.</summary>
    public Vector3 Position => ReadVec(12);

    /// <summary>Attitude reconstructed from the compact quaternion.</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(Bytes.AsSpan(24, 8));

    /// <summary>World velocity reconstructed from the compact form.</summary>
    public Vector3 Velocity => CompactQuant.DecodeVelocity(Bytes.AsSpan(32, 5));

    /// <summary>Three turn rates, rad/s, in yaw/pitch/roll order.</summary>
    public float[] TurnRates => new[]
    {
        CompactQuant.DeTurnRate(Bytes[37]),
        CompactQuant.DeTurnRate(Bytes[38]),
        CompactQuant.DeTurnRate(Bytes[39]),
    };

    /// <summary>Hull fraction (0..1), from the 6-byte <c>CompactShipFractions</c>.</summary>
    public float HullFraction => CompactQuant.DePercent(Bytes[40]);
    /// <summary>Shield fraction (0..1).</summary>
    public float ShieldFraction => CompactQuant.DePercent(Bytes[41]);
    /// <summary>Ammo fraction (0..1).</summary>
    public float AmmoFraction => CompactQuant.DePercent(Bytes[42]);
    /// <summary>Fuel fraction (0..1). P377 — for the OWNING peer's payload of an
    /// adrift Lifepod this slot carries the server-computed remaining-oxygen
    /// fraction instead (a coasting pod has no real fuel), surfaced client-side
    /// as the OXYGEN countdown.</summary>
    public float FuelFraction => CompactQuant.DePercent(Bytes[43]);
    /// <summary>Energy fraction (0..1).</summary>
    public float EnergyFraction => CompactQuant.DePercent(Bytes[44]);
    /// <summary>Ore fraction (0..1).</summary>
    public float OreFraction => CompactQuant.DePercent(Bytes[45]);

    /// <summary>Power (0..1).</summary>
    public float Power => CompactQuant.DePercent(Bytes[46]);

    private Vector3 ReadVec(int off)
    {
        var s = Bytes.AsSpan(off);
        return new Vector3(
            BitConverter.ToSingle(s),
            BitConverter.ToSingle(s.Slice(4)),
            BitConverter.ToSingle(s.Slice(8)));
    }

    /// <summary>
    /// Quantize a live ship's state into the canonical 47 bytes. Hull, shield
    /// and ammo fractions are real (damage / P362 ammo); fuel/energy/power stay
    /// at flat 1.0 (the flight demo never drains them) — EXCEPT that when
    /// <paramref name="carryEndurance"/> is set (the OWNING peer's own payload)
    /// the fuel slot carries the ship's real remaining-lifepod-oxygen fraction
    /// (P377). <see cref="Ship.Ore"/> is the exception — the P55 cargo slot (<c>byte 45</c>,
    /// <c>OreFraction = Ore/MineCapacity</c>) is real: it is zero for an idle ship
    /// and rises as the ship mines, so mining is wire-visible without a new message
    /// (the same "rides an existing message" pattern P53 used for docking).
    /// </summary>
    public static ServerShipUpdate FromModel(short shipId, Ship ship, uint time, int extraStateBits = 0, bool carryEndurance = false)
    {
        var u = new ServerShipUpdate();
        CompactQuant.WriteShort(u.Bytes, 0, shipId);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Bytes.AsSpan(2), unchecked((ushort)(ship.StateM | extraStateBits)));
        u.Bytes[4] = CompactQuant.ControlAxis(ship.Controls.Yaw);
        u.Bytes[5] = CompactQuant.ControlAxis(ship.Controls.Pitch);
        u.Bytes[6] = CompactQuant.ControlAxis(ship.Controls.Roll);
        u.Bytes[7] = CompactQuant.ControlAxis(ship.Controls.Throttle);
        BinaryPrimitives.WriteUInt32LittleEndian(u.Bytes.AsSpan(8), time);
        WriteVec(u.Bytes.AsSpan(12), ship.Position);
        CompactOrientationCodec.Encode(ship.Orientation, u.Bytes.AsSpan(24, 8));
        CompactQuant.EncodeVelocity(ship.Velocity, u.Bytes.AsSpan(32, 5));
        u.Bytes[37] = CompactQuant.TurnRate(ship.TurnRates[0]);
        u.Bytes[38] = CompactQuant.TurnRate(ship.TurnRates[1]);
        u.Bytes[39] = CompactQuant.TurnRate(ship.TurnRates[2]);
        u.Bytes[40] = CompactQuant.Percent(Math.Clamp(ship.Fraction, 0f, 1f));   // hull (real: damage lowers it)
        u.Bytes[41] = CompactQuant.Percent(Math.Clamp(ship.ShieldFraction, 0f, 1f));   // shield (P87: real shield state; 0 for a ship with no shield mounted)
        // P362 — ammo is now REAL: P361 made Ship.Ammo a live magazine that
        // drains as projectile weapons fire and refills on reload. Previously
        // hardcoded to 1.0 (a flight-demo constant), so the own-ship ammo gauge
        // never reflected the pool. Same 47-byte slot (wire format unchanged);
        // this only fills the existing documented "ammo fraction" member with
        // truthful data, which the client HUD needs to detect the >0→0
        // transition and the reload's recovery.
        float ammoFrac = ship.HullType.MaxAmmo > 0 ? ship.Ammo / (float)ship.HullType.MaxAmmo : 0f;
        u.Bytes[42] = CompactQuant.Percent(Math.Clamp(ammoFrac, 0f, 1f));   // ammo
        // P377 — the FUEL slot (a coasting lifepod has no real fuel): the port
        // keeps fuel at a flat 1.0 for every remote ship (no fuel pool exists),
        // but the OWNING peer's own payload (`carryEndurance`) instead carries
        // the real remaining-oxygen fraction (Ship.LifepodEnduranceFraction,
        // native IshipIGC::GetEndurance) while the pod is adrift — so the
        // driver's HUD shows the true server-computed countdown and nobody
        // else's does (self-only, mirroring native's ModelData::GetEndurance
        // gate). Time base is the sim clock (Mission.LastUpdate == now when the
        // broadcast step runs), not the wire uint tick slot.
        u.Bytes[43] = CompactQuant.Percent(carryEndurance
            ? ship.LifepodEnduranceFraction(ship.Mission?.LastUpdate ?? 0.0)
            : 1.0f);   // fuel (owner-lifepod → oxygen fraction; else flat 1.0)
        // P461 — energy is now REAL: P461 added the in-flight recharge
        // (Ship.OnUpdate, shipIGC.cpp:295-303), so the pool drains as energy
        // weapons fire / a cloak draws and recovers every tick while flying.
        // Previously hardcoded to 1.0 (a flight-demo constant), so the own-ship
        // energy gauge never reflected the pool — the same 47-byte slot, this
        // only fills the documented "energy fraction" member with truthful data
        // the client HUD needs to show the drain and the recharge's recovery.
        // P467b — the wire carries the FRACTION; the denominator is the
        // GA-scaled effective ceiling (native pfractions->SetEnergy(
        // m_myHullType.GetMaxEnergy(), m_energy), shipIGC.h:1124) so a
        // c_gaMaxEnergy bump widens the ceiling without inflating the 0..1 readout.
        float eMax = ship.EffectiveMaxEnergy();
        float energyFrac = eMax > 0f ? ship.Energy / eMax : 0f;
        u.Bytes[44] = CompactQuant.Percent(Math.Clamp(energyFrac, 0f, 1f));   // energy
        u.Bytes[45] = CompactQuant.Percent(Math.Clamp(ship.Ore / ship.EffectiveMineCapacity(), 0f, 1f));   // ore (P55: real cargo, shipIGC.h:1125,1148 SetOre(GetOreCapacity(), m_fOre) — GA-scaled capacity, P477)
        u.Bytes[46] = CompactQuant.Percent(1.0f);   // power
        return u;
    }

    /// <summary>
    /// Build a canonical <see cref="ServerShipUpdate"/> from already-decoded
    /// rich kinematic values (P347). This is the client-side consumer half of
    /// the periodic cross-side visibility sync: a <see cref="HeavyShipUpdateEntry"/>
    /// carries the same rich fields as a single-ship update but compressed as
    /// deltas from the enclosing message's references, so the client reconstructs
    /// the absolutes and re-quantizes them into the 47-byte payload the
    /// render/target path reads via <c>ShipEntity.Latest</c>. Like
    /// <see cref="FromModel"/>, the rich values are re-quantized immediately so
    /// the stored bytes equal what a wire decode would produce (round-trip
    /// exact by construction).</summary>
    public static ServerShipUpdate FromValues(
        short shipId, int stateM, ControlData controls, uint time,
        Vector3 position, Orientation orientation, Vector3 velocity,
        float[] turnRates,
        float hullFrac, float shieldFrac, float ammoFrac, float fuelFrac,
        float energyFrac, float oreFrac, float power)
    {
        var u = new ServerShipUpdate();
        CompactQuant.WriteShort(u.Bytes, 0, shipId);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Bytes.AsSpan(2), unchecked((ushort)stateM));
        u.Bytes[4] = CompactQuant.ControlAxis(controls.Yaw);
        u.Bytes[5] = CompactQuant.ControlAxis(controls.Pitch);
        u.Bytes[6] = CompactQuant.ControlAxis(controls.Roll);
        u.Bytes[7] = CompactQuant.ControlAxis(controls.Throttle);
        BinaryPrimitives.WriteUInt32LittleEndian(u.Bytes.AsSpan(8), time);
        WriteVec(u.Bytes.AsSpan(12), position);
        CompactOrientationCodec.Encode(orientation, u.Bytes.AsSpan(24, 8));
        CompactQuant.EncodeVelocity(velocity, u.Bytes.AsSpan(32, 5));
        u.Bytes[37] = CompactQuant.TurnRate(turnRates[0]);
        u.Bytes[38] = CompactQuant.TurnRate(turnRates[1]);
        u.Bytes[39] = CompactQuant.TurnRate(turnRates[2]);
        u.Bytes[40] = CompactQuant.Percent(Math.Clamp(hullFrac, 0f, 1f));   // hull
        u.Bytes[41] = CompactQuant.Percent(Math.Clamp(shieldFrac, 0f, 1f)); // shield
        u.Bytes[42] = CompactQuant.Percent(Math.Clamp(ammoFrac, 0f, 1f));   // ammo
        u.Bytes[43] = CompactQuant.Percent(Math.Clamp(fuelFrac, 0f, 1f));   // fuel
        u.Bytes[44] = CompactQuant.Percent(Math.Clamp(energyFrac, 0f, 1f));// energy
        u.Bytes[45] = CompactQuant.Percent(Math.Clamp(oreFrac, 0f, 1f));   // ore
        u.Bytes[46] = CompactQuant.Percent(Math.Clamp(power, 0f, 1f));     // power
        return u;
    }

    /// <summary>Encode the 47 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(Bytes);

    /// <summary>Read a 47-byte body from the reader's current position.</summary>
    public static ServerShipUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ServerShipUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ServerShipUpdate();
        r.ReadBytes(Width).CopyTo(u.Bytes);
        return u;
    }

    private static void WriteVec(Span<byte> dst, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(8), v.Z);
    }

    /// <summary>Copy encoder inputs without quantizing or retaining the live ship.</summary>
    public static ServerShipUpdate CaptureModel(short shipId, Ship ship, uint time,
        int extraStateBits = 0, bool carryEndurance = false) => new(captured: true)
    {
        _bytes = null,
        _snapshot = ShipWireSnapshot.Capture(shipId, ship, time, extraStateBits, carryEndurance),
    };

    private byte[] Materialize()
    {
        var s = _snapshot!;
        return FromValues(s.Id, s.State, s.Controls, s.Time, s.Position, s.Orientation,
            s.Velocity, [s.Yaw, s.Pitch, s.Roll], s.Hull, s.Shield, s.Ammo,
            s.Fuel, s.Energy, s.Ore, 1f).Bytes;
    }
}
