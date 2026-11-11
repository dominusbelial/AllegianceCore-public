using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>A timed construction reservation from OT_buildingEffect. Its four
/// phase sample is shared with the simulation (Igc/buildingeffectigc.h:160-225).
/// ClientWorld.ConstructionVisuals controls presentation lifetime independently:
/// a station/asteroid hand-off removes the visual while MCP retains its original
/// timed reservation semantics in ClientWorld.BuildingEffects.</summary>
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
    /// fallback anchor for a local fixture without a session clock.</summary>
    public long ReceivedLocalMs;

    /// <summary>Native converted creation instant in the local simulation epoch.</summary>
    public double StartClientSeconds;
    internal double StartServerSeconds;
    internal Func<long, double>? ClientTimeAt;
    public double ElapsedSeconds(long nowLocalMs) => ClientTimeAt is null
        ? (nowLocalMs - ReceivedLocalMs) / 1000.0
        : ClientTimeAt(nowLocalMs) - StartClientSeconds;


    /// <summary>Construction progress in [0,1]: 0 = just started, 1 =
    /// complete. Computed from elapsed local simulation time since creation and
    /// <see cref="ConstructionPhase.Duration"/> (the sum of native's four
    /// 10-second phase constants), clamped to [0,1].</summary>
    public ConstructionPhase Phase(long nowLocalMs) => ConstructionPhase.At(
        ElapsedSeconds(nowLocalMs), PositionStart, PositionStop, RadiusAsteroid, RadiusStation);

    public float ProgressFraction(long nowLocalMs)
    {
        float p = (float)(ElapsedSeconds(nowLocalMs) / ConstructionPhase.Duration);
        return Math.Clamp(p, 0f, 1f);
    }
}
