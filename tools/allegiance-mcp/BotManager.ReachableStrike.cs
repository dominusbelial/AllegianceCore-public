using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Granted hardware is the inventory epoch; confirmed launches consume it.
    // Desired parts may describe the next purchase and must never size a wave.
    private static StrikeVolley.Loadout? FinaleGrantedStrikeLoadout(ActiveBot bot)
    {
        var core = Catalog.RepresentativeCore();
        if (bot.Granted.Parts is not { } granted) return null;
        long fired = 0;
        bool noAmmo = false;
        if (bot.Cs is { } cs)
            lock (cs.Gate)
            {
                fired = Math.Max(0, cs.World.OwnMissilesFired - bot.FinaleMissileLaunchBase);
                noAmmo = cs.World.Ships.TryGetValue(bot.ShipId, out var own) && own.Latest?.AmmoFraction <= 0;
            }
        var remaining = new List<ExpandedPartData>();
        foreach (var part in granted)
        {
            if (noAmmo && core.PartTypes.TryGetValue(part.PartId, out var gunPart)
                && gunPart.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 }) continue;
            if (part.MountId < 128 && core.Launchers.TryGetValue(part.PartId, out var launcher)
                && core.MissileTypes.ContainsKey(launcher.ExpendabletypeId))
            {
                int used = (int)Math.Min(fired, part.Amount);
                remaining.Add(part with { Amount = (short)(part.Amount - used) });
                fired -= used;
            }
            else remaining.Add(part);
        }
        return StrikeVolley.FromParts(core, ActualHullId(bot), remaining);
    }

    /// <summary>P973 — P959's "no unarmed wave" rule as one predicate: the member
    /// really carries station-damaging hardware, granted, on the hull it is mounted
    /// on. <see cref="BotManager.StrikeMass"/> counts and commits only these, so a
    /// P733 fallback member (finale DUTY with no station-damaging loadout available
    /// on its side) can never be sized into a wave, staged as assembled or released
    /// with one. Uses the granted inventory (never the desired parts, see
    /// <see cref="FinaleGrantedStrikeLoadout"/>) so a refit that has not launched yet
    /// is not mistaken for hardware. Pure apart from that read.</summary>
    private static bool FinaleCarriesStrikeHardware(ActiveBot bot, FinalePlan plan)
    {
        var core = Catalog.RepresentativeCore();
        if (!core.StationTypes.TryGetValue(plan.TargetTypeId, out var stationType)) return false;
        var loadout = FinaleGrantedStrikeLoadout(bot);
        return FinaleWingMemberReady(
            loadout is not null && StrikeVolley.DamagesStation(StrikeVolley.StationProfile.From(core, stationType), loadout),
            bot.Granted.HullId == ActualHullId(bot));
    }

    private static bool HasFinaleDestination(ActiveBot bot)
        => bot.Finale is { Rotated: false, GameOverAt: null } run
            && run.Plans.TryGetValue(bot.Side, out var plan)
            && FinaleAssembly.OwnsDestination(bot.FinaleBomberReserved, bot.FinaleEscort,
                FinaleSideArmed(run, bot.Side), plan.TargetId != FinalePlan.NoTarget && plan.KilledAt is null);

    // The experimental planner races the original finale's existing timeout,
    // including after a target change. It does not arm or extend that timeout.
    private static double ReachableFinaleDeadline(FinaleRun run, FinalePlan plan)
        => !plan.EarlyWave && run.TriggeredAt is { } trigger
            ? trigger + run.Settings.DeadlineSeconds : plan.DeadlineAt;

    // Every observation is from this side's client worlds. A crowd on the other
    // side of a home sector is not a threat to the staging/release corridor.
    private static (int Sector, int Engaging) ObserveFinaleThreats(FinaleRun run, short side, FinalePlan plan, double now)
    {
        var sectorIds = new HashSet<short>();
        var engagingIds = new HashSet<short>();
        var wing = run.Bots.Where(b => b.Side == side && b.FinaleBomberReserved && !b.IsPod).ToArray();
        foreach (var bot in wing)
        {
            if (bot.Cs is not { } cs) continue;
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } pose
                    || own.StationId >= 0 || own.IsDead) continue;
                Vector3 release = pose.Position;
                if (own.SectorId == plan.Sector && cs.World.Stations.TryGetValue(plan.TargetId, out var station)
                    && station.Position is { Length: 3 } p)
                {
                    var centre = new Vector3(p[0], p[1], p[2]);
                    var radial = pose.Position - centre;
                    float range = Math.Min(FinaleStrikeStandoff, FinaleLaunchRange(bot) * .85f);
                    if (radial.LengthSquared() > 1) release = centre + radial.Normalize() * range;
                }
                foreach (var enemy in cs.World.Ships.Values)
                {
                    if (enemy.SideId < 0 || enemy.SideId == side || enemy.IsDead || enemy.IsGone || enemy.IsNotVisible
                        || enemy.StationId >= 0 || Catalog.IsLifepodHull(enemy.HullId) || enemy.Latest is not { } enemyPose)
                        continue;
                    if (enemy.SectorId == plan.Sector) sectorIds.Add(enemy.ShipId);
                    if (enemy.SectorId != own.SectorId) continue;
                    bool attacked = bot.RecentAttackers.TryGetValue(enemy.ShipId, out double at)
                        && now - at <= ThreatWindowSeconds;
                    bool canAttack = !Catalog.RepresentativeCore().HullTypes.TryGetValue(enemy.HullId, out var hull)
                        || hull.MaxWeapons > 0 || hull.CapacityMagazine > 0;
                    if (FinaleAssembly.ThreatensPath(enemyPose.Position, pose.Position, release,
                            run.Settings.EngagementRange, attacked, canAttack)) engagingIds.Add(enemy.ShipId);
                }
            }
        }
        return (sectorIds.Count, engagingIds.Count);
    }

    // Route distance uses only known paired alephs. Unknown paths are not
    // promised. 65% of the hull's ordinary cruise speed budgets turning/dodge;
    // each transition gets another five seconds. This is a bot planning estimate.
    private static double FinaleTravelSeconds(ActiveBot bot, ClientWorld world, ShipEntity own,
        short sector, Vector3 destination)
    {
        if (own.Latest is not { } pose || world.Graph?.FindShortestPath(own.SectorId, sector) is not { Count: > 0 } path
            || !Catalog.RepresentativeCore().HullTypes.TryGetValue(own.HullId, out var hull) || hull.Speed <= 0)
            return double.PositiveInfinity;
        var point = pose.Position;
        double distance = 0;
        for (int hop = 0; hop < path.Count - 1; hop++)
        {
            var exits = world.Warps.Values.Where(w => w.ClusterId == path[hop]
                && world.Warps.TryGetValue(w.DestinationId, out var partner) && partner.ClusterId == path[hop + 1]
                && w.Position is { Length: 3 }).OrderBy(w => (Vec3(w.Position) - point).LengthSquared()).ToArray();
            if (exits.Length == 0) return double.PositiveInfinity;
            var exit = exits[0];
            distance += (Vec3(exit.Position) - point).Length();
            point = Vec3(world.Warps[exit.DestinationId].Position);
        }
        distance += Math.Max(0, (destination - point).Length() - FinaleStrikeStandoff - ResearchTactics.RallyGatherRadius);
        return distance / (hull.Speed * .65) + (path.Count - 1) * 5;
    }

    private static ActiveBot[] ReachableFinaleWing(FinaleRun run, FinalePlan plan, short side,
        IReadOnlyList<ActiveBot> armed, double now, bool forRetarget = false)
    {
        var result = new List<ActiveBot>();
        var core = Catalog.RepresentativeCore();
        var profile = StrikeVolley.Profile(core, plan.TargetTypeId);
        foreach (var bot in armed)
        {
            if (bot.Cs is not { } cs || profile is not { } target
                || FinaleGrantedStrikeLoadout(bot) is not { } loadout
                || !StrikeVolley.DamagesStation(target, loadout)) continue;
            lock (cs.Gate)
            {
                // The coordinator may communicate a station seen by a teammate;
                // demanding that every transiting pilot already sees it makes the
                // reachable count zero until the wing has arrived at the target.
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || plan.TargetPosition is not { } position)
                    continue;
                double travel = FinaleTravelSeconds(bot, cs.World, own, sector: plan.Sector, destination: position);
                if (FinaleAssembly.CanArrive(!own.IsDead && !own.IsGone && !bot.IsPod,
                        (bot.FinaleGrantVerified || forRetarget && bot.Granted.HullId == ActualHullId(bot))
                            && !bot.FinaleStrikeSpent, bot.FinaleBomberReserved,
                        own.StationId >= 0 || bot.FinaleRefitting, travel, now, ReachableFinaleDeadline(run, plan))) result.Add(bot);
            }
        }
        return result.ToArray();
    }

    private static FinaleAssembly.Demand ReachableFinaleDemand(FinaleRun run, FinalePlan plan, short side,
        IReadOnlyList<ActiveBot> armed, double now, bool forRetarget = false)
    {
        var core = Catalog.RepresentativeCore();
        var reachable = ReachableFinaleWing(run, plan, side, armed, now, forRetarget);
        var profile = StrikeVolley.Profile(core, plan.TargetTypeId);
        return profile is { } target ? FinaleAssembly.Compute(target,
            reachable.Select(b => FinaleGrantedStrikeLoadout(b)).ToArray(),
            reachable.Length, run.Settings.SurvivalMargin, plan.ReleaseLosses, plan.EngagingDefenders, plan.LandingShare,
            core.StationTypes[plan.TargetTypeId].ClassId == 0)
            : default;
    }

    private bool TryRetargetReachableFinale(FinaleRun run, FinalePlan plan, short side,
        ActiveBot[] armed, double now)
    {
        if (now - plan.LastRetargetAt < 30 || plan.GoTime is not null) return false;
        plan.LastRetargetAt = now;
        var core = Catalog.RepresentativeCore();
        var observed = ObservedFinaleStations(side, run.Bots.Where(b => b.Side == side).ToArray(), []);
        // A loadout ineffective against this garrison may already be legal and
        // effective against an outpost. Evaluate the reserved hardware for each
        // candidate before activation, rather than waiting for impossible tech.
        var candidateWing = armed.Concat(run.Bots.Where(b => b.Side == side && b.FinaleBomberReserved
            && !b.IsPod && IsParticipating(b))).Distinct().ToArray();
        var options = new List<(DataStationIGC Station, FinaleAssembly.Demand Demand)>();
        foreach (var station in observed.Where(s => s.StationId != plan.TargetId && s.SideId != side
                     && s.SideId >= 0 && core.StationTypes.ContainsKey(s.StationTypeId)))
        {
            if (HasMultipleEnemies && !MultiSideActive(station.SideId)) continue;
            var candidate = new FinalePlan(station.StationId, station.SideId, station.ClusterId, station.StationTypeId, null)
                { DeadlineAt = plan.DeadlineAt, LandingShare = plan.LandingShare,
                    TargetPosition = station.Position is { Length: 3 } p ? Vec3(p) : null };
            candidate.EngagingDefenders = ObserveFinaleThreats(run, side, candidate, now).Engaging;
            var demand = ReachableFinaleDemand(run, candidate, side, candidateWing, now, forRetarget: true);
            if (demand.ReachableKill) options.Add((station, demand));
        }
        if (options.Count == 0) return false;
        var (next, chosen) = options.OrderBy(o => o.Demand.MinimumUseful).ThenBy(o => o.Demand.Required)
            .ThenBy(o => o.Station.StationId).First();
        short previous = plan.TargetId;
        plan.TargetId = next.StationId; plan.TargetSide = next.SideId;
        plan.TargetTypeId = next.StationTypeId; plan.Sector = next.ClusterId;
        plan.TargetPosition = next.Position is { Length: 3 } position ? Vec3(position) : null;
        plan.ObservedTarget = plan.TargetHeldBySide = true;
        plan.StagingSince = plan.LastAssemblyGrowthAt = null; plan.AssemblyPeak = 0;
        plan.Demand = chosen;
        ArmFinaleWing(run, side, run.Bots, now); // preparation latch and deadline survive retargeting
        foreach (var escort in run.Bots.Where(b => b.Side == side && b.FinaleEscort && !b.IsPod))
            OrderFinaleWingBot(escort, run, plan, now);
        Console.WriteLine($"[strike] side={side} finale retarget from=station#{previous} target=station#{next.StationId} "
            + $"sector={next.ClusterId} reason=unreachable-volley reachable={chosen.Reachable} minimum={chosen.MinimumUseful}");
        return true;
    }
}
