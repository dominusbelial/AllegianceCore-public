using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Mission-owned waypoint identity (Igc/buoyigc.cpp:34-85).</summary>
public sealed class Buoy : Model
{
    public DataBuoyIGC Data { get; }
    public Buoy(DataBuoyIGC data) : base(data.BuoyId,
        new Vector3(data.Position[0], data.Position[1], data.Position[2]))
    {
        Data = data;
        SideVisibility.Predictable = true;
    }
    protected override void OnUpdate(double now, double dt) { }
}
