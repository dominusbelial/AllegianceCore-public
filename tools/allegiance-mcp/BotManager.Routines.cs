using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly ScriptedBotChat _scriptedChat = new();

    // Called under the same pause gate as Loop, so a role change cannot race
    // a decision. Only existing world-owning peers can run these routines.
    internal int ChangeBehavior(string name, string? push)
    {
        lock (_pauseGate)
        {
            ActiveBot bot;
            lock (_gate) bot = _bots.Single(b => b.Name == name);
            if (bot.Cs is null) throw new InvalidOperationException("Behavior changes require a combat or routine peer");
            bot.Routines ??= new(bot.Behavior);
            if (push is null && bot.Routines.Count == 1) return 1;
            bot.Routines.Top.SavedOrder = bot.OrderedCommand;
            bot.Routines.Top.SavedTarget = bot.TargetId;
            bot.Routines.Top.SavedOrderedTarget = bot.OrderedTargetId;
            if (push is null) bot.Routines.Pop(); else bot.Routines.Push(push);
            bot.Behavior = bot.Routines.Top.Behavior;
            bot.TargetId = bot.Routines.Top.SavedTarget;
            bot.OrderedTargetId = bot.Routines.Top.SavedOrderedTarget;
            bot.OrderedCommand = bot.Routines.Top.SavedOrder;
            bot.DecisionFrame = null;
            bot.Cs.CommandControls(new(0, 0, 0, -1), 0);
            return bot.Routines.Count;
        }
    }

    private void DriveRoutine(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (!RefreshLife(bot, cs, now) || cs.World.MyShipId is not { } id) return;
        bot.ShipId = id;
        bot.Routines ??= new(bot.Behavior);
        var routine = bot.Routines.Top;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(id, out var me) || me.Latest is not { } pose) return;
            bool docked = me.StationId >= 0;
            if (bot.Behavior == "miner" && Catalog.IsLifepodHull(me.HullId)
                && UpdatePodState(bot, cs, now)) { DrivePodHome(bot, cs, now); return; }
            if (bot.Behavior == "miner") { DriveOrderedMiner(bot, routine, now, docked, pose); return; }
            if (LaunchNextMission(bot, cs, now)) return;
            if (UpdatePodState(bot, cs, now)) { DrivePodHome(bot, cs, now); return; }
            if (docked)
            {
                bot.OrderedCommand = null;
                routine.Docker.Rock = null;
                routine.LaunchAt = double.NegativeInfinity;
                if (now - bot.RoutinePurchaseAt >= 3)
                {
                    bot.RoutinePurchaseAt = now;
                    cs.SendBuyLoadout(bot.HullId, true, bot.Parts ?? []);
                }
                return;
            }
            if (double.IsNegativeInfinity(routine.LaunchAt))
            {
                routine.LaunchAt = now;
                routine.Docker.Flying(now);
                routine.Deathmatch.Flying(now, DeathmatchShips(bot, pose.Position));
                routine.Chat.Set(now, 30);
                if (bot.Behavior == "docker") cs.SendWingId(id, 8);
            }
            HandleOrders(bot, now);
            if (DriveCommanderOrder(bot, now)) return;
            switch (bot.Behavior)
            {
                case "docker": DriveDocker(bot, routine, now, pose); break;
                case "deathmatch": DriveDeathmatch(bot, routine, now, pose); break;
                case "chatter":
                    // Explicit opt-in; one message / 30s for this whole worker,
                    // even if an operator explicitly spawns 400 chatter peers.
                    if (routine.Chat.Pulse(now) && _scriptedChat.TrySend(now))
                        cs.SendChat(ScriptedBotChat.Text, ChatTarget.Everyone);
                    cs.CommandControls(new(0, 0, 0, -1), 0);
                    break;
            }
        }
    }

    private void DriveOrderedMiner(ActiveBot bot, BotRoutine routine, double now, bool docked, ServerShipUpdate pose)
    {
        var cs = bot.Cs!;
        var w = cs.World;
        // Native Miner.cs:141-238 receives the actual cluster-buoy dispatch.
        // Object bookmarks survive bounded-list rollover without replaying
        // the most recent dispatch on every 50ms decision pass.
        bool fresh = routine.LastBuoy is null || !w.Events.ChatBuoys.Any(b => ReferenceEquals(b, routine.LastBuoy));
        foreach (var buoy in w.Events.ChatBuoys)
        {
            if (ReferenceEquals(buoy, routine.LastBuoy)) { fresh = true; continue; }
            if (!fresh || buoy.Cd.OidRecipient != bot.ShipId) continue;
            DispatchMiner(bot, routine, buoy.Db.ClusterId, buoy.Cd.CommandID, docked, now);
        }
        routine.LastBuoy = w.Events.ChatBuoys.LastOrDefault();
        // The legacy CHATMESSAGE form can reference an already imported buoy
        // (Miner.cs:64-113). Promote the port's queued buoy slot once, just as
        // the shared order consumer promotes its ship/station/asteroid slots.
        if (w.MyQueuedCommand is { TargetType: IgcObjectType.Buoy } pending
            && w.CommandBuoys.TryGetValue(pending.TargetId, out var destination)
            && bot.AcceptedQueuedOrder != pending)
        {
            DispatchMiner(bot, routine, destination.ClusterId, pending.CommandId, docked, now);
            cs.SendOrderChange(bot.ShipId, pending.CommandId, OrderChange.CmdQueued, pending.TargetType, pending.TargetId);
            bot.AcceptedQueuedId = pending.CommandId;
            bot.AcceptedQueuedOrder = pending;
        }
        HandleOrders(bot, now);
        // Plain chat orders are already queued/accepted by the shared path.
        if (bot.OrderedCommand is { TargetType: IgcObjectType.Asteroid } order
            && w.Asteroids.TryGetValue(order.TargetId, out var orderedRock))
        {
            DispatchMiner(bot, routine, orderedRock.ClusterId, order.CommandId, docked, now);
            bot.OrderedCommand = order;
        }
        if (docked) { cs.CommandControls(new(0, 0, 0, -1), 0); return; }
        float capacity = Catalog.RepresentativeCore().Constants.FloatConstants[7]
            * w.GlobalAttributesFor(bot.Side).Get(GlobalAttributeSet.MiningCapacity);
        if (routine.Miner.ShouldReturn(pose.OreFraction * capacity, docked))
        {
            DataStationIGC? station = w.Stations.Values.Where(s => s.SideId == bot.Side)
                .OrderBy(s => (Vec3(s.Position) - pose.Position).LengthSquared()).Select(s => (DataStationIGC?)s).FirstOrDefault();
            if (station is not null)
            {
                routine.Miner.Return();
                bot.OrderedCommand = new(OrderChange.CidGoto, IgcObjectType.Station, station.Value.StationId);
            }
        }
        if (routine.Miner.Returning || bot.OrderedCommand is { TargetType: IgcObjectType.Station or IgcObjectType.Warp }) { DriveCommanderOrder(bot, now); return; }
        if (routine.Miner.Cluster is not { } goal)
        { cs.CommandControls(new(0, 0, 0, -1), 0); return; }
        short current = CsSector(cs);
        if (current != goal)
        {
            EnsureCombatPath(bot, current, goal, now);
            short next = CombatNextHop(bot, current);
            if (next != current) TransitWarp(cs, bot, current, next);
            return;
        }
        DataAsteroidIGC? rock = bot.OrderedCommand is { TargetType: IgcObjectType.Asteroid } mining
            ? (w.Asteroids.TryGetValue(mining.TargetId, out var assigned) ? assigned : null)
            : w.Asteroids.Values.Where(a => a.ClusterId == current && (a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0
                && (w.AsteroidLive.TryGetValue(a.AsteroidId, out var live) ? live.OreFraction > 0 : a.Ore > 0))
                .OrderBy(a => (Vec3(a.Position) - pose.Position).LengthSquared()).Select(a => (DataAsteroidIGC?)a).FirstOrDefault();
        if (rock is null) { cs.CommandControls(new(0, 0, 0, -1), 0); return; }
        // The existing peer mining path mines an adjacent stationary hull.
        // Approach outside the collision surface, inside its mining padding.
        var center = Vec3(rock.Value.Position);
        var away = pose.Position - center;
        float length = away.Length();
        if (length < 1) away = new(1, 0, 0); else away /= length;
        float radius = rock.Value.Radius + HullRadius(ActualHullId(bot)) + Sector.MiningProximityPadding * .5f;
        var point = center + away * radius;
        cs.CommandControls(AutopilotSteeringForMining(pose, point), 0);
    }

    private static void DispatchMiner(ActiveBot bot, BotRoutine routine, short cluster, short command, bool docked, double now)
    {
        if (!routine.Miner.Dispatch(cluster, command, docked)) return;
        bot.OrderedCommand = null; bot.Path.Clear();
        if (docked && now - bot.RoutinePurchaseAt >= 3)
        {
            bot.RoutinePurchaseAt = now;
            bot.Cs!.SendBuyLoadout(bot.HullId, true, bot.Parts ?? []);
        }
    }

    private static ControlData AutopilotSteeringForMining(ServerShipUpdate pose, Vector3 point)
    {
        float distance = (pose.Position - point).Length();
        var controls = Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(pose.Position, pose.Orientation, point);
        return controls with { Throttle = 2 * Math.Clamp((distance - 3) / 300, 0, 1) * ((controls.Throttle + 1) / 2) - 1 };
    }

    private void DriveDocker(ActiveBot bot, BotRoutine routine, double now, ServerShipUpdate pose)
    {
        var cs = bot.Cs!;
        // docker.pig:86-96: communal traffic control sends a real goto order
        // back to the sender; the ordinary acceptance/docking path flies it.
        foreach (var chat in cs.World.Events.ChatsAfter(routine.ChatCursor))
        {
            if (!chat.Message.StartsWith("sendcommand2", StringComparison.Ordinal) || chat.SidSender == bot.ShipId) continue;
            DataStationIGC? station = cs.World.Stations.Values.Where(s => s.ClusterId == CsSector(cs)).Select(s => (DataStationIGC?)s).FirstOrDefault();
            if (station is not null) cs.SendChat("", ChatTarget.Individual, chat.SidSender,
                commandId: OrderChange.CidGoto, oidTarget: station.Value.StationId, otTarget: (short)IgcObjectType.Station);
        }
        routine.ChatCursor = cs.World.Events.ChatSequence;
        if (routine.Docker.Rock is null)
        {
            var rocks = cs.World.Asteroids.Values.Where(a => a.ClusterId == CsSector(cs)).ToArray();
            if (rocks.Length > 0) routine.Docker.Rock = rocks[Random.Shared.Next(rocks.Length)].AsteroidId;
        }
        DataAsteroidIGC? rock = routine.Docker.Rock is { } rid && cs.World.Asteroids.TryGetValue(rid, out var asteroid) ? asteroid : null;
        var point = rock is null ? pose.Position : Vec3(rock.Value.Position);
        var delta = point - pose.Position;
        var hull = Catalog.RepresentativeCore().HullTypes[ActualHullId(bot)];
        var rates = pose.TurnRates;
        var face = PigFaceRoutine.Face(delta, pose.Orientation, rock?.Radius ?? 0,
            DockApproach.TurnState.FromHull(hull, cs.World.GlobalAttributesFor(bot.Side), rates[0], rates[1]),
            pose.Velocity.Length(), .05f);
        switch (routine.Docker.Tick(now, face.Complete, Random.Shared.Next))
        {
            case DockerAction.Face:
                cs.CommandControls(face.Controls, 0); break;
            case DockerAction.Fly: cs.CommandControls(new(0, 0, 0, .6f), 0); break; // 80% in [-1,1]
            case DockerAction.Dock:
                cs.CommandControls(new(0, 0, 0, -1), 0);
                cs.SendChat("sendcommand2", ChatTarget.Team, 8); break;
            default: cs.CommandControls(new(0, 0, 0, -1), 0); break;
        }
    }

    private List<DeathmatchCandidate> DeathmatchShips(ActiveBot bot, Vector3 position) => bot.Cs!.World.Ships.Values
        .Where(s => s.ShipId != bot.ShipId && s.SideId != bot.Side && !s.IsNotVisible && !s.IsDead
            && s.SectorId == CsSector(bot.Cs) && s.Latest is not null)
        .Select(s => new DeathmatchCandidate(s.ShipId, (s.Latest!.Position - position).LengthSquared(), Catalog.IsLifepodHull(s.HullId))).ToList();

    private void DriveDeathmatch(ActiveBot bot, BotRoutine routine, double now, ServerShipUpdate pose)
    {
        var cs = bot.Cs!;
        var dm = routine.Deathmatch;
        dm.Tick(now, DeathmatchShips(bot, pose.Position));
        bool rearm = dm.Rearm.Pulse(now) && dm.MustRearm(pose.HullFraction, pose.AmmoFraction, pose.FuelFraction);
        if (rearm || dm.Target is null)
        {
            bool home = rearm || pose.HullFraction < .75f;
            DataStationIGC? station = cs.World.Stations.Values.Where(s => s.Name == "Garrison" && (s.SideId == bot.Side) == home).Select(s => (DataStationIGC?)s).FirstOrDefault();
            if (station is not null)
            {
                bot.OrderedCommand = new(OrderChange.CidGoto, IgcObjectType.Station, station.Value.StationId);
                DriveCommanderOrder(bot, now);
                if (!home) bot.OrderedCommand = null; // camping continues to search every 3s
                return;
            }
        }
        bot.TargetId = dm.Target;
        if (dm.Target is { } target) EngageShipAt(bot, cs, target, now, 0, 0, boostOverride: !dm.Close);
        else cs.CommandControls(new(0, 0, 0, -1), 0);
    }

    private sealed partial class ActiveBot
    {
        public BotRoutineStack? Routines;
        public double RoutinePurchaseAt = double.NegativeInfinity;
    }
}
