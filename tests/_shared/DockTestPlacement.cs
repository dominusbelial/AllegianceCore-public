using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.TestInfra;

/// <summary>Placement for tests of dock wire/lifecycle behavior: use a real bay
/// interior instead of assuming a station's center is a landing point.</summary>
internal static class DockTestPlacement
{
    internal static Vector3 Inside(Mission mission, DataStationIGC data, DataHullTypeIGC hull)
    {
        var sector = mission.Sectors.Single(s => s.Id == data.ClusterId);
        var station = sector.StationRuntimes.Single(s => s.StationId == data.StationId);
        var geometry = sector.StationLandingBays?.Bay(station.Type);
        if (geometry is null) return station.Center; // deliberately geometry-less fixture
        int count = hull.HasCapability(DataHullTypeIGC.HabmFighter) ? geometry.LandSlots.Count : geometry.CapLandSlots;
        var probe = new Ship(-1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        for (int slot = 0; slot < count; slot++)
        {
            var planes = geometry.LandSlots[slot].Planes;
            var point = planes[0].Position;
            float clearance = hull.Length / 2 + 2;
            for (int n = 0; n < 64; n++)
            {
                foreach (var plane in planes)
                {
                    var normal = plane.Direction.Normalize();
                    float missing = clearance - (point - plane.Position) * normal;
                    if (missing > 0) point += normal * missing;
                }
                var world = station.Center + point * station.Orientation;
                if (station.InGarage(probe, world, geometry)) return world;
            }
        }
        throw new InvalidOperationException($"Hull {hull.HullId} cannot fit station {data.StationId}'s native bays");
    }
}
