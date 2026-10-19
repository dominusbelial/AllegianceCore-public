using Allegiance.Core.Model;
using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Native construction envelope and transformation clock
/// (Igc/buildingeffectigc.h:19-22,44-54,160-225,312-351).</summary>
public sealed class BuildingEffect : IBuildingEffectIgc
{
    public const double BuildDuration = 30;
    public Asteroid Target { get; }
    public Ship Builder { get; }
    public DataStationTypeIGC StationType { get; }
    public short SideId { get; }
    public double CompletionTime { get; }
    public double StartTime { get; }
    public double EndTime => StartTime + 40 / TimeScale;
    public double TimeScale { get; }
    public Vector3 PositionStart { get; }
    public Vector3 PositionStop { get; }
    public Vector3 Position { get; private set; }
    public float Radius { get; private set; } = 1;
    public float InnerAlpha { get; private set; } = .5f;
    public float InnerIntensity { get; private set; } = 1;
    public float OuterIntensity { get; private set; } = 1;
    public bool IsTerminated { get; private set; }
    public bool ConstructionCompleted { get; internal set; }
    // Capture the audience at creation; a later eye must not rewrite history.
    public HashSet<short> InformedSides { get; } = [];

    public BuildingEffect(Asteroid target, Ship builder, DataStationTypeIGC stationType,
        short sideId, double completionTime, double timeScale = 1)
    {
        Target = target; Builder = builder; StationType = stationType; SideId = sideId;
        CompletionTime = completionTime; TimeScale = timeScale;
        StartTime = completionTime - BuildDuration / timeScale;
        PositionStart = builder.Position + builder.Orientation.GetForward() * (builder.HullType.Length * .5f);
        PositionStop = new Vector3(target.Data.Position[0], target.Data.Position[1], target.Data.Position[2]);
        Position = PositionStart;
    }

    public void Terminate() => IsTerminated = true;

    public void AsteroidUpdate(double now)
    {
        if (!ConstructionCompleted && !IsTerminated && now >= CompletionTime)
            Target.Sector?.CompleteConstruction(this, now);
    }

    public void Update(double now)
    {
        if (IsTerminated) return;
        if (now >= EndTime) { Terminate(); return; }
        var phase = ConstructionPhase.At((now - StartTime) * TimeScale, PositionStart, PositionStop, Target.Radius, StationType.Radius);
        Position = phase.Position; Radius = phase.Radius; InnerAlpha = phase.InnerAlpha;
        InnerIntensity = phase.InnerIntensity; OuterIntensity = phase.OuterIntensity;
    }
}
