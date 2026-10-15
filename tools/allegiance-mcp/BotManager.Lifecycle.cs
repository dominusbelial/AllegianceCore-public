using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly List<RemovedBotView> _removedBots = new();
    public sealed record RemovedBotView(string Name, short ShipId, string Reason);
    public IReadOnlyList<RemovedBotView> RemovedBots()
    {
        lock (_gate) return _removedBots.Concat(_bots
            .Where(b => !b.SpawnPending && !IsParticipating(b))
            .Select(b => new RemovedBotView(b.Name, b.ShipId, "connection lost"))).ToArray();
    }

    internal int StopBots(string? name, bool clearHistory)
    {
        ActiveBot[] bots;
        lock (_gate) bots = _bots.Where(b => name is null || b.Name == name).ToArray();
        foreach (var bot in bots) RemoveInactiveBot(bot, "stopped by operator");
        if (clearHistory) lock (_gate) _removedBots.Clear();
        return bots.Length;
    }

    private static bool IsParticipating(ActiveBot bot) => !bot.Inactive &&
        (bot.Cs is { } cs ? cs.Connected && !cs.Lost : bot.Bot?.Connected == true);

    private void RemoveInactiveBot(ActiveBot bot, string reason)
    {
        bot.Inactive = true;
        bot.TargetId = bot.OrderedTargetId = null;
        lock (_gate)
        {
            if (!_bots.Remove(bot)) return;
            _removedBots.Add(new(bot.Name, bot.ShipId, reason));
            if (_removedBots.Count > 500) _removedBots.RemoveAt(0);
        }
        // Disposal can join a receive thread; never do it under the roster gate.
        try { DisposePeer(bot.Cs); } catch { }
        try { DisposePeer(bot.Bot); } catch { }
    }

    private static void ResetNewLife(ActiveBot bot, double now, bool reinforcement)
    {
        if (reinforcement && bot.IsPod && bot.PodDocked)
        {
            bot.CompletedPodCycles++;
            if (bot.FinaleBomberActive && bot.FinaleGrantVerified && bot.Finale is { } finale)
                lock (finale.Sync) finale.StrikeStartedAt ??= now;
        }
        bot.IsPod = bot.PodDocked = false;
        bot.PodStationId = bot.PodDockStationId = bot.PodStationSector = -1;
        bot.PodStationCenter = Vector3.Zero;
        bot.RelaunchAttempts = 0;
        bot.RelaunchAt = double.NegativeInfinity;
        bot.RelaunchAckHandled = bot.RelaunchExhausted = false;
        bot.Arrived = false;
        bot.ArrivedAt = null;
        bot.StagingSector = -1;
        bot.StagePhase = reinforcement ? StageReleasedPhase : StageTransitPhase;
        bot.ReleasedAt = reinforcement ? now : null;
        bot.FirstStagingAt = null;
        bot.Path.Clear();
        bot.PathStart = bot.PathGoal = bot.LastCluster = -1;
        bot.WarpDest = null;
        bot.ExitClearFrom = null;
        bot.ExitClearRadius = 0;
        bot.TargetId = bot.OrderedTargetId = null;
        bot.AcceptedQueuedId = Allegiance.Core.Wire.OrderChange.CidNone;
        bot.CurrentQueued = null;
        bot.QcuConsumed = 0;
        bot.FriendlyGrids = null;
        bot.DecisionFrame = null;
        bot.RecentAttackers.Clear();
        bot.RecentAssetAttackers.Clear();
        EndDefense(bot);
        bot.LastAttackersPruneAt = bot.LastTargetAt = bot.LastFireAt = bot.LastMineAt = double.NegativeInfinity;
        bot.LastBoostAt = bot.LastCloakAt = double.NegativeInfinity;
        bot.AttackAfterburnerHeld = false;
        bot.HasPos = false;
        bot.LastMoveAt = now;
        bot.WorldNotReadyUntil = double.NegativeInfinity;
        bot.WorldReadyAt = -1;
        bot.WorldNotReadyAttempts = 0;
    }

    private static void RelaunchDockedPod(ActiveBot bot, ConnectionSession cs, double now,
        Action<short, bool, IReadOnlyList<ExpandedPartData>>? send = null, bool prepareFinale = true)
    {
        // The player's shop uses one BUY_LOADOUT(fLaunch:true), not an NPC undock.
        lock (cs.Gate)
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.StationId < 0) return;
        HandleRelaunchAck(bot, cs);
        if (now - bot.RelaunchAt < 5) return;
        if (bot.RelaunchAttempts >= 3)
        {
            if (!bot.RelaunchExhausted)
            {
                bot.RelaunchExhausted = true;
                bot.GrantWarning = $"relaunch: {bot.Name} remains docked after 3 BUY_LOADOUT launch requests";
                Console.WriteLine(bot.GrantWarning);
            }
            return;
        }
        if (bot.RelaunchAttempts == 0 && prepareFinale) PrepareFinaleRelaunch(bot, now);
        if (bot.FinaleBomberActive) bot.FinaleGrantVerified = false;
        cs.ResetBuyLoadoutAcks();
        bot.RelaunchAttempts++;
        bot.RelaunchAt = now;
        bot.RelaunchAckHandled = false;
        (send ?? ((hull, launch, parts) => cs.SendBuyLoadout(hull, launch, parts)))(bot.HullId, true, bot.Parts ?? []);
    }

    private static void HandleRelaunchAck(ActiveBot bot, ConnectionSession cs)
    {
        if (bot.RelaunchAttempts > 0 && !bot.RelaunchAckHandled && cs.BuyLoadoutAcks.LastOrDefault() is { } ack)
        {
            bot.RelaunchAckHandled = true;
            var grant = new LoadoutGrant(ack.HullId, ack.Parts.ToList());
            try
            {
                bot.GrantWarning = EvaluateGrant(bot.Name, bot.HullId, bot.Parts ?? [], grant);
                bot.Granted = grant;
                if (bot.FinaleBomberActive)
                {
                    bot.FinaleGrantVerified = ack.FBoughtEverything && ack.HullId == bot.HullId
                        && (bot.Parts ?? []).All(p => ack.Parts.Any(a => a.PartId == p.PartId
                            && a.MountId == p.MountId && a.Amount >= p.Amount));
                    if (!bot.FinaleGrantVerified)
                        bot.GrantWarning = $"finale: {bot.Name} strike purchase was not fully granted (check hull, mounts and ammunition). {bot.GrantWarning}";
                }
            }
            catch (InvalidOperationException ex) { bot.GrantWarning = ex.Message; Console.WriteLine($"[relaunch] {ex.Message}"); }
        }
    }

    private bool RefreshLife(ActiveBot bot, ConnectionSession cs, double now)
    {
        lock (cs.Gate)
        {
            var world = cs.World;
            if (world.MyShipId is not { } ship) return false;
            bool missionChanged = bot.LifeSession is not null &&
                (bot.LifeSession != cs || bot.LifeGeneration != world.MissionGeneration ||
                 (bot.LifeCookie is { } cookie && world.Mission is { } mission && cookie != mission.Cookie));
            if (bot.ShipId != ship || missionChanged)
            {
                bool reinforcement = bot.IsPod && !missionChanged;
                if (missionChanged)
                {
                    RestoreFinaleForNextMission(bot, now, world.GameOverResult);
                    bot.CompletedPodCycles = 0;
                    bot.MissionLaunchPending = true;
                }
                ResetNewLife(bot, now, reinforcement);
                if (!reinforcement)
                {
                    bot.DefenseChatCursor = 0;
                    bot.RoutePath = null; // sector ids from the previous map are invalid
                    bot.ObjectivesPending = true;
                }
                bot.ReadyShipId = -1;
                if (bot.LifeSession != cs) bot.KillCursor = 0;
            }
            bot.ShipId = ship;
            bot.Side = world.MySideId ?? bot.Side;
            bot.LifeSession = cs;
            bot.LifeGeneration = world.MissionGeneration;
            bot.LifeCookie = world.Mission?.Cookie;
            if (!bot.ObjectivesPending) return true;
            if (!world.WorldReady) return false;
            short? target = null;
            if (bot.TargetSector is not null && !bot.Arena && string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase))
                target = world.Stations.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side)
                    .Select(s => (short?)s.ClusterId).FirstOrDefault();
            if (bot.TargetSector is not null && target is null && bot.ObjectiveName is { } name)
                target = world.Clusters.Values.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (short?)c.ClusterId).FirstOrDefault();
            if (bot.TargetSector is not null && target is null) return false;
            bot.TargetSector = target;
            if (target is { } goal && bot.RouteIndex >= 0 && world.Ships.TryGetValue(ship, out var own))
            {
                var routes = world.Graph!.FindDistinctEntryRoutes(own.SectorId, goal, 3);
                if (routes.Count > 0)
                {
                    bot.RouteIndex %= routes.Count;
                    bot.RoutePath = routes[bot.RouteIndex];
                }
                else bot.RouteIndex = -1;
            }
            bot.ObjectivesPending = false;
            return true;
        }
    }

    // Native reseats players in a hangar (FedSrv/fsmission.cpp:791-796),
    // then BUY_LOADOUT(fLaunch) buys and launches (FedSrv/FedSrv.CPP:6189-6190,
    // 6247-6259, 6282-6303). Ready alone never launches or restores hardware.
    // Use the existing purchase/ACK/retry path, without counting a pod cycle or
    // preparing a finale bomber: this is the next match's original combat loadout.
    private static bool LaunchNextMission(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!bot.MissionLaunchPending) return false;
        lock (cs.Gate)
        {
            // The starting-money payday runs at Started; do not exhaust the
            // purchase retries against an empty wallet during briefing.
            if (!cs.World.WorldReady || cs.World.Mission?.Stage != MissionStageMessage.StageStarted
                || !cs.World.Ships.TryGetValue(bot.ShipId, out var me)) return true;
            HandleRelaunchAck(bot, cs);
            var ack = cs.BuyLoadoutAcks.LastOrDefault();
            bool isPod = Catalog.IsLifepodHull(me.HullId);
            // A partial parts grant can still launch the requested hull (the
            // same warning-only policy as initial spawn). Do not let that one
            // purchase hide every subsequent death and pod relaunch. The receive
            // thread may also have consumed an eject/dock after SET_CLUSTER and
            // before this pass; the launch ACK still completes the initial life.
            if (bot.RelaunchAttempts > 0 && ack is { FLaunch: true }
                && IsSuccessorOf(bot.HullId, ack.HullId)
                && (me.StationId < 0 || isPod) && (me.HullId == ack.HullId || isPod))
            {
                bot.MissionLaunchPending = false;
                bot.RelaunchAttempts = 0;
                bot.RelaunchAt = double.NegativeInfinity;
                bot.RelaunchAckHandled = bot.RelaunchExhausted = false;
                Console.WriteLine($"[p682] bot '{bot.Name}' ship {bot.ShipId} bought and launched next-match hull {ack.HullId}");
                return false;
            }
            if (me.StationId >= 0)
            {
                RelaunchDockedPod(bot, cs, now, prepareFinale: false);
                return true;
            }
            return true;
        }
    }

    internal static short? PickEnemyStation(IEnumerable<Allegiance.Core.DataStationIGC> stations,
        short side, short sector, Vector3 pos) => stations
        .Where(s => s.SideId >= 0 && s.SideId != side && s.ClusterId == sector && s.Position is { Length: 3 })
        .OrderBy(s => (new Vector3(s.Position[0], s.Position[1], s.Position[2]) - pos).LengthSquared())
        .ThenBy(s => s.StationId).Select(s => (short?)s.StationId).FirstOrDefault();

    private static bool HasVisibleAttacker(ActiveBot bot, ConnectionSession cs, short sector, double now)
    {
        lock (cs.Gate)
            return bot.RecentAttackers.Any(a => now - a.Value < ThreatWindowSeconds
                && cs.World.Ships.TryGetValue(a.Key, out var enemy) && !enemy.IsNotVisible
                && enemy.SideId != bot.Side && enemy.SectorId == sector && enemy.Latest is not null
                && (!IgnoreMinerTargets || CombatTargetSelector.ClassifyTargetId(enemy.HullId) != BotTargetClass.MinerDrone));
    }

    public long DecisionOverruns => _cadence.OverrunCount;

    internal static short? ResolveScenarioTarget(bool arena, Showcase.HullRole role, short? rendezvous, short enemyHome)
        => rendezvous is null or < 0 ? null : arena ? rendezvous : ResolveRoleTarget(role, rendezvous.Value, enemyHome);

    private static short ActualHullId(ActiveBot bot)
    {
        if (bot.Cs is { } cs)
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(cs.World.MyShipId ?? bot.ShipId, out var ship) && ship.HullId >= 0
                    && (ship.HasRealIdentity || ship.Loadout is not null))
                    return ship.HullId;
        return bot.Granted.Parts is not null ? bot.Granted.HullId : bot.HullId;
    }

    private static void ClearInvalidOrderedTarget(ActiveBot bot, ConnectionSession cs)
    {
        if (bot.OrderedTargetId is not { } previous) return;
        lock (cs.Gate)
            if (!cs.World.Ships.TryGetValue(previous, out var target) || target.IsDead || target.IsGone
                || Catalog.IsLifepodHull(target.HullId))
                bot.OrderedTargetId = bot.TargetId = null;
    }

}
