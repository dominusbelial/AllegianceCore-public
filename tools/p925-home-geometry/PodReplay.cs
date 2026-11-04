using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.P925;

/// <summary>Deterministic controller isolation: exact logged terminal pose,
/// fresh 300s allowance; no rocks/collision/network. Loopback is separate.</summary>
public static class PodReplay
{
    public sealed record Result(int Match, short Home, short Pod, short Ship, short StationType,
        bool NativeTurn, bool Docked, double Seconds, string Stage, float[] End, int Pivots, int Enters);

    public static Result Run(StaticCore core, string art, JsonElement sample, bool nativeTurn)
    {
        Vector3 V(string key) { var a = sample.GetProperty(key).EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new(a[0], a[1], a[2]); }
        short id = sample.GetProperty("Ship").GetInt16();
        var type = core.StationTypes[sample.GetProperty("StationType").GetInt16()];
        var hull = core.HullTypes[sample.GetProperty("PodHull").GetInt16()];
        var so = new Orientation(); so.Set(V("StationForward"), V("StationUp"));
        var data = new DataStationIGC([V("StationPosition").X, V("StationPosition").Y, V("StationPosition").Z],
            [so.GetUp().X, so.GetUp().Y, so.GetUp().Z], [so.GetForward().X, so.GetForward().Y, so.GetForward().Z],
            [0, 0, 0, 1], sample.GetProperty("HomeSector").GetInt16(), 0, 1, type.StationTypeId, 255, 255, type.Name);
        var station = new Station(data, type);
        var bay = HullLandingBay.ReconstructStation(CvhReader.Parse(File.ReadAllText(Path.Combine(art, type.ModelName + ".cvh"))), type.Radius);
        var orientation = new Orientation(); orientation.Set(V("EndForward"), V("EndUp"));
        var pod = new Ship(id, hull, V("EndPosition"), V("EndVelocity"), orientation)
        { SideId = 0, Hull = CvhReader.Parse(File.ReadAllText(Path.Combine(art, hull.ModelName + ".cvh"))) };
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "m2xspiderw.igc")));
        using var mission = Mission.Assemble(core, map, [new SideSpec(0, "sample", (short)(sample.GetProperty("Side").GetInt16() == 0 ? 18 : 4)), new SideSpec(1, "other", 3)], new NullIgcSite());
        mission.PlaceShip(pod, mission.Sectors.Single(s => s.Id == data.ClusterId));
        var rates = sample.GetProperty("EndTurnRates").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        for (int axis = 0; axis < 3; axis++) pod.SetTurnRate(axis, rates[axis]);
        var bays = bay.LandSlots.Select(s => new DockApproach.DockBay(s.Planes[0].Position * so, s.Planes[0].Direction.Normalize() * so)).ToArray();
        int pivots = 0, enters = 0; bool radius = false;
        for (int tick = 0; tick < 6000; tick++)
        {
            float speed = pod.Velocity.Length();
            float rest = DockApproach.StoppingDistance(speed, pod.EffectiveThrust(), pod.Mass, pod.EffectiveMaxSpeed(), hull.BackMultiplier);
            var step = DockApproach.DockStation(pod.Position, pod.Velocity, pod.Orientation, station.Center, bays, bays.Length,
                rest, speed > .1f ? pod.Position + pod.Velocity * (rest / speed) : pod.Position,
                type.Radius + hull.Length / 2 + 10, pod.TurnRates[ControlData.AxisYaw], pod.TurnRates[ControlData.AxisPitch], false, id);
            if (step.IsPivot()) pivots++;
            if (step.IsEnter()) enters++;
            var turn = DockApproach.TurnState.FromShip(pod);
            pod.Controls = nativeTurn
                ? DockApproach.Steer(step, pod.Position, pod.Orientation, pod.Velocity, rest, dt: .05f, turnState: turn)
                : DockApproach.Steer(step, pod.Position, pod.Orientation, pod.Velocity, rest);
            pod.StateM = DockApproach.MovementButtons(step, pod.Position, pod.Orientation, pod.Velocity, turn);
            pod.ExecuteMove(tick * .05, (tick + 1) * .05);
            pod.Position += pod.Velocity * .05f;
            radius |= (pod.Position - station.Center).Length() <= type.Radius + hull.Length / 2;
            if ((pod.Position - station.Center).Length() <= type.Radius + hull.Length / 2 && station.InGarage(pod, pod.Position, bay))
                return End(true, (tick + 1) * .05, "bay");
        }
        return End(false, 300, radius ? "radius" : "home");
        Result End(bool docked, double seconds, string stage) => new(sample.GetProperty("Match").GetInt32(), data.ClusterId,
            hull.HullId, id, type.StationTypeId, nativeTurn, docked, seconds, stage,
            [pod.Position.X, pod.Position.Y, pod.Position.Z], pivots, enters);
    }
}
