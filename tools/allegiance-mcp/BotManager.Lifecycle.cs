using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
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
        lock (_gate)
        {
            foreach (var key in _fleetSlots.Where(s => name is null || s.Value.Name == name).Select(s => s.Key).ToArray()) _fleetSlots.Remove(key);
            if (clearHistory) _removedBots.Clear();
        }
        return bots.Length;
    }

    private static bool IsParticipating(ActiveBot bot) => !bot.Inactive &&
        (bot.Cs is { } cs
            // P813c — a session in its reconnect window is not gone: the server is
            // holding the seat and the join burst re-syncs the world when the
            // re-dial lands. Reaping it here would make the grace pointless.
            ? cs.Reconnecting || (cs.Connected && !cs.Lost)
            : bot.Bot?.Connected == true);

    private void RemoveInactiveBot(ActiveBot bot, string reason)
    {
        bot.Inactive = true;
        bot.TargetId = bot.OrderedTargetId = null;
        bot.OrderedCommand = null;
        bot.AcceptedQueuedOrder = null;
        bot.OrderedWarpOrigin = null;
        lock (_gate)
        {
            if (!_bots.Remove(bot)) return;
            _removedBots.Add(new(bot.Name, bot.ShipId, reason));
            if (_removedBots.Count > 500) _removedBots.RemoveAt(0);
        }
        RefreshMedevacScouts();   // P779b-1 — hand the side's medevac job to the next scout
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
        bot.IsPod = bot.PodDocked = bot.PodRestarted = false;
        bot.FlagTask = FlagDuty.None;
        bot.FlagAnchor = null;
        bot.FlagTrace = "";
        bot.KillRecovering = false;
        bot.KillTrace = "";
        bot.ArtifactSearchRound = 0;
        bot.SupportRefitting = false;
        bot.SupportTraceTarget = null;
        bot.SupportCheckAt = double.NegativeInfinity;
        // P791 — fresh life, fresh repair-hysteresis state.
        bot.SupportHoldTarget = null;
        bot.SupportHoldLast = 1f;
        bot.SupportCooldownTarget = -1;
        bot.SupportCooldownUntil = double.NegativeInfinity;
        bot.SupportCooldownStrikes = 0;
        bot.FinaleRefitting = bot.FinaleRefitLaunchPending = false;
        bot.FinaleRefitCheckAt = double.NegativeInfinity;
        // P801 — a fresh life has not released into the current go time yet.
        bot.FinaleReleaseLogged = false;
        bot.FinaleReleaseAt = null;
        bot.FinaleStrikeSpent = false;
        bot.PodStationId = bot.PodDockStationId = bot.PodStationSector = -1;
        bot.PodProbeId = -1;
        bot.PodTargetAuto = false;
        bot.PodRepickAt = double.NegativeInfinity;
        bot.PodRouteSector = bot.PodRouteWarpId = -1;
        bot.PodRouteWarpCount = 0;
        bot.PodStationCenter = Vector3.Zero;
        // P814 — a fresh life resets gunner state (no lingering carrier/board).
        bot.GunnerCarrierId = null;
        bot.TurretMount = -1;
        bot.TurretSeeded = false;
        bot.TurretOrientation = new Orientation();
        bot.GunnerSeekSince = double.NegativeInfinity;
        bot.GunnerBoardRev = -1;
        bot.GunnerParkUntil = null;
        bot.CarrierHullBought = false;
        bot.CarrierGatherUntil = null;
        bot.GunnerKillsAtLaunch = 0;
        bot.LastGunnerKill = -1;
        bot.GunnerLastHull = 1f;
        bot.ServerClock.Reset();
        bot.PurchaseScheduledAt = double.NegativeInfinity;
        bot.LoadoutRefused = false;
        bot.PendingTechLoadout = null;
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
        bot.OrderedCommand = null;
        bot.AcceptedQueuedOrder = null;
        bot.OrderedWarpOrigin = null;
        bot.AcceptedQueuedId = Allegiance.Core.Wire.OrderChange.CidNone;
        bot.CurrentQueued = null;
        bot.QcuConsumed = 0;
        bot.FriendlyGrids = null;
        bot.DecisionFrame = null;
        bot.RecentAttackers.Clear();
        bot.RecentAssetAttackers.Clear();
        bot.RecentStationAttackers.Clear();
        // P778a — push telemetry is per-life state.
        bot.PushPrevAt = double.NegativeInfinity;
        bot.PushPrevClosing = bot.PushPrevBomberAlong = 0;
        bot.PushPrevOwnHull = bot.PushPrevBomberHull = 1f;
        bot.PushLastContactAt = null;
        bot.PushLastHoldAt = bot.PushLastSeekAt = double.NegativeInfinity;
        bot.PushHoldCount = 0;
        bot.PushLastHoldReason = null;
        bot.PushContacts = 0;
        EndDefense(bot);
        bot.LastAttackersPruneAt = bot.LastTargetAt = bot.LastFireAt = bot.LastMineAt = double.NegativeInfinity;
        bot.LastChaffAt = bot.LastProbeAt = bot.LastRescueProbeAt = double.NegativeInfinity;
        bot.MedevacPods.Clear();
        bot.LastBoostAt = double.NegativeInfinity;
        bot.CloakHeld = false;
        bot.AttackAfterburnerHeld = false;
        bot.ObjectiveFallbackLogged = false;
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
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.StationId < 0) return;
            // P799 — ghost guard (minimal, per the task brief): a bot whose player
            // is out of lives (deaths > iLives, shipIGC.h:2465-2469) is a ghost;
            // the server refuses every BUY_LOADOUT (FedSrv.CPP:6156), so stop
            // requesting launches instead of burning the 3-attempt retry budget
            // forever on a refusal that can never succeed.
            if (cs.World.TryGetOwnMissionStats(out _, out short ghostDeaths) && ghostDeaths > cs.World.MissionLives)
            {
                if (!bot.RelaunchExhausted)
                {
                    bot.RelaunchExhausted = true;
                    bot.GrantWarning = $"relaunch: {bot.Name} is a ghost ({ghostDeaths} deaths > lives {cs.World.MissionLives}) — out of lives, cannot launch";
                    Console.WriteLine(bot.GrantWarning);
                }
                return;
            }
        }
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
        if (bot.TechLoadouts)
        {
            if (double.IsNegativeInfinity(bot.PurchaseScheduledAt))
                bot.PurchaseScheduledAt = bot.PurchasePacer?.Reserve(now) ?? now;
            if (now < bot.PurchaseScheduledAt) return;
            if (bot.PurchasePacer is { } pacer && !pacer.TrySend(now)) return;
            if (!RepickRelaunchLoadout(bot, cs)) return;
        }
        if (bot.FinaleBomberActive) bot.FinaleGrantVerified = false;
        cs.ResetBuyLoadoutAcks();
        bot.RelaunchAttempts++;
        bot.RelaunchAt = now;
        bot.RelaunchAckHandled = false;
        bot.PurchaseScheduledAt = double.NegativeInfinity;
        (send ?? ((hull, launch, parts) => cs.SendBuyLoadout(hull, launch, parts)))(bot.HullId, true, bot.Parts ?? []);
    }

    private static void HandleRelaunchAck(ActiveBot bot, ConnectionSession cs)
    {
        if (bot.RelaunchAttempts > 0 && !bot.RelaunchAckHandled && cs.BuyLoadoutAcks.LastOrDefault() is { } ack)
        {
            bot.RelaunchAckHandled = true;
            var grant = new LoadoutGrant(ack.HullId, ack.Parts.ToList());
            // P796 — every real relaunch purchase ack: hull bought+launched and
            // every granted part equipped. The initial spawn purchase is recorded
            // in SeatCombatClient (RelaunchAttempts > 0 keeps this from double-counting).
            if (ack.FLaunch) WeaponCoverage.Global.RecordPurchase(bot.Side, ack.HullId, ack.Parts);
            bot.LoadoutRefused = !ack.FLaunch;
            if (bot.TechLoadouts && ack.FBoughtEverything && ack.FLaunch && bot.PendingTechLoadout is { } selected
                && ack.HullId == selected.HullId && selected.Parts.All(p => ack.Parts.Contains(p)))
                bot.PreviousLegalLoadout = selected;
            if (bot.LoadoutRefused) Console.WriteLine($"[loadout] {bot.Name}: purchase refused; revalidate previous loadout at next paced retry");
            try
            {
                bot.GrantWarning = EvaluateGrant(bot.Name, bot.HullId, bot.Parts ?? [], grant);
                bot.Granted = grant;
                if (ack.FLaunch) lock (cs.Gate) bot.FinaleMissileLaunchBase = cs.World.OwnMissilesFired;
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
                if (bot.LifeSession != cs) bot.KillCursor = bot.PodOutcomeCursor = 0;
            }
            bot.ShipId = ship;
            bot.Side = world.MySideId ?? bot.Side;
            bot.LifeSession = cs;
            bot.LifeGeneration = world.MissionGeneration;
            bot.LifeCookie = world.Mission?.Cookie;
            if (!bot.ObjectivesPending) return true;
            if (!world.WorldReady) return false;
            short? target = null;
            if (bot.TargetSector is not null && string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase))
            {
                target = world.Stations.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side)
                    .Select(s => (short?)s.ClusterId).FirstOrDefault();
                // P797 follow-up — enemy base not seen yet: the topology overview's
                // home-sector flags (CLUSTERINFO(200)) minus this side's own homes.
                if (target is null && world.Graph is { } graph)
                {
                    var ownHomes = world.Stations.Values.Where(s => s.SideId == bot.Side).Select(s => s.ClusterId).ToHashSet();
                    var enemyHomes = graph.Clusters.Where(c => c.HomeSector && !ownHomes.Contains(c.Id)).Select(c => c.Id).ToArray();
                    if (enemyHomes.Length == 1) target = enemyHomes[0];
                }
            }
            if (bot.TargetSector is not null && target is null && bot.ObjectiveName is { } name)
                target = world.Clusters.Values.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (short?)c.ClusterId).FirstOrDefault();
            // P831 — the fresh life must ALWAYS resolve here. The old code stayed
            // pending (`return false`) until the fogged world could name the
            // objective, which parked the bot: a pending bot is never driven, so
            // it never flies, so it never observes the station/home the
            // resolution waits for — a self-deadlock (rig batches 34/36: the 48
            // requested-Bomber bots on a P821 fallback hull sat in the hangar for
            // hours, and with every bot carrying a clash target from the previous
            // map the whole fleet could park). A stale sector id from the previous
            // mission must never outlive the reset either. Falling back to "no
            // fixed objective yet" is a RESOLVABLE state: the bot fights where it
            // is and AssignClashTargets (which re-derives a bomber's enemy home
            // from the side's own intel) hands it a fresh objective as soon as the
            // side has one. The requested duty is untouched (P821).
            if (bot.TargetSector is not null && target is null) LogObjectiveFallback(bot, world);
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

    /// <summary>P831 — one bounded line per fresh life when the new mission's
    /// objective is not derivable from this bot's own (fogged) world, naming what
    /// the world was missing so a recurrence is diagnosable from the log alone.
    /// The bot proceeds with no fixed objective (a resolvable state) instead of
    /// staying pending; the clash assignment in <c>BotManager.Exploration.cs</c>
    /// re-issues the duty once the side has resolved its target.</summary>
    private static void LogObjectiveFallback(ActiveBot bot, ClientWorld world)
    {
        if (bot.ObjectiveFallbackLogged) return;
        bot.ObjectiveFallbackLogged = true;
        int enemyStations = world.Stations.Values.Count(s => s.SideId >= 0 && s.SideId != bot.Side);
        int ownStations = world.Stations.Values.Count(s => s.SideId == bot.Side);
        var ownHomes = world.Stations.Values.Where(s => s.SideId == bot.Side).Select(s => s.ClusterId).ToHashSet();
        int enemyHomes = world.Graph?.Clusters.Count(c => c.HomeSector && !ownHomes.Contains(c.Id)) ?? 0;
        var core = Catalog.RepresentativeCore();
        bool known = core.HullTypes.TryGetValue(ActualHullId(bot), out var hullType);
        var hullRole = known ? Showcase.ClassifyHull(hullType) : (Showcase.HullRole?)null;
        bool bomberDuty = string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"[p831] {bot.Name} (side {bot.Side}, requested role={bot.Role}, "
            + $"flying {(known ? $"{hullType.Name} [{hullRole}]" : "unknown hull")}"
            + (bomberDuty && known && hullRole != Showcase.HullRole.Bomber ? ", P821 fallback hull" : "") + "): "
            + $"no new-mission objective derivable yet (stale sector {bot.TargetSector}, enemyStations={enemyStations}, "
            + $"ownStations={ownStations}, candidate enemy homes={enemyHomes}, "
            + $"objectiveName={(bot.ObjectiveName is { } name ? $"'{name}'" : "unset")}); "
            + "engaging in place until this side resolves a target");
    }

    // Native reseats players in a hangar (FedSrv/fsmission.cpp:791-796),
    // then BUY_LOADOUT(fLaunch) buys and launches (FedSrv/FedSrv.CPP:6189-6190,
    // 6247-6259, 6282-6303). Ready alone never launches or restores hardware.
    // Use the existing purchase/ACK/retry path, without counting a pod cycle or
    // preparing a finale bomber: select the role against the next match's real shop.
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

    /// <summary>P837 — the nearest DIFFERENT-side ship this bot can see in its own
    /// sector, or null. A small fleet fights what it meets while travelling: with
    /// a handful of ships there is no wing to keep assembled, and two nine-bot
    /// fleets otherwise cross the map on their way to the same clash sector without
    /// a single shot (live test P727: 0 shots in 210 s). The big-fleet staging
    /// discipline (P539d) is untouched — it only applies above
    /// <see cref="ScoutOpening.MeetingEngagementCombatBots"/> combat bots.</summary>
    private static short? VisibleEnemyInSector(ActiveBot bot, ConnectionSession cs, short sector)
    {
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } mine) return null;
            short? best = null;
            float bestD = float.MaxValue;
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.ShipId == bot.ShipId || ship.IsNotVisible) continue;
                if (ship.SideId < 0 || ship.SideId == bot.Side) continue;
                if (ship.SectorId != sector || ship.Latest is not { } lu) continue;
                if (IgnoreMinerTargets && CombatTargetSelector.ClassifyTargetId(ship.HullId) == BotTargetClass.MinerDrone) continue;
                float d = (lu.Position - mine.Position).Length();
                if (d < bestD) { bestD = d; best = ship.ShipId; }
            }
            return best;
        }
    }

    public long DecisionOverruns => _cadence.OverrunCount;

    internal static short? ResolveScenarioTarget(Showcase.HullRole role, short? rendezvous, short enemyHome)
        => rendezvous is null or < 0 ? null : ResolveRoleTarget(role, rendezvous.Value, enemyHome);

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
