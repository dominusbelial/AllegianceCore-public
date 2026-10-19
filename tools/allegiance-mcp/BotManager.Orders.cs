using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    internal sealed record CommanderOrderReport(short Ship, short? OrderedTarget, short? Target, short? Command, short? TargetType, short? TargetId);
    internal CommanderOrderReport[] CommanderOrders()
    {
        if (_gate is null || _bots is null) return [];
        lock (_gate) return _bots.Where(b => b.OrderedCommand is not null).Select(b => new CommanderOrderReport(
            b.ShipId, b.OrderedTargetId, b.TargetId, b.OrderedCommand?.CommandId,
            (short?)b.OrderedCommand?.TargetType, b.OrderedCommand?.TargetId)).ToArray();
    }

    // Native defend acquires enemies within 1500 of the protectee and returns
    // when the chased enemy is farther than 3500 (shipIGC.cpp:2373-2404,2688-2695).
    internal static short? PickCommanderDefenseTarget(IEnumerable<(short Id, Vector3 Position)> enemies, Vector3 charge, short? current)
    {
        var visible = enemies.ToArray();
        foreach (var enemy in visible)
            if (enemy.Id == current && (enemy.Position - charge).LengthSquared() <= 3500 * 3500) return enemy.Id;
        return visible.Where(e => (e.Position - charge).LengthSquared() <= 1500 * 1500)
            .OrderBy(e => (e.Position - charge).LengthSquared()).Select(e => (short?)e.Id).FirstOrDefault();
    }

    // Native accepted orders own the destination before autonomous orders:
    // Igc/shipIGC.cpp:1936-1939; command defaults Igc/shipIGC.h:2071-2161.
    private bool DriveCommanderOrder(ActiveBot bot, double now)
    {
        if (bot.OrderedCommand is not { } command || bot.Cs is not { } cs) return false;
        if (bot.OrderedWarpOrigin is { } origin && CsSector(cs) != origin)
        {
            bot.OrderedCommand = null; bot.OrderedWarpOrigin = null; return false;
        }
        Vector3 point = default;
        short sector = -1;
        bool enemy = false;
        lock (cs.Gate)
        {
            var world = cs.World;
            switch (command.TargetType)
            {
                case IgcObjectType.Ship when world.Ships.TryGetValue(command.TargetId, out var ship):
                    if (ship.IsDead || Catalog.IsLifepodHull(ship.HullId))
                    { bot.OrderedCommand = null; bot.OrderedTargetId = null; return false; }
                    sector = ship.SectorId;
                    if (ship.Latest is { } pose) point = pose.Position;
                    enemy = ship.SideId != bot.Side && !world.AreAllied(bot.Side, ship.SideId);
                    break;
                case IgcObjectType.Station when world.Stations.TryGetValue(command.TargetId, out var station):
                    sector = station.ClusterId; point = Vec3(station.Position);
                    enemy = station.SideId != bot.Side && !world.AreAllied(bot.Side, station.SideId);
                    break;
                case IgcObjectType.Asteroid when world.Asteroids.TryGetValue(command.TargetId, out var rock):
                    sector = rock.ClusterId; point = Vec3(rock.Position);
                    break;
                case IgcObjectType.Warp when world.Warps.TryGetValue(command.TargetId, out var warp):
                    sector = warp.ClusterId; point = Vec3(warp.Position);
                    break;
            }
        }
        // A temporary visibility gap preserves the accepted order.
        if (sector < 0) { LoiterCombat(bot, now); return true; }
        short current = CsSector(cs);
        if (current != sector)
        {
            EnsureCombatPath(bot, current, sector, now);
            short next = CombatNextHop(bot, current);
            if (next == current || !TransitWarp(cs, bot, current, next)) LoiterCombat(bot, now);
            bot.LastCluster = current;
            return true;
        }
        if (command.CommandId == OrderChange.CidDefend && !enemy)
        {
            short? threat;
            lock (cs.Gate)
                threat = PickCommanderDefenseTarget(cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side
                    && !cs.World.AreAllied(bot.Side, s.SideId) && !s.IsNotVisible && s.Latest is not null && s.SectorId == current
                    && !Catalog.IsLifepodHull(s.HullId))
                    .Select(s => (s.ShipId, s.Latest!.Position)), point, bot.TargetId);
            bot.TargetId = threat;
            if (threat is { } id) EngageShipAt(bot, cs, id, now, 0, 0);
            else CombatFlyTo(bot, point, 250, 600, 0);
        }
        else if (enemy && command.TargetType == IgcObjectType.Ship)
        {
            // Retain the established OrderedTarget focus-fire path.
            bot.OrderedTargetId = command.TargetId;
            EngageCombat(bot, now, null);
        }
        else if (enemy && command.TargetType == IgcObjectType.Station && command.CommandId == OrderChange.CidAttack)
        {
            bot.TargetId = command.TargetId;
            EngageShipAt(bot, cs, command.TargetId, now, 0, 0, stationTarget: true);
        }
        else if (!enemy && command.TargetType == IgcObjectType.Station && command.CommandId == OrderChange.CidGoto)
        {
            bot.TargetId = null;
            if (TryDockApproachControl(cs, bot, command.TargetId, out var controls, out var movement)) cs.CommandControls(controls, movement);
            else if (TryCsPose(cs, bot.ShipId, out var position, out var orientation))
                cs.CommandControls(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(position, orientation, point), 0);
        }
        else if (command.TargetType == IgcObjectType.Warp)
        { bot.OrderedWarpOrigin = sector; FlyWarpLeg(bot, point); }
        else
        {
            // Defend/goto remains assigned to the named friendly/neutral object.
            bot.TargetId = null;
            CombatFlyTo(bot, point, 250, 600, 0);
        }
        bot.LastCluster = current;
        return true;
    }
}
