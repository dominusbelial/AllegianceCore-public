namespace Allegiance.Core.Wire;

/// <summary>
/// <c>OWN_SHIP_CLOAK_UPDATE</c> (id 220, this-port-only) — the port's
/// <b>owner-only</b>, per-tick extension carrying the local player's own ship's
/// continuous cloak fraction. It is the renderer-only client's answer to a gap
/// in the native wire: native's client simulates its own ship, so
/// <c>CshipIGC::GetCloaking()</c> — the visible fraction the HUD reads
/// (<c>WinTrek/consoledata.cpp:476-489,563-575</c>) — is available locally.
/// This port's client is a pure renderer with no ship sim, and the 47-byte
/// <c>ServerShipUpdate</c> carries only the <c>cloakActiveIGC</c> state bit
/// (0/1), not the ramp. The server therefore streams the sim's authoritative
/// <see cref="Allegiance.Core.Sim.Ship.Cloaking"/> (the direct port of
/// <c>CcloakIGC::Update</c>, <c>Igc/cloakigc.cpp:85-122</c>) on the same
/// per-tick owner-only pattern P230/P255/P266 set. (P773 note: the P298
/// kill-bonus stream that once shared this list is retired — native has no
/// such message; the client derives its own kill bonus from the experience
/// the standard PLAYERINFO/KILL_SHIP path already carries.)
/// <para>
/// <b>Value.</b> <see cref="Cloaking"/> is the native <c>GetCloaking()</c>
/// VISIBLE fraction: 1.0 = fully visible, falling toward
/// <c>1 − MaxCloaking</c> while the emitter ramps up
/// (<c>cloakigc.cpp:105-116,121</c>). Native HUD formulas follow directly:
/// <c>CloakStrength = 100·(1 − Cloaking)</c> (<c>consoledata.cpp:480-489</c>)
/// and <c>IsCloaked = Cloaking &lt; 1.0</c> (<c>consoledata.cpp:563-575</c>,
/// <c>dialog.mdl:2537-2547</c>) — the second being why the cloak icon stays
/// lit through the decloak ramp instead of clearing with the state bit.
/// </para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 220
///   offset 4: float  cloaking  (visible fraction, 0..1; 1 = fully visible)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// </summary>
public sealed class OwnShipCloakUpdate : IFedMessage
{
    public const ushort Fmid = 220;

    /// <summary>Wire width: header(4) + float(4) = 8 bytes, no padding.</summary>
    public const int FixedSize = 8;

    /// <summary>Native <c>GetCloaking()</c>: the visible fraction (1 = fully
    /// visible, lower = more cloaked).</summary>
    public float Cloaking = 1f;

    public ushort GetFmid() => Fmid;

    /// <summary>Build the owner-only cloak update from the live ship's sim value
    /// (<see cref="Allegiance.Core.Sim.Ship.Cloaking"/>, the port of
    /// <c>cloakigc.cpp:121</c>).</summary>
    public static OwnShipCloakUpdate FromShip(Sim.Ship ship) => new() { Cloaking = ship.Cloaking };

    /// <summary>Native cloak strength percentage
    /// (<c>100·(1 − GetCloaking())</c>, <c>consoledata.cpp:480-489</c>).</summary>
    public static float StrengthPercent(float cloaking) => 100f * (1f - Math.Clamp(cloaking, 0f, 1f));

    /// <summary>Native <c>IsCloaked</c> (<c>GetCloaking() &lt; 1.0</c>,
    /// <c>consoledata.cpp:563-575</c>) — true for any nonzero cloak ramp,
    /// including the decloak tail.</summary>
    public static bool IsCloaked(float cloaking) => cloaking < 1.0f;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                                              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(BitConverter.SingleToUInt32Bits(Cloaking));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"OWN_SHIP_CLOAK_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static OwnShipCloakUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"OWN_SHIP_CLOAK_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected OWN_SHIP_CLOAK_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new OwnShipCloakUpdate
        {
            Cloaking = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(4))),
        };
    }
}
