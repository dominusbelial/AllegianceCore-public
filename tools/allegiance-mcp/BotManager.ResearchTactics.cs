using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private (ResearchTactics.Pilot[] Pilots, ResearchTactics.Target[] Targets) ResearchTacticalSnapshot(short side)
    {
        ActiveBot[] bots;
        lock (_gate) bots = _bots.Where(b => b.Side == side && b.Cs is not null && !b.IsPod && IsParticipating(b)).ToArray();
        var pilots = new List<ResearchTactics.Pilot>();
        var targets = new List<ResearchTactics.Target>();
        foreach (var bot in bots)
        {
            var cs = bot.Cs!;
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.IsDead
                    || own.StationId >= 0 || own.ParentId >= 0 || own.Latest is not { HullFraction: > 0 } pose) continue;
                bool support = IsSupport(bot);
                if (support && !HasNanite(bot)) continue;
                pilots.Add(new(bot.ShipId, own.SectorId, pose.Position, support,
                    bot.FinaleBomberActive || bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)));
                // Team pilots report only what their ordinary clients can see.
                targets.AddRange(ObserveTacticalTargets(cs.World, side));
            }
        }
        return (pilots.ToArray(), targets.ToArray());
    }

    internal static ResearchTactics.Target[] ObserveTacticalTargets(ClientWorld world, short side)
    {
        var targets = new List<ResearchTactics.Target>();
        var core = Catalog.RepresentativeCore();
        foreach (var station in world.Stations.Values.Where(s => s.SideId >= 0 && s.Position is { Length: 3 }))
        {
            bool friendly = station.SideId == side || world.AreAllied(side, station.SideId);
            // SC7 — the Garrison (toughest hull) is the station-type CLASS 0
            // (stationtypeigc.h GetClassID returns m_data.classID; every civ's
            // initial station — its garrison — is class 0, verified across the
            // showcase race matrix). We deprioritise it for strikes.
            bool garrison = core.StationTypes.TryGetValue(station.StationTypeId, out var st) && st.ClassId == 0;
            targets.Add(new(station.StationId, IgcObjectType.Station, station.ClusterId, Vec3(station.Position), friendly,
                world.StationLive.TryGetValue(station.StationId, out var live) && live.HullFraction is > 0 and < 1,
                Garrison: garrison));
        }
        foreach (var ship in world.Ships.Values.Where(s => s.SideId >= 0 && !s.IsDead && !s.Unknown && !s.IsNotVisible
            && s.StationId < 0 && s.ParentId < 0 && s.SectorId >= 0 && s.Latest is { HullFraction: > 0 }
            && !Catalog.IsLifepodHull(s.HullId)))
            targets.Add(new(ship.ShipId, IgcObjectType.Ship, ship.SectorId, ship.Latest!.Position,
                ship.SideId == side || world.AreAllied(side, ship.SideId), ship.Latest.HullFraction < 1,
                CombatTargetSelector.ClassifyTargetId(ship.HullId) == BotTargetClass.Bomber));
        // SC7 — warps (alephs) as rally points. DestinationSector = the FAR side
        // (the partner warp's cluster), so the strike policy can pick the aleph
        // that LEADS INTO the target sector. Warps are neutral/shared -> Friendly.
        foreach (var warp in world.Warps.Values)
        {
            short dest = -1;
            if (world.Warps.TryGetValue(warp.DestinationId, out var partner)) dest = partner.ClusterId;
            targets.Add(new(warp.WarpId, IgcObjectType.Warp, warp.ClusterId, Vec3(warp.Position), true,
                DestinationSector: dest));
        }
        return targets.ToArray();
    }
}
