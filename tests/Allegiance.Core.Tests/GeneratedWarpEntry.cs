using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>A local transit fixture pose, with the generated asteroid field intact.
/// Random population does not guarantee that an arbitrary warp approach is clear.</summary>
internal sealed record GeneratedWarpEntry(
    Sector Sector, ResolvedWarp Warp, Vector3 Holding, Vector3 Entry, Vector3 Velocity)
{
    public static GeneratedWarpEntry Find(Mission mission, DataHullTypeIGC hull)
    {
        float shipRadius = .5f * hull.Length;
        foreach (var warp in mission.Warps.Where(w => w.Destination is not null && w.Partner() is not null))
        {
            var sector = mission.Sectors.Single(s => s.Warps.Contains(warp));
            var p = warp.Warp.Position!;
            var center = new Vector3(p[0], p[1], p[2]);
            var f = warp.Warp.Forward!;
            var forward = new Vector3(f[0], f[1], f[2]);
            if (forward.LengthSquared() == 0) forward = new Vector3(1, 0, 0);
            forward = forward.Normalize();
            Vector3[] directions = [forward, -forward, new(1, 0, 0), new(-1, 0, 0),
                new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
            foreach (var direction in directions)
            {
                float radius = warp.Warp.Radius + shipRadius;
                var entry = center + direction * (.75f * radius);
                var holding = center + direction * (radius + 500f);
                // Include the short forward sweep used by the first transit tick.
                if (!Clear(sector, entry, shipRadius) || !Clear(sector, entry - direction * 4f, shipRadius)
                    || !Clear(sector, holding, shipRadius)) continue;
                if (sector.Warps.Any(w => Near(holding, w, shipRadius)
                    || (!ReferenceEquals(w, warp) && Near(entry, w, shipRadius)))) continue;
                return new(sector, warp, holding, entry, -direction * 20f);
            }
        }
        throw new InvalidOperationException("generated map has no unobstructed warp entry for the transit fixture");
    }

    public void Enter(Ship ship, double now)
    {
        ship.Position = Entry;
        ship.Velocity = Velocity;
        ship.InitClock(now);
    }

    private static bool Near(Vector3 position, ResolvedWarp warp, float shipRadius)
    {
        var p = warp.Warp.Position!;
        float radius = warp.Warp.Radius + shipRadius + 5f;
        return (position - new Vector3(p[0], p[1], p[2])).LengthSquared() <= radius * radius;
    }

    private static bool Clear(Sector sector, Vector3 position, float shipRadius)
    {
        float clearance = shipRadius + 5f;
        return sector.Mineable.All(a => (position - new Vector3(a.Data.Position[0], a.Data.Position[1], a.Data.Position[2]))
                .Length() > a.Radius + clearance)
            && sector.StationRuntimes.All(s => (position - new Vector3(s.Data.Position[0], s.Data.Position[1], s.Data.Position[2]))
                .Length() > s.Radius + clearance)
            && sector.Models.OfType<Ship>().All(s => (position - s.Position).Length() > .5f * s.HullType.Length + clearance);
    }
}
