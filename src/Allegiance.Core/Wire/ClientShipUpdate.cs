using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// The wire payload of <c>ClientShipUpdate</c> — 39 bytes (<c>igc.h:2457</c>),
/// embedded in <c>FM_C_SHIP_UPDATE</c> (24). Layout (BOT.md §Step 1):
/// <code>
///   0 uint    time
///   4 [12]    position (Vector, raw floats — client's echo)
///  16 [8]     orientation (CompactOrientation — client's echo)
///  24 ushort  stateM (CompactState)
///  26 [4]     controls (CompactControlData)  ← the actual commanded axes
///  30 [5]     velocity (CompactVelocity — echo)
///  35 [3]     turnRates (CompactTurnRates — echo)
///  38 [1]     power (BytePercentage)
/// </code>
/// For control input the server reads <c>StateM</c> + <c>Controls</c>; the
/// position/velocity/orientation slots are the client's prediction and are
/// carried verbatim (zeros/identity here). Same round-trip-exact-by-construction
/// pattern as <see cref="ServerShipUpdate"/>.
/// </summary>
public sealed class ClientShipUpdate
{
    /// <summary>Wire width, bytes.</summary>
    public const int Width = 39;

    private readonly byte[] _b = new byte[Width];

    private ClientShipUpdate() { }

    /// <summary>Client's time for this update (uint).</summary>
    public uint Time => BinaryPrimitives.ReadUInt32LittleEndian(_b);

    /// <summary>Client-reported position (echo; not authority).</summary>
    public Vector3 Position => ReadVec(4);

    /// <summary>Client-reported attitude (echo).</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(_b.AsSpan(16, 8));

    /// <summary>stateM bitmask (movement buttons live here).</summary>
    public int StateM => (int)BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(24));

    /// <summary>The commanded joystick axes — yaw/pitch/roll/throttle, each <c>char/127</c>.</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(_b[26]),
        CompactQuant.DeControlAxis(_b[27]),
        CompactQuant.DeControlAxis(_b[28]),
        CompactQuant.DeControlAxis(_b[29]));

    /// <summary>Client-reported velocity (echo).</summary>
    public Vector3 Velocity => CompactQuant.DecodeVelocity(_b.AsSpan(30, 5));

    /// <summary>Turn rates (echo), yaw/pitch/roll.</summary>
    public float[] TurnRates => new[]
    {
        CompactQuant.DeTurnRate(_b[35]),
        CompactQuant.DeTurnRate(_b[36]),
        CompactQuant.DeTurnRate(_b[37]),
    };

    /// <summary>Power (0..1).</summary>
    public float Power => CompactQuant.DePercent(_b[38]);

    private Vector3 ReadVec(int off)
    {
        var s = _b.AsSpan(off);
        return new Vector3(
            BitConverter.ToSingle(s),
            BitConverter.ToSingle(s.Slice(4)),
            BitConverter.ToSingle(s.Slice(8)));
    }

    /// <summary>
    /// Build a control-input update from the commanded state: time, the
    /// movement-button bitmask and the four joystick axes. Position/velocity
    /// echoes are zero, orientation identity, power 1.0 — none of which the
    /// server treats as authority. This is the LEGACY (pre-P508) form the
    /// current client and test bots send; the server detects it and falls back
    /// to controls-only.
    /// </summary>
    public static ClientShipUpdate FromControls(uint time, int stateM, ControlData controls) =>
        FromPose(time, Vector3.Zero, Vector3.Zero, new Orientation(), stateM, controls, new float[3], 1.0f);

    /// <summary>
    /// P507 — build a NATIVE-style update carrying the client's reported pose
    /// (position, velocity, orientation, turn rates, power) plus the commanded
    /// controls/stateM, exactly the payload a P508 client sends. Used by the
    /// server-side tests to exercise the accept/reject pipeline.
    /// </summary>
    public static ClientShipUpdate FromPose(
        uint time, Vector3 position, Vector3 velocity, Orientation orientation,
        int stateM, ControlData controls, float[] turnRates, float power)
    {
        var u = new ClientShipUpdate();
        BinaryPrimitives.WriteUInt32LittleEndian(u._b, time);
        WriteVec(u._b.AsSpan(4), position);
        CompactOrientationCodec.Encode(orientation, u._b.AsSpan(16, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(u._b.AsSpan(24), unchecked((ushort)stateM));
        u._b[26] = CompactQuant.ControlAxis(controls.Yaw);
        u._b[27] = CompactQuant.ControlAxis(controls.Pitch);
        u._b[28] = CompactQuant.ControlAxis(controls.Roll);
        u._b[29] = CompactQuant.ControlAxis(controls.Throttle);
        CompactQuant.EncodeVelocity(velocity, u._b.AsSpan(30, 5));
        u._b[35] = CompactQuant.TurnRate(turnRates[0]);
        u._b[36] = CompactQuant.TurnRate(turnRates[1]);
        u._b[37] = CompactQuant.TurnRate(turnRates[2]);
        u._b[38] = CompactQuant.Percent(power);
        return u;
    }

    /// <summary>Encode the 39 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 39-byte body from the reader's current position.</summary>
    public static ClientShipUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ClientShipUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ClientShipUpdate();
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
