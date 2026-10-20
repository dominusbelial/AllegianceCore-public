using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // SC7 — goto hold-law constants. An aleph must be approached and HELD
    // short of its transit sphere (warp.Radius + ship.Radius, ~110 u), never
    // flown through (FlyWarpLeg) — the group bunches on our side of the border.
    private const float GotoWarpHoldArrival = 750f;
    private const float GotoWarpHoldBrake = 1100f;
    private const float GotoObjectHoldArrival = 550f;
    private const float GotoObjectHoldBrake = 900f;
    private const float FollowFriendlyArrival = 220f;
    private const float FollowFriendlyBrake = 360f;

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
        RepairTarget? repair = null;
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
                    if (!enemy && ship.Latest is { HullFraction: > 0 and < 1 } shipPose && ship.StationId < 0 && ship.ParentId < 0)
                        repair = new(ship.ShipId, false, sector, point, shipPose.Velocity, HullRadius(ship.HullId));
                    break;
                case IgcObjectType.Station when world.Stations.TryGetValue(command.TargetId, out var station):
                    sector = station.ClusterId; point = Vec3(station.Position);
                    enemy = station.SideId != bot.Side && !world.AreAllied(bot.Side, station.SideId);
                    if (!enemy && world.StationLive.TryGetValue(station.StationId, out var live) && live.HullFraction is > 0 and < 1)
                        repair = new(station.StationId, true, sector, point, Vector3.Zero,
                            Catalog.RepresentativeCore().StationTypes.TryGetValue(station.StationTypeId, out var type) ? type.Radius : 100);
                    break;
                case IgcObjectType.Asteroid when world.Asteroids.TryGetValue(command.TargetId, out var rock):
                    sector = rock.ClusterId; point = Vec3(rock.Position);
                    break;
                case IgcObjectType.Warp when world.Warps.TryGetValue(command.TargetId, out var warp):
                    sector = warp.ClusterId; point = Vec3(warp.Position);
                    break;
            }
        }
        if (command.CommandId == OrderChange.CidRepair)
        {
            // Never steer a positive-power loadout into a friendly attack.
            if (!HasNanite(bot)) return false;
            if (sector >= 0 && repair is null)
            {
                bot.OrderedCommand = null; bot.OrderedTargetId = null; bot.TargetId = null;
                return false; // completed, captured, docked or otherwise invalid
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
        if (command.CommandId == OrderChange.CidRepair && repair is { } repairTarget)
        {
            bot.OrderedTargetId = null;
            bot.TargetId = repairTarget.Id;
            FlyRepairTarget(bot, cs, repairTarget);
        }
        else if (command.CommandId == OrderChange.CidDefend && !enemy)
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
        else if (command.CommandId == OrderChange.CidGoto)
        {
            // SC7 — goto: fly to the object and HOLD without attacking.
            bot.TargetId = null;
            switch (command.TargetType)
            {
                case IgcObjectType.Warp:
                    // Aleph rally: fly to the mouth and hold JUST short of the
                    // transit sphere (threshold = warp.Radius + ship.Radius, ~110 u)
                    // so the group bunches on OUR side of the aleph, not crossing.
                    CombatFlyTo(bot, point, GotoWarpHoldArrival, GotoWarpHoldBrake, 0);
                    break;
                case IgcObjectType.Ship:
                    // A goto naming an ENEMY ship is the port's "engage that ship"
                    // action (P431 keys on TargetType == Ship for any command id), so
                    // preserve the established focus-fire path.
                    if (enemy)
                    {
                        bot.OrderedTargetId = command.TargetId;
                        EngageCombat(bot, now, null);
                    }
                    else
                    {
                        // SC7 — goto to a FRIENDLY ship = escort/follow: track its
                        // LIVE position each tick (a one-shot waypoint strands the
                        // escort as the leader flies on).
                        FollowFriendlyShip(bot, cs, command.TargetId);
                    }
                    break;
                case IgcObjectType.Station:
                    // Friendly station goto: keep the existing dock/approach.
                    if (TryDockApproachControl(cs, bot, command.TargetId, out var controls, out var movement)) cs.CommandControls(controls, movement);
                    else if (TryCsPose(cs, bot.ShipId, out var position, out var orientation))
                        cs.CommandControls(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(position, orientation, point), 0);
                    break;
                default:
                    // Asteroid / other named friendly-neutral object: fly and hold.
                    CombatFlyTo(bot, point, GotoObjectHoldArrival, GotoObjectHoldBrake, 0);
                    break;
            }
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
    /// <summary>SC7 — escort/follow a friendly ship: each tick re-read the
    /// friend's LIVE pose and fly to it at a soft standoff, never attacking.
    /// A one-shot waypoint (the pre-SC7 fallback) strands the escort the moment
    /// the leader moves on.</summary>
    private static void FollowFriendlyShip(ActiveBot bot, ConnectionSession cs, short targetId)
    {
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(targetId, out var s) || s.Latest is not { } pose) return;
            CombatFlyTo(bot, pose.Position, FollowFriendlyArrival, FollowFriendlyBrake, 0);
        }
    }
}
