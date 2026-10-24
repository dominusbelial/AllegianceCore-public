using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live deployed probe in the client's world model — one per real probe,
/// keyed by <see cref="ProbeId"/> (probes carry a real ObjectID on the wire,
/// P224's <c>DataProbeExport</c>). A probe is a <b>static</b> object: it never
/// moves (it sits exactly where it was deployed), so unlike a
/// <see cref="MissileEntity"/> it carries no per-tick streamed
/// position/heading — the EXPORT's <see cref="Position"/> is final for its
/// lifetime. Populated from the creation <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c>
/// broadcast and removed by the server's <c>FM_S_PROBE_DESTROYED(85)</c> (sent
/// on real destruction or lifespan expiry), so a <see cref="ProbeEntity"/>'s
/// presence in the world mirrors a live probe's survival — the client-side
/// substrate the probe ambient-sound and deploy cues read.
/// </summary>
/// <remarks>
/// The P260 client is a pure renderer/observer with no static core (same as
/// P236/P237), so the probe's per-type decoded <c>DataProbeTypeIGC.AmbientSound</c>
/// SoundID cannot be resolved to an asset client-side; the ambient/deploy cues
/// use the documented single-real-default convention (see
/// <c>Allegiance.ClientCore.MonoGame.Audio.ProbeAmbientSoundCurve</c>). What the
/// wire <em>does</em> carry and this entity preserves is the probe's real
/// <see cref="SideId"/> (owner) and <see cref="LauncherShipId"/> (deployer),
/// which the sound driver uses to attribute the deploy one-shot to the local
/// ship and the ambient loop to the player's own side.
/// </remarks>
public sealed class ProbeEntity
{
    /// <summary>The probe's ObjectID (<c>ProbeId</c> = short, the key).</summary>
    public short ProbeId;

    /// <summary>Native BytePercentage health, appmsg.cpp:584-606.</summary>
    public float Fraction { get; internal set; } = 1f;

    /// <summary>The side that owns this probe (<c>DataProbeExport.SideId</c>) —
    /// used to gate the ambient loop to probes owned by the player's own side.</summary>
    public short SideId;

    /// <summary>The deploying ship's id (<c>DataProbeExport.ShipId</c>, the
    /// <c>m_launcher</c>) — <c>NA</c>(-1) if the probe has no launcher. Used to
    /// attribute the deploy sound to the local ship's own drop
    /// (<c>LauncherShipId == MyShipId</c>), mirroring native's
    /// <c>if (pShip == m_ship) PlaySoundEffect(deployProbeSound, pShip)</c>
    /// (<c>clintlib.cpp:3245-3257</c>).</summary>
    public short LauncherShipId;

    /// <summary>The sector (cluster) this probe lives in — the draw/sound loop's
    /// sector filter key, exactly like <c>DataWarpIGC.ClusterId</c> / asteroids.</summary>
    public short ClusterId;

    /// <summary>
    /// P460 — the probe's type id (<c>DataProbeExport.ProbetypeId</c>, the
    /// <c>ExpendableTypeID</c>). The P260 entity dropped it (sound attribution
    /// only needed SideId/ShipId); the starmap's own-probe count re-reads it to
    /// resolve the probe type's <c>EabmCapabilities</c> from the core's probe-type
    /// table so the native capability filter (no capabilities, or
    /// <c>&gt;= c_eabmRescue</c>) can be applied faithfully.
    /// </summary>
    public short ProbetypeId;

    /// <summary>The probe's position (<c>DataProbeExport.P0</c>) — fixed for the
    /// probe's whole life (a probe never moves, <c>probeigc.cpp:115,142</c>).</summary>
    public Vector3 Position;

    /// <summary>
    /// P786 — the probe's native <c>m_time0</c> deploy instant, in server
    /// mission seconds (<c>DataProbeExport.Time0</c> is the wire's uint server
    /// millisecond tick; the export sets it from the live probe at creation,
    /// <c>probeigc.cpp:542</c> <c>pdme-&gt;time0 = m_time0</c>, with
    /// <c>m_time0 = now</c> at construction <c>probeigc.cpp:47</c>). This is
    /// the value native's <c>GetTimeFraction</c> needs
    /// (<c>probeigc.h:271-279</c>: <c>(m_timeExpire − now) / (m_timeExpire −
    /// m_time0)</c>, <c>m_timeExpire = m_time0 + lifespan</c>) — the radar
    /// probe contact's lifetime fill bar. Interpreted against
    /// <see cref="ClientWorld.SimulationTimeSeconds"/> (both server-mission
    /// seconds, the same clock the treasure entities use).
    /// </summary>
    public double Time0ServerSeconds;
}
