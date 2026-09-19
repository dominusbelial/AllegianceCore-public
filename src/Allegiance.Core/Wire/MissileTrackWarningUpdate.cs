using Allegiance.Core.Wire;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P264 — the
/// per-tick, owner-only delivery of "an enemy missile is tracking YOU": a
/// boolean armed when any live in-sector missile's target is the local
/// player's ship, plus the best lock fraction among those missiles (the
/// pitch ramp). Native's client computed this warning locally by walking its
/// own IGC simulation — <c>trekigc.cpp:5025-5071</c> iterates every model in
/// the player's cluster and arms the <c>missileLockSound</c> cue for each
/// <c>OT_missile</c> whose <c>GetTarget() == trekClient.GetShip()-&gt;GetSourceShip()</c>,
/// with <c>SetPitch(0.75 + fBestLock/2)</c> — so no wire message exists to be
/// byte-faithful to. This port's MonoGame client is a pure renderer of
/// server-authoritative state with <b>no</b> static core, and the missile
/// stream it already receives (<c>FIRE_MISSILE(70)</c>,
/// <c>MISSILE_UPDATE(205)</c>, <c>MISSILE_DESTROYED(83)</c>) carries position /
/// heading only — never a target — so it cannot tell "this missile's target
/// is me" from data already in flight. The server holds the real in-flight
/// <see cref="Sim.Missile"/> objects with their real <see cref="Sim.Missile.Target"/>
/// and <see cref="Sim.Missile.LockFraction"/>, so it computes the warning and
/// streams the boolean + best lock to the target's owner only (the same
/// owner-only per-tick precedent as <see cref="MissileLockUpdate"/> (204),
/// <see cref="MissileUpdate"/> (205) and <see cref="OwnShipMiningUpdate"/> (206)).
/// <para>
/// FMID <b>208</b> sits above native's entire real FMID range (max 200 in
/// <c>src/Inc/Messages.h</c>) and above this port's prior extensions (204-207),
/// so it can never collide. Layout (our own, no native DEFINE_FEDMSG to be
/// faithful to):
/// </para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 208
///   offset 4: byte   bTracked         (1 = an in-sector enemy missile is targeting the local ship)
///   offset 5: byte   bestLockPercent  (div-240 of the best lock fraction; 0 when not tracked)
///   = 6 B fixed
/// </code>
/// Round-trip exactness is by construction (fields, like <see cref="OwnShipMiningUpdate"/>).
/// </summary>
public sealed class MissileTrackWarningUpdate : IFedMessage
{
    /// <summary>This-port-only FMID, above native's real range (max 200) and the port's prior extensions.</summary>
    public const ushort Fmid = 208;

    /// <summary>Wire width of the whole message: header(4) + bTracked(1) + bestLockPercent(1).</summary>
    public const int FixedSize = 6;

    /// <summary>True when an in-sector enemy missile's target is the local ship
    /// — the warning is armed. False disarms (no targeting missile / player left
    /// the sector / docked).</summary>
    public bool Tracked;

    /// <summary>div-240 quantized best lock fraction among the missiles tracking
    /// the local ship (native's <c>fBestLock</c>, <c>trekigc.cpp:5047-5048</c>).</summary>
    public byte BestLockPercent;

    /// <summary>The best lock fraction 0..1 (<c>BestLockPercent</c> / 240).</summary>
    public float BestLock => CompactQuant.DePercent(BestLockPercent);

    public ushort GetFmid() => Fmid;

    /// <summary>Build the owner-only warning from the server-computed armed state
    /// and best lock fraction (0..1). <paramref name="bestLock"/> is quantized to
    /// the div-240 wire byte.</summary>
    public static MissileTrackWarningUpdate From(bool tracked, float bestLock)
    {
        return new MissileTrackWarningUpdate
        {
            Tracked = tracked,
            BestLockPercent = tracked ? CompactQuant.Percent(Math.Clamp(bestLock, 0f, 1f)) : (byte)0,
        };
    }

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte(Tracked ? (byte)1 : (byte)0);
        w.WriteByte(BestLockPercent);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MISSILE_TRACK_WARNING internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MissileTrackWarningUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MISSILE_TRACK_WARNING needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSILE_TRACK_WARNING fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new MissileTrackWarningUpdate
        {
            Tracked = r.ReadByte() != 0,
            BestLockPercent = r.ReadByte(),
        };
    }
}
