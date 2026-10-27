using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private static bool ConquestGame(ConnectionSession cs) => cs.World.Mission?.Goals.IsConquestGame == true;
    private static int ConquestPercent(ConnectionSession cs, short side)
    {
        lock (cs.Gate) return cs.World.SideGameStates.GetValueOrDefault(side).Conquest;
    }

    // Policy inputs are this pilot's native exports/updates only. Station discovery:
    // FedSrv/fsmission.cpp:4215-4222; counters: :3685-3696, clintlib/appmsg.cpp:2392-2407.
    private ConquestContact[]? ConquestContacts(ActiveBot bot)
    {
        var cs = bot.Cs!;
        if (!ConquestGame(cs)) return null;
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes[ActualHullId(bot)];
        lock (cs.Gate)
            return cs.World.Stations.Values.Where(s => s.Position is { Length: 3 }
                && core.StationTypes.TryGetValue(s.StationTypeId, out var st)
                && st.HasCapability(DataStationTypeIGC.SabmFlag)
                && (s.SideId == bot.Side || !cs.World.AreAllied(bot.Side, s.SideId)))
                .Select(s =>
                {
                    var type = core.StationTypes[s.StationTypeId];
                    bool own = s.SideId == bot.Side;
                    // shipIGC.cpp:727-729,849-861: bay class + HabmBoard + shield
                    // threshold; no armor-fraction gate and no SabmCapture gate
                    // on player boarding. Never infer an unseen shield value.
                    bool board = hull.HasCapability(DataHullTypeIGC.HabmBoard)
                        && type.HasCapability(hull.HasCapability(DataHullTypeIGC.HabmFighter)
                            ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand);
                    bool strike = bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase) || (bot.Granted.Parts ?? bot.Parts ?? []).Any(p => p.MountId < 128
                        && core.PartTypes.TryGetValue(p.PartId, out var part) && part.Type is DataPartTypeIGC.Weapon gun
                        && core.ProjectileTypes.TryGetValue(gun.ProjectileTypeId, out var round) && round.Power > 0
                        && core.Constants.DamageConstants[round.DamageType & 0x1f][type.DefenseTypeShield] > 0
                        && core.Constants.DamageConstants[round.DamageType & 0x1f][type.DefenseTypeArmor] > 0);
                    bool threat = own && ((_defenseIncidents?.TryGetValue(DefenseKeyFor(bot, s.ClusterId), out var incident) == true
                            && Now() - incident.LastThreat < DefenseQuietSeconds)
                        || bot.RecentStationAttackers.Any(a => a.Key.Station == s.StationId
                            && Now() - a.Value < ThreatWindowSeconds)
                        || cs.World.Ships.Values.Any(e => e.SideId >= 0 && e.SideId != bot.Side
                            && !cs.World.AreAllied(bot.Side, e.SideId) && e.SectorId == s.ClusterId
                            && FlagVisible(cs.World, e) && !Catalog.IsLifepodHull(e.HullId)
                            && (e.Latest!.Position - Vec3(s.Position)).LengthSquared() < 2500 * 2500));
                    return new ConquestContact(s.StationId, s.ClusterId, own,
                        FlagDistance(bot, s.ClusterId, Vec3(s.Position)), board, strike, threat);
                }).Where(s => s.Distance < double.MaxValue).ToArray();
    }

    // One shared offense/denial budget, executed by both standalone and supervisor.
    // Preserve cargo first. Combined games reserve at least half the remaining
    // offense for flags/artifacts; spare hold slots stay with the ordinary fleet.
    private static void PlanConquestDuties(List<FlagCandidate> available, ref int offense, ref int denial,
        List<FlagDeployment> result)
    {
        void Assign(FlagCandidate c, FlagDuty duty, short? anchor = null, int post = -1)
        { result.Add(new(c.Cookie, c.Side, c.Name, duty, anchor, post)); available.Remove(c); }
        var carriers = available.Where(c => c.Carrying).ToArray();
        foreach (var carrier in carriers) Assign(carrier, FlagDuty.Return);
        offense = Math.Max(0, offense - carriers.Length);
        bool cargo = available.Any(c => c.CargoPilot && (c.Flags || c.Artifacts));
        int attackBudget = cargo ? offense / 2 : offense;
        int holdBudget = cargo ? denial / 2 : denial;
        var threatened = available.SelectMany(c => c.Stations ?? []).Where(s => s.Own && s.Threatened)
            .Select(s => s.Id).ToHashSet();
        // On a two-base map 50% is already one station from a 100% win.
        // Use only observed identities; under fog a smaller known denominator
        // recalls conservatively earlier, without inventing hidden stations.
        int knownStations = available.SelectMany(c => c.Stations ?? []).Select(s => s.Id).Distinct().Count();
        int nearWindow = Math.Max(20, knownStations == 0 ? 20 : (int)Math.Ceiling(100.0 / knownStations));
        if (available.Any(c => c.ConquestPercent >= Math.Max(0, c.ConquestGoal - nearWindow)))
        {
            // Recall the nearest FREE combat pilots, never a returning cargo ship.
            foreach (var c in available.Where(c => c.CargoPilot && (c.Stations ?? []).Any(s => threatened.Contains(s.Id)))
                         .OrderBy(c => c.Stations!.Any(s => s.Board) ? 1 : 0)
                         .ThenBy(c => c.Stations!.Where(s => threatened.Contains(s.Id)).Min(s => s.Distance))
                         .ThenBy(c => c.Name, StringComparer.Ordinal).Take(holdBudget).ToArray())
            {
                var station = c.Stations!.Where(s => threatened.Contains(s.Id)).OrderBy(s => s.Distance).ThenBy(s => s.Id).First();
                Assign(c, FlagDuty.Hold, station.Id); denial--;
            }
        }
        if (attackBudget == 0) return;
        var target = available.SelectMany(c => (c.Stations ?? []).Where(s => !s.Own))
            .GroupBy(s => s.Id).OrderByDescending(g => g.Any(s => s.Board))
            .ThenBy(g => g.Min(s => s.Distance)).ThenBy(g => g.Key).FirstOrDefault();
        if (target is null)
        {
            // No hidden station coordinates: send one scout through the native
            // discovery/sweep path, then replan from its station export.
            var scout = available.OrderByDescending(c => c.Scout).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (scout is not null) { Assign(scout, FlagDuty.Recon); offense--; }
            return;
        }
        short id = target.Key;
        var boarder = available.Where(c => c.Stations?.Any(s => s.Id == id && s.Board) == true)
            .OrderBy(c => c.Stations!.First(s => s.Id == id).Distance).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
        // A boarder cannot strip shields by merely touching the station. Keep a
        // strike slot when shields are up; the attack driver stops at downed shield.
        if (boarder is not null && attackBudget >= 2)
        { Assign(boarder, FlagDuty.Board, id); attackBudget--; offense--; }
        var strikes = available.Where(c => c.Stations?.Any(s => s.Id == id && s.Strike) == true)
            .OrderBy(c => c.CargoPilot ? 1 : 0).ThenBy(c => c.Stations!.First(s => s.Id == id).Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal).Take(Math.Max(1, attackBudget - 1)).ToArray();
        // Small/custom fleets may have no bomber role or armor-capable gun.
        // Reserve one ordinary pilot for the SAME legal strike refit rather
        // than allocating an escort-only wing that can never take a station.
        if (strikes.Length == 0 && attackBudget > 0)
            strikes = available.Where(c => c.Stations?.Any(s => s.Id == id) == true)
                .OrderBy(c => c.Scout ? 1 : 0).ThenBy(c => c.Name, StringComparer.Ordinal).Take(1).ToArray();
        foreach (var c in strikes.Take(attackBudget))
        { Assign(c, FlagDuty.Siege, id, boarder is not null && !available.Contains(boarder) ? boarder.ShipId : -1); attackBudget--; offense--; }
        if (strikes.Length == 0 && boarder is not null && available.Contains(boarder))
        { Assign(boarder, FlagDuty.Board, id); attackBudget--; offense--; }
        foreach (var c in available.Where(c => c.CargoPilot && c.Stations?.Any(s => s.Id == id) == true)
                     .OrderBy(c => c.Name, StringComparer.Ordinal).Take(attackBudget).ToArray())
        { Assign(c, FlagDuty.Wing, id, strikes.FirstOrDefault()?.ShipId ?? -1); offense--; }
    }

    private sealed partial class ActiveBot { public bool ConquestStrike; }

    private void UpdateConquestStrike(ActiveBot bot, double now)
    {
        if (bot.Cs is not { } cs || !ConquestGame(cs)) return;
        if (bot.FlagTask != FlagDuty.Siege)
        {
            if (!bot.ConquestStrike) return;
            RestoreFinaleLoadout(bot, currentHardware: true);
            bot.ConquestStrike = false;
            bot.Finale = null;
            bot.FinaleBomberReserved = bot.FinaleBomberActive = bot.FinaleGrantVerified = false;
            bot.FinaleRefitting = bot.FinaleRefitLaunchPending = false;
            bot.FinaleLoneWolf = bot.FinaleReleaseLogged = bot.FinaleStrikeSpent = false;
            bot.FinaleReleaseAt = null;
            return;
        }
        DataStationIGC station;
        lock (cs.Gate)
            if (bot.FlagAnchor is not { } id || !cs.World.Stations.TryGetValue(id, out station)) return;
        if (!bot.ConquestStrike || bot.Finale?.Plans.GetValueOrDefault(bot.Side)?.TargetId != station.StationId)
        {
            // An ordinary strike context reuses legal dock/refit/resupply and
            // the bomber flight law. It is NOT registered as a timed finale and
            // never sends the port's finale deadline request to the server.
            bot.Finale = new(new(AfterMinutes: 1), now, [bot], new()
            { [bot.Side] = new(station.StationId, station.SideId, station.ClusterId, station.StationTypeId, null) })
            { TriggeredAt = now, StrikeStartedAt = now, Sides = [bot.Side] };
            bot.ConquestStrike = true;
            bot.FinaleBomberReserved = true;
        }
    }

    private bool TryDriveConquest(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!ConquestGame(cs) || bot.FlagTask is not (FlagDuty.Siege or FlagDuty.Board or FlagDuty.Hold or FlagDuty.Wing or FlagDuty.Recon)) return false;
        if (HumanCommands(bot.Side) && bot.OrderedCommand is not null) return false;
        bot.TargetId = null;
        short cur = CsSector(cs);
        if (cur < 0) return false;
        if (bot.FlagTask == FlagDuty.Recon) return ConquestRecon(bot, cs, now);
        DataStationIGC station = default;
        bool valid;
        lock (cs.Gate)
        {
            valid = bot.FlagAnchor is { } id && cs.World.Stations.TryGetValue(id, out station)
                && (station.SideId == bot.Side) == (bot.FlagTask == FlagDuty.Hold)
                && !cs.World.AreAllied(bot.Side, station.SideId);
            // Boarding ends docked. Let the ordinary purchase/launch path launch
            // a fresh sortie, even when another station is still being contested.
            if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.StationId >= 0)
            { bot.MissionLaunchPending = true; return true; }
        }
        if (!valid) return ConquestRecon(bot, cs, now);
        TraceFlag(bot, bot.FlagTask, station.StationId, now);
        if (FlagTravel(bot, cs, station.ClusterId, now)) return true;
        if (bot.FlagTask == FlagDuty.Hold)
        {
            short? enemy;
            lock (cs.Gate) enemy = cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side
                    && !cs.World.AreAllied(bot.Side, s.SideId) && s.SectorId == cur && FlagVisible(cs.World, s)
                    && !Catalog.IsLifepodHull(s.HullId) && (s.Latest!.Position - Vec3(station.Position)).LengthSquared() < 2500 * 2500)
                .OrderBy(s => (s.Latest!.Position - Vec3(station.Position)).LengthSquared()).ThenBy(s => s.ShipId)
                .Select(s => (short?)s.ShipId).FirstOrDefault();
            if (enemy is { } threat) { bot.TargetId = threat; EngageShipAt(bot, cs, threat, now, 0, 0); }
            else CombatFlyTo(bot, Vec3(station.Position) + new Vector3(400, 0, 0), 100, 400, 0);
            return true;
        }
        if (bot.FlagTask == FlagDuty.Wing)
        {
            // Same bounded bomber screen as the P778 offense: prioritize threats
            // to the anchor; a dead anchor is replaced on the next planner pass.
            Vector3 anchor = Vec3(station.Position);
            var enemies = new List<ShowcaseFinale.EscortEnemy>();
            lock (cs.Gate)
            {
                var leader = cs.World.Ships.GetValueOrDefault((short)bot.FlagPost);
                if (leader is not null && (leader.SideId != bot.Side || leader.SectorId != cur || !FlagVisible(cs.World, leader))) leader = null;
                if (leader?.Latest is { } pose) anchor = pose.Position;
                foreach (var e in cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side
                             && !cs.World.AreAllied(bot.Side, s.SideId) && s.SectorId == cur && FlagVisible(cs.World, s)
                             && !Catalog.IsLifepodHull(s.HullId)))
                    enemies.Add(new(e.ShipId, e.Latest!.Position, e.Latest.Velocity, false));
            }
            if (TryCsPose(cs, bot.ShipId, out var pos, out _))
            {
                var enemy = ShowcaseFinale.EscortTarget(pos, anchor, enemies, 1800, 2500);
                if (enemy is { } target) { bot.TargetId = target; EngageShipAt(bot, cs, target, now, 0, 0); }
                else CombatFlyTo(bot, anchor, 150, 400, 0);
            }
            return true;
        }
        bool down;
        lock (cs.Gate) down = cs.World.StationLive.TryGetValue(station.StationId, out var live)
            && live.ShieldFraction < Catalog.RepresentativeCore().Constants.FloatConstants[5];
        if (bot.FlagTask == FlagDuty.Board)
        {
            if (down)
            {
                if (TryDockApproachControl(cs, bot, station.StationId, out var ctrl, out int movement)) cs.CommandControls(ctrl, movement);
                else CombatFlyTo(bot, Vec3(station.Position), 0, 40, 0);
            }
            else CombatFlyTo(bot, Vec3(station.Position) + new Vector3(700, 0, 0), 150, 400, 0);
            return true;
        }
        bool boarding;
        lock (cs.Gate) boarding = bot.FlagPost >= 0 && cs.World.Ships.TryGetValue((short)bot.FlagPost, out var boarder)
            && boarder.SideId == bot.Side && !boarder.IsDead && boarder.StatusSectorKnown
            && boarder.StationId < 0 && !Catalog.IsLifepodHull(boarder.HullId);
        if (down && boarding) CombatFlyTo(bot, Vec3(station.Position) + new Vector3(1000, 0, 0), 150, 400, 0);
        else
        {
            // The existing bomber fire/lead/range/missile controller is the only
            // attack path. No direct damage or scenario/debug command.
            bot.TargetId = station.StationId;
            EngageShipAt(bot, cs, station.StationId, now, 0, 0, stationTarget: true);
        }
        return true;
    }

    private bool ConquestRecon(ActiveBot bot, ConnectionSession cs, double now)
    {
        short sector;
        var graph = GraphOf(cs);
        lock (_intel.Sync)
        {
            var intel = _intel.Get(CookieOf(cs), bot.Side);
            short own = ResolveOwnHome(intel, graph, bot.Side);
            sector = ResolveEnemyHome(intel, graph, own);
        }
        TraceFlag(bot, FlagDuty.Recon, sector, now);
        if (sector >= 0 && FlagTravel(bot, cs, sector, now)) return true;
        SweepSector(bot, cs, CsSector(cs), now, null);
        return true;
    }
}
