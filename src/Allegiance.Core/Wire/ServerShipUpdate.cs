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

    private readonly byte[] _b = new byte[Width];

    private ServerShipUpdate() { }

    /// <summary>The embedded ship id (short).</summary>
    public short ShipId => BinaryPrimitives.ReadInt16LittleEndian(_b);

    /// <summary>stateM bitmask; values stored in the ushort as an unsigned mask (igc.h:2266).</summary>
    public int StateM => (int)BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(2));

    /// <summary>Decoded controls — four axes each <c>char/127</c>.</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(_b[4]),
        CompactQuant.DeControlAxis(_b[5]),
        CompactQuant.DeControlAxis(_b[6]),
        CompactQuant.DeControlAxis(_b[7]));

    /// <summary>Server time (uint).</summary>
    public uint Time => BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(8));

    /// <summary>World position — raw full-precision floats.</summary>
    public Vector3 Position => ReadVec(12);

    /// <summary>Attitude reconstructed from the compact quaternion.</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(_b.AsSpan(24, 8));

    /// <summary>World velocity reconstructed from the compact form.</summary>
    public Vector3 Velocity => CompactQuant.DecodeVelocity(_b.AsSpan(32, 5));

    /// <summary>Three turn rates, rad/s, in yaw/pitch/roll order.</summary>
    public float[] TurnRates => new[]
    {
        CompactQuant.DeTurnRate(_b[37]),
        CompactQuant.DeTurnRate(_b[38]),
        CompactQuant.DeTurnRate(_b[39]),
    };

    /// <summary>Hull fraction (0..1), from the 6-byte <c>CompactShipFractions</c>.</summary>
    public float HullFraction => CompactQuant.DePercent(_b[40]);
    /// <summary>Shield fraction (0..1).</summary>
    public float ShieldFraction => CompactQuant.DePercent(_b[41]);
    /// <summary>Ammo fraction (0..1).</summary>
    public float AmmoFraction => CompactQuant.DePercent(_b[42]);
    /// <summary>Fuel fraction (0..1).</summary>
    public float FuelFraction => CompactQuant.DePercent(_b[43]);
    /// <summary>Energy fraction (0..1).</summary>
    public float EnergyFraction => CompactQuant.DePercent(_b[44]);
    /// <summary>Ore fraction (0..1).</summary>
    public float OreFraction => CompactQuant.DePercent(_b[45]);

    /// <summary>Power (0..1).</summary>
    public float Power => CompactQuant.DePercent(_b[46]);

    private Vector3 ReadVec(int off)
    {
        var s = _b.AsSpan(off);
        return new Vector3(
            BitConverter.ToSingle(s),
            BitConverter.ToSingle(s.Slice(4)),
            BitConverter.ToSingle(s.Slice(8)));
    }

    /// <summary>
    /// Quantize a live ship's state into the canonical 47 bytes. Fills hull/shield/
    /// ammo/fuel/energy fractions with representative full values and power 1.0
    /// (the flight demo never drains them; the compact wire slots are still real).
    /// <see cref="Ship.Ore"/> is the exception — the P55 cargo slot (<c>byte 45</c>,
    /// <c>OreFraction = Ore/MineCapacity</c>) is real: it is zero for an idle ship
    /// and rises as the ship mines, so mining is wire-visible without a new message
    /// (the same "rides an existing message" pattern P53 used for docking).
    /// </summary>
    public static ServerShipUpdate FromModel(short shipId, Ship ship, uint time)
    {
        var u = new ServerShipUpdate();
        CompactQuant.WriteShort(u._b, 0, shipId);
        BinaryPrimitives.WriteUInt16LittleEndian(u._b.AsSpan(2), unchecked((ushort)ship.StateM));
        u._b[4] = CompactQuant.ControlAxis(ship.Controls.Yaw);
        u._b[5] = CompactQuant.ControlAxis(ship.Controls.Pitch);
        u._b[6] = CompactQuant.ControlAxis(ship.Controls.Roll);
        u._b[7] = CompactQuant.ControlAxis(ship.Controls.Throttle);
        BinaryPrimitives.WriteUInt32LittleEndian(u._b.AsSpan(8), time);
        WriteVec(u._b.AsSpan(12), ship.Position);
        CompactOrientationCodec.Encode(ship.Orientation, u._b.AsSpan(24, 8));
        CompactQuant.EncodeVelocity(ship.Velocity, u._b.AsSpan(32, 5));
        u._b[37] = CompactQuant.TurnRate(ship.TurnRates[0]);
        u._b[38] = CompactQuant.TurnRate(ship.TurnRates[1]);
        u._b[39] = CompactQuant.TurnRate(ship.TurnRates[2]);
        u._b[40] = CompactQuant.Percent(Math.Clamp(ship.Fraction, 0f, 1f));   // hull (real: damage lowers it)
        u._b[41] = CompactQuant.Percent(0.0f);   // shield
        u._b[42] = CompactQuant.Percent(1.0f);   // ammo
        u._b[43] = CompactQuant.Percent(1.0f);   // fuel
        u._b[44] = CompactQuant.Percent(1.0f);   // energy
        u._b[45] = CompactQuant.Percent(Math.Clamp(ship.Ore / Ship.MineCapacity, 0f, 1f));   // ore (P55: real cargo, shipIGC.h:1125,1148 SetOre; 0 for an idle ship)
        u._b[46] = CompactQuant.Percent(1.0f);   // power
        return u;
    }

    /// <summary>Encode the 47 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 47-byte body from the reader's current position.</summary>
    public static ServerShipUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ServerShipUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ServerShipUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }

    private static void WriteVec(Span<byte> dst, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(8), v.Z);
    }
}
