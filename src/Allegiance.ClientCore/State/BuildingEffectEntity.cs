using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live in-progress station construction in the client's world model — one
/// per real building effect, keyed by <see cref="AsteroidId"/> (the target rock
/// being transformed; native carries this as the export's
/// <c>asteroidID</c>, <c>igc.h:2594</c>). Populated from the creation
/// <c>FM_S_EXPORT(26)/OT_buildingEffect</c> broadcast
/// (<see cref="Wire.ExportBuildingEffect"/>, P294) and removed by the server's
/// <c>FM_S_BUILDINGEFFECT_DESTROYED(181)</c> (interruption) or by the same
/// client-side self-expiry native uses (a build that completes transmits
/// nothing — the client reconstructs the 4-phase animation from
/// <see cref="Wire.DataBuildingEffectExport.TimeStart"/> and the fixed phase
/// constants and terminates at <c>m_timeComplete</c>,
/// <c>buildingeffectigc.h:160-165</c>).
/// </summary>
/// <remarks>
/// Like a probe/mine (<see cref="ProbeEntity"/>/<see cref="MineEntity"/>), the
/// building effect's placement is static — it sits over the target asteroid —
/// but unlike them it is a <b>transient</b> object with a timed lifespan
/// (<see cref="ConstructionPhase.Duration"/> seconds), so it carries a
/// <see cref="ReceivedLocalMs"/> anchor exactly like <see cref="ProjectileEntity"/>
/// and is pruned when it completes. <see cref="ProgressFraction"/> drives the
/// "under construction" reveal (the renderer grows/scales it toward the
/// finished station, mirroring native's grow/shrink phases without a periodic
/// server push).
/// </remarks>
public sealed class BuildingEffectEntity
{
    /// <summary>The target asteroid's ObjectID (<c>AsteroidID</c> = short, the
    /// key — the rock being transformed into a station).</summary>
    public short AsteroidId;

    /// <summary>The sector (cluster) this construction lives in — the draw
    /// loop's sector filter key, exactly like <c>ProbeEntity.ClusterId</c>.</summary>
    public short ClusterId;

    /// <summary>The target asteroid's radius (<c>DataBuildingEffectExport.radiusAsteroid</c>)
    /// — the starting scale of the growing construction.</summary>
    public float RadiusAsteroid;

    /// <summary>The finished station's radius (<c>radiusStation</c>) — the
    /// final scale of the construction, which reaches it as
    /// <see cref="ProgressFraction"/> → 1.</summary>
    public float RadiusStation;

    /// <summary>The envelope-start position (<c>positionStart</c>; native
    /// places it at the Builder's forward surface). Not where the station forms — see
    /// <see cref="PositionStop"/>.</summary>
    public Vector3 PositionStart;

    /// <summary>The asteroid position (<c>positionStop</c>) — where the
    /// construction sits and the finished station is placed.</summary>
    public Vector3 PositionStop;

    /// <summary>The local monotonic instant this export was received, in ms —
    /// the anchor for <see cref="ProgressFraction"/> and pruning (same
    /// receipt-anchor convention as <see cref="ProjectileEntity"/>).</summary>
    public long ReceivedLocalMs;

    /// <summary>Construction progress in [0,1]: 0 = just started, 1 =
    /// complete. Computed purely from elapsed wall-clock since receipt and
    /// <see cref="ConstructionPhase.Duration"/> (the sum of native's four
    /// 10-second phase constants), clamped to [0,1].</summary>
    public ConstructionPhase Phase(long nowLocalMs) => ConstructionPhase.At(
        (nowLocalMs - ReceivedLocalMs) / 1000.0, PositionStart, PositionStop, RadiusAsteroid, RadiusStation);

    public float ProgressFraction(long nowLocalMs)
    {
        long elapsed = nowLocalMs - ReceivedLocalMs;
        float p = (float)elapsed / (float)(ConstructionPhase.Duration * 1000.0);
        return Math.Clamp(p, 0f, 1f);
    }
}
