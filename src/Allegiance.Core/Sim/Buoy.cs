using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Mission-owned waypoint identity (Igc/buoyigc.cpp:34-85).</summary>
public sealed class Buoy : Model
{
    public readonly record struct Visual(short Id, short SectorId, Vector3 Position, float Radius);
    public DataBuoyIGC Data { get; }
    internal Allegiance.Core.Model.Sector? PickableSector;
    // Igc/buoyigc.cpp:39-54; buoyigc.h:67-88. Lifetime is distinct from
    // managed reachability: the last command/action consumer removes the model.
    public float Radius => Data.Visible ? 20f : 2f;
    public int ConsumerCount { get; private set; }
    public bool Visible => Data.Visible && ConsumerCount > 0;
    public void AddConsumer() => ConsumerCount++;
    public void ReleaseConsumer()
    {
        if (ConsumerCount <= 0) throw new InvalidOperationException("Buoy has no consumer to release.");
        if (--ConsumerCount == 0) ThingSiteMission?.DeleteBuoy(this);
    }
    public Buoy(DataBuoyIGC data) : base(data.BuoyId,
        new Vector3(data.Position[0], data.Position[1], data.Position[2]))
    {
        Data = data;
        SideVisibility.Predictable = true;
    }
    protected override void OnUpdate(double now, double dt) { }
}
