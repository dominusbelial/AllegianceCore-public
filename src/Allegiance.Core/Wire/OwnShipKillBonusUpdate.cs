namespace Allegiance.Core.Wire;

/// <summary>
/// <c>OWN_SHIP_KILL_BONUS_UPDATE</c> (id 212) — the port's <b>owner-only</b>,
/// per-tick extension message carrying the local player's own ship's live
/// Kill Bonus (P278). It is the renderer-only client's answer to a gap in the
/// native wire: native replicates the KB streak counter to the client as
/// <c>fExperience</c> inside <c>FM_S_PLAYERINFO(10)</c>
/// (<c>appmsg.cpp:2280</c> <c>pship-&gt;SetExperience(pfmPlayerInfo-&gt;fExperience)</c>),
/// which the client's local ship replica then turns into a display via
/// <c>GetExperienceMultiplier()</c> (<c>WinTrek/console.cpp:826</c>, the
/// <c>showexperience</c> console readout <c>100·(mult−1)</c>). This port's
/// MonoGame client is a pure renderer with no static ship core and no
/// server-side ship object to read <see cref="Sim.Ship.KillBonus"/> from, so
/// the server streams the value to the owning peer, following the exact
/// owner-only per-tick precedent P230 (<c>MissileLockUpdate</c> 204), P255
/// (<c>OwnShipMiningUpdate</c> 206) and P266 (<c>LeadIndicatorUpdate</c> 210)
/// set for state native's client derived locally but this client cannot.
/// <para>
/// The message carries the two values the HUD gauge needs in one shot:
/// <see cref="KillBonusPercent"/> (the <c>50·c/(c+4)</c> readout, 0..50, cap 50 —
/// native's <c>100·(GetExperienceMultiplier()−1)</c>) and the raw streak count
/// <see cref="Streak"/> (native's <c>m_experience</c> counter, integral here).
/// Both are server-computed from the real <see cref="Sim.Ship"/> each tick; no
/// client-side formula duplication.
/// </para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 212
///   offset 4: float  killBonusPercent (0..50)
///   offset 8: short  streak           (kill count, 0..)
///   = 10 B fixed, no var items → cbmsg = 10
/// </code>
/// </summary>
public sealed class OwnShipKillBonusUpdate : IFedMessage
{
    public const ushort Fmid = 212;

    /// <summary>Wire width of the whole message: header(4) + float(4) + short(2)
    /// = 10 bytes. Natural alignment (float 4-aligned at offset 4, short at 8)
    /// introduces no gaps.</summary>
    public const int FixedSize = 10;

    /// <summary>The live Kill Bonus readout (native <c>100·(GetExperienceMultiplier()−1)</c>),
    /// 0..50 with cap 50.</summary>
    public float KillBonusPercent;

    /// <summary>The raw kill-streak counter (native <c>m_experience</c>), the
    /// number of eligible non-allied kills in the current streak.</summary>
    public short Streak;

    public ushort GetFmid() => Fmid;

    /// <summary>
    /// Build the owner-only KB update from a live ship's real values (the same
    /// <see cref="Sim.Ship.KillBonusPercent"/>/<see cref="Sim.Ship.KillBonus"/>
    /// P278 computes). Sent every tick, always present (never NA — a 0% streak
    /// is a meaningful, truthful readout).
    /// </summary>
    public static OwnShipKillBonusUpdate FromShip(Sim.Ship ship) => new()
    {
        KillBonusPercent = ship.KillBonusPercent,
        Streak = (short)ship.KillBonus,
    };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                                            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(BitConverter.SingleToUInt32Bits(KillBonusPercent));
        w.WriteI16(Streak);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"OWN_SHIP_KILL_BONUS_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static OwnShipKillBonusUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"OWN_SHIP_KILL_BONUS_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected OWN_SHIP_KILL_BONUS_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                             // header
        return new OwnShipKillBonusUpdate
        {
            KillBonusPercent = BitConverter.UInt32BitsToSingle(r.ReadU32()),
            Streak = r.ReadI16(),
        };
    }
}
