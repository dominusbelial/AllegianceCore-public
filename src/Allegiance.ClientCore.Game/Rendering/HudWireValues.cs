using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P786 (PAR-5) — the HUD wire-data values this wave wires through, as pure
/// functions over data the client already receives. Every rule here is the
/// direct port of a native client read; no new wire fields are introduced
/// (the native client gets each value the same way — see the cited messages).
/// </summary>
public static class HudWireValues
{
    /// <summary>
    /// Native <c>IprobeIGC::GetTimeFraction</c> (<c>Igc/probeigc.h:271-279</c>)
    /// / <c>ImineIGC::GetTimeFraction</c> (<c>Igc/mineigc.h:183-191</c>): the
    /// contact's remaining-lifetime fill,
    /// <c>(m_timeExpire − now) / (m_timeExpire − m_time0)</c> clamped to [0,1],
    /// with <c>m_timeExpire = m_time0 + lifespan</c>
    /// (<c>probeigc.cpp:133</c>, <c>mineigc.cpp:113</c>). The wire already
    /// carries <c>m_time0</c> — <c>DataProbeExport.Time0</c> /
    /// <c>DataMineExport.Time0</c> (<c>probeigc.cpp:542</c>,
    /// <c>mineigc.cpp:167</c>, server-side <c>pdme-&gt;time0 = m_time0</c>) —
    /// and the type's <c>lifespan</c> rides in the core the client loaded, so
    /// this is computed client-side exactly as native does. <paramref
    /// name="timeNowServerSeconds"/> is the server sim clock sample
    /// (<c>ClientWorld.SimulationTimeSeconds</c>), standing in for native's
    /// <c>GetMyLastUpdate()</c>. Returns null when the type has no usable
    /// lifespan (the native assert guard <c>m_timeExpire != m_time0</c>).
    /// </summary>
    public static float? ProbeMineTimeFraction(
        double time0ServerSeconds, double lifespanSeconds, double timeNowServerSeconds)
    {
        if (lifespanSeconds <= 0d) return null;
        double expire = time0ServerSeconds + lifespanSeconds;
        double f = (expire - timeNowServerSeconds) / lifespanSeconds;
        return (float)Math.Clamp(f, 0d, 1d);
    }

    /// <summary>
    /// Native radar asteroid fill (<c>WinTrek/radarimage.cpp:1051-1069</c>):
    /// <c>fill = GetOreSeenBySide(psideMine) / (2 * capacity)</c> clamped to 1,
    /// drawn only when the side-known ore is non-zero — "how many shiploads of
    /// ore are on the asteroid; more than two is shown as full"
    /// (radarimage.cpp:1058-1060). <c>capacity</c> is the miner shipload
    /// <c>c_fcidCapacityHe3 · GA[c_gaMiningCapacity]</c>
    /// (radarimage.cpp:576-577), NOT the asteroid's own capacity, so the same
    /// <see cref="MinerCapacityHe3"/> the miner fill bar divides by
    /// (<c>pship-&gt;GetOre() / capacity</c>, radarimage.cpp:994) is used here.
    /// The side-known ore itself is the P797 wire: the client's live asteroid
    /// fraction is only ever received for ore its side has seen
    /// (<c>AsteroidsUpdate</c>/<c>AsteroidMined</c> are gated on the side's
    /// seen state), so <paramref name="oreFractionOfMax"/> × <paramref
    /// name="oreMax"/> IS <c>GetOreSeenBySide</c>. Returns null (no bar) when
    /// the side-known ore is zero, exactly like native's <c>if (ore != 0)</c>.
    /// </summary>
    public static float? OreSeenFill(float oreFractionOfMax, float oreMax, float minerCapacityHe3)
    {
        if (minerCapacityHe3 <= 0f) return null;
        float oreSeen = Math.Clamp(oreFractionOfMax, 0f, 1f) * oreMax;
        if (oreSeen == 0f) return null;
        return MathF.Min(1f, oreSeen / (2f * minerCapacityHe3));
    }

    /// <summary>
    /// The miner-shipload capacity both fill bars divide by:
    /// <c>FloatConstants[c_fcidCapacityHe3 = 7] · GA[c_gaMiningCapacity = 19]</c>
    /// (<c>WinTrek/radarimage.cpp:576-577</c>; constant ids <c>Igc/igc.h:66</c>
    /// and <c>:613</c>, default <c>c_fCapacityHe3 = 90</c> when the core omits
    /// the constant — the same fallback <c>Sim.Ship.EffectiveMineCapacity</c>
    /// uses, <c>shipIGC.cpp:2213</c>).
    /// </summary>
    public static float MinerCapacityHe3(float? capacityHe3Constant, float miningCapacityAttribute)
        => (capacityHe3Constant ?? 90f) * miningCapacityAttribute;

    /// <summary>
    /// Native <c>PartWrapper::GetArmedFraction</c>
    /// (<c>WinTrek/consoledata.cpp:1189-1202</c>) for a mounted launcher:
    /// <c>1 − max(0, (m_timeLoaded − now) / loadTime)</c>
    /// (<c>Igc/magazineigc.h:156-165</c>, the dispenser form is identical,
    /// <c>Igc/dispenserigc.h:165-175</c>), and 0 while the part is still
    /// mounting (<c>GetMountedFraction() &lt; 1</c>). Native computes this on
    /// the client from its own simulated magazine (the fire resets
    /// <c>m_timeLoaded = now + GetLoadTime()</c>, <c>magazineigc.cpp:116</c>;
    /// the mount sets <c>m_timeLoaded = lastUpdate + 1/mountRate + loadTime</c>,
    /// <c>magazineigc.h:73</c>); this port's client is a pure renderer, so the
    /// same two clocks are recomputed from the wire events the native client
    /// would act on — its own <c>FM_CS_FIRE_MISSILE(70)</c> salvo count
    /// (<see cref="Allegiance.ClientCore.State.ClientWorld.OwnMissilesFired"/>)
    /// / own dispenser deploy and the loadout's mounted-fraction transitions.
    /// <paramref name="timeLoadedLocalSeconds"/> is <see cref="double"/>.<c>NegativeInfinity</c>
    /// when no clock was ever set — a launcher observed already mounted and
    /// never fired reads armed (the steady state a mid-session join lands in).
    /// </summary>
    public static float LauncherArmedFraction(
        float mountedFraction, double timeLoadedLocalSeconds, double loadTimeSeconds, double nowLocalSeconds)
    {
        if (mountedFraction < 1f) return 0f;
        if (double.IsNegativeInfinity(timeLoadedLocalSeconds)) return 1f;
        if (loadTimeSeconds <= 0d) return 1f;
        double remaining = Math.Max(0d, (timeLoadedLocalSeconds - nowLocalSeconds) / loadTimeSeconds);
        return (float)Math.Clamp(1d - remaining, 0d, 1d);
    }
}
