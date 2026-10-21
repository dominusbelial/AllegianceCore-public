using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private (ResearchTactics.Pilot[] Pilots, ResearchTactics.Target[] Targets, int StrikeRoster) ResearchTacticalSnapshot(short side)
    {
        ActiveBot[] bots;
        int strikeRoster;
        lock (_gate)
        {
            bots = _bots.Where(b => b.Side == side && b.Cs is not null && !b.IsPod && IsParticipating(b)).ToArray();
            // SC9 — the quorum counts the side's whole strike roster (docked or
            // launched), not just the pilots visible in this snapshot.
            strikeRoster = _bots.Count(b => b.Side == side && b.Cs is not null && !b.IsPod && IsParticipating(b)
                && IsStrikeCapable(b.FinaleBomberActive, b.Role, b.HullId));
        }
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
                    IsStrikeCapable(bot.FinaleBomberActive, bot.Role, bot.HullId)));
                // Team pilots report only what their ordinary clients can see.
                targets.AddRange(ObserveTacticalTargets(cs.World, side));
            }
        }
        return (pilots.ToArray(), targets.ToArray(), strikeRoster);
    }

    /// <summary>SC9 — a strike pilot is an anti-base-capable hull, not a requested
    /// role: the same <c>c_habmThreatToStation</c> bit the loadout selector uses to
    /// classify bombers (<c>TechLoadoutSelector.cs:52</c>; native
    /// <c>src/Igc/hullTypeIGC.h:243-246</c>, decoded at
    /// <c>src/Allegiance.Core/Data/DataHullTypeIGC.cs:203,235</c>). A bomber that
    /// relaunched on a temporary Scout/Fighter (BotManager.TechLoadouts.cs:88) is an
    /// ordinary non-strike pilot, and the same helper decides the side's roster.</summary>
    internal static bool IsStrikeCapable(bool finaleBomber, string role, short hullId)
        => (finaleBomber || role.Equals("bomber", StringComparison.OrdinalIgnoreCase))
            && Catalog.RepresentativeCore().HullTypes.TryGetValue(hullId, out var hull)
            && hull.HasCapability(DataHullTypeIGC.HabmThreatToStation);

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
            // SC9 — "damaged" is hull OR shield: a station whose shield was knocked
            // down is the best target (StationLiveState(HullFraction, ShieldFraction),
            // src/Allegiance.ClientCore/State/ClientLiveState.cs:54). Unknown live
            // state stays undamaged with -1 fractions for the log.
            float hullFraction = -1, shieldFraction = -1;
            if (world.StationLive.TryGetValue(station.StationId, out var live))
            {
                hullFraction = live.HullFraction;
                shieldFraction = live.ShieldFraction;
            }
            bool damaged = hullFraction >= 0 && (hullFraction < 1 || shieldFraction < 1);
            targets.Add(new(station.StationId, IgcObjectType.Station, station.ClusterId, Vec3(station.Position), friendly,
                damaged, Garrison: garrison, Hull: hullFraction, Shield: shieldFraction));
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
