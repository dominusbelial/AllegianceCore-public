using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Browser input adapter over the same visible world the scene draws.
/// No new visibility rule. Native targeting: WinTrek.cpp:10127-10355,10440-10449;
/// nearest/next/previous and role masks mirror the desktop Game1.Targeting path.
/// The port's general T/reticle picker keeps P256's ship/warp/station/rock tiers.</summary>
public static class BrowserTargeting
{
    public sealed record Choice(IgcObjectType Type, short Id, string Name, SimVec Position,
        SimVec Velocity, short Sector, short? Side = null, short Roles = 0);

    public static IReadOnlyList<Choice> Gather(ClientWorld world, ShopCatalog catalog, short ownId)
    {
        if (!world.Ships.TryGetValue(ownId, out var own)) return [];
        var choices = new List<Choice>();
        foreach (var ship in world.ShipsInSector(own.SectorId))
        {
            if (ship.ShipId == ownId || ship.StationId >= 0 || !ship.CanDrawInSector(own.SectorId) || ship.Latest is not { } pose) continue;
            short roles = catalog.Core?.HullTypes.GetValueOrDefault(ship.HullId).HabmCapabilities ?? 0;
            choices.Add(new(IgcObjectType.Ship, ship.ShipId, ship.CharacterName, pose.Position, pose.Velocity, own.SectorId,
                ship.HasRealIdentity ? ship.SideId : null, roles));
        }
        foreach (var station in world.Stations.Values)
            choices.Add(new(IgcObjectType.Station, station.StationId, station.Name, Vec(station.Position), default, station.ClusterId, station.SideId,
                catalog.Core?.StationTypes.GetValueOrDefault(station.StationTypeId).SabmCapabilities ?? 0));
        foreach (var warp in world.Warps.Values.Where(w => w.ClusterId == own.SectorId))
            choices.Add(new(IgcObjectType.Warp, warp.WarpId, warp.Name, Vec(warp.Position), default, warp.ClusterId));
        foreach (var rock in world.Asteroids.Values.Where(r => r.ClusterId == own.SectorId))
            choices.Add(new(IgcObjectType.Asteroid, rock.AsteroidId, rock.Name, Vec(rock.Position), default, rock.ClusterId));
        return choices;
    }

    public static Choice? Select(string action, IReadOnlyList<Choice> choices, Choice? selected,
        SimVec position, short sector, short side, short ownRoles,
        Func<short, short, bool> allied, Func<Choice, (float Sx, float Sy)?> projectToScreen)
    {
        bool Friendly(Choice c) => c.Side is { } s && (s == side || allied(side, s));
        IEnumerable<Choice> pool = choices;
        bool bases = action.Contains("Base", StringComparison.Ordinal);
        bool friendly = action.Contains("Friendly", StringComparison.Ordinal) || action.Contains("Allied", StringComparison.Ordinal);
        bool enemy = action.Contains("Enemy", StringComparison.Ordinal);
        if (!bases) pool = pool.Where(c => c.Sector == sector);
        if (friendly) pool = pool.Where(Friendly);
        if (enemy) pool = pool.Where(c => c.Side.HasValue && !Friendly(c));
        if (bases)
        {
            short dock = (ownRoles & DataHullTypeIGC.HabmLifepod) != 0
                ? (short)(DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmRescue | DataStationTypeIGC.SabmRescueAny)
                : (ownRoles & DataHullTypeIGC.HabmFighter) != 0 ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand;
            pool = pool.Where(c => c.Type == IgcObjectType.Station && (friendly ? (c.Roles & dock) != 0 : (c.Roles & DataStationTypeIGC.SabmPedestal) == 0));
            if (action.Contains("Major", StringComparison.Ordinal)) pool = pool.Where(c => (c.Roles & DataStationTypeIGC.SabmFlag) != 0);
        }
        else if (action.Contains("Warp", StringComparison.Ordinal)) pool = pool.Where(c => c.Type == IgcObjectType.Warp);
        else if (friendly || enemy)
        {
            short roles = action.Contains("Bomber", StringComparison.Ordinal) ? DataHullTypeIGC.HabmThreatToStation
                : action.Contains("Fighter", StringComparison.Ordinal) ? DataHullTypeIGC.HabmFighter
                : action.Contains("Constructor", StringComparison.Ordinal) ? DataHullTypeIGC.HabmBuilder
                : action.Contains("Miner", StringComparison.Ordinal) ? DataHullTypeIGC.HabmMiner
                : action.Contains("Lifepod", StringComparison.Ordinal) ? DataHullTypeIGC.HabmLifepod
                : enemy ? (short)(DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder | DataHullTypeIGC.HabmRescue) : (short)0;
            pool = pool.Where(c => c.Type == IgcObjectType.Ship && (roles == 0 || (c.Roles & roles) != 0));
        }
        else if (action is "Target" or "TargetCenter" or "TargetPrev" or "TargetNearest")
        {
            // P256 desktop SelectCandidates: actual in-frustum projection,
            // then ship/warp/station/rock tier and screen-space distance.
            var front = pool.Select(c => (Choice: c, Screen: projectToScreen(c))).Where(c => c.Screen.HasValue).ToArray();
            if (front.Length == 0) return null;
            static int Tier(Choice c) => c.Type switch { IgcObjectType.Ship => 0, IgcObjectType.Warp => 1, IgcObjectType.Station => 2, _ => 3 };
            int tier = front.Min(c => Tier(c.Choice));
            pool = front.Where(c => Tier(c.Choice) == tier)
                .OrderBy(c => c.Screen!.Value.Sx * c.Screen.Value.Sx + c.Screen.Value.Sy * c.Screen.Value.Sy).Select(c => c.Choice);
            return Step(pool.ToArray(), selected, action == "TargetCenter" || action.EndsWith("Nearest", StringComparison.Ordinal), action.EndsWith("Prev", StringComparison.Ordinal));
        }
        else return null;
        return Step(pool.OrderBy(c => (c.Position - position).LengthSquared()).ThenBy(c => c.Id).ToArray(), selected,
            action.EndsWith("Nearest", StringComparison.Ordinal), action.EndsWith("Prev", StringComparison.Ordinal));
    }

    private static Choice? Step(Choice[] pool, Choice? selected, bool nearest, bool previous)
    {
        if (pool.Length == 0) return null;
        int i = selected is null || nearest ? -1 : Array.FindIndex(pool, c => c.Type == selected.Type && c.Id == selected.Id);
        return pool[i < 0 ? 0 : (i + (previous ? pool.Length - 1 : 1)) % pool.Length];
    }
    private static SimVec Vec(float[] p) => new(p[0], p[1], p[2]);
}
