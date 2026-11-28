using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly record struct StrikeApproach(StrikeWeaponEnvelope.Weapon Weapon, Vector3 Station,
        Vector3 Position, Vector3 Velocity, Orientation Orientation, float Distance, bool Magazine, bool Ready);

    // Caller owns the pilot's world gate. Each observation uses the flying hull,
    // granted inventory and the owner's current pose, pools and typed target.
    private static StrikeApproach? ReadStrikeApproach(ActiveBot bot, ClientWorld world, FinalePlan plan, double now)
    {
        if (world.MyShipId != bot.ShipId || !world.Ships.TryGetValue(bot.ShipId, out var own)
            || own.Latest is not { } pose || own.IsDead || own.StationId >= 0 || own.SectorId != plan.Sector
            || own.HullId != bot.Granted.HullId || bot.Granted.Parts is not { } parts
            || !world.Stations.TryGetValue(plan.TargetId, out var station) || station.ClusterId != own.SectorId
            || station.SideId == bot.Side || station.Position is not { Length: 3 } p) return null;
        var core = Catalog.RepresentativeCore();
        if (!core.HullTypes.TryGetValue(own.HullId, out var hull)
            || StrikeVolley.Profile(core, station.StationTypeId) is not { } profile) return null;
        var ga = world.GlobalAttributesFor(bot.Side);
        long used = Math.Max(0, world.OwnMissilesFired - bot.FinaleMissileLaunchBase);
        var weapon = StrikeWeaponEnvelope.Select(core, hull, parts, profile, ga, used,
            pose.AmmoFraction * hull.MaxAmmo, pose.EnergyFraction * hull.MaxEnergy * ga.Get(GlobalAttributeSet.MaxEnergy));
        if (weapon is not { } w) return null;
        bool magazine = w.Kind != StrikeWeaponEnvelope.Kind.Gun || parts.Any(part => part.MountId == 0
            && core.Launchers.TryGetValue(part.PartId, out var l) && core.MissileTypes.ContainsKey(l.ExpendabletypeId));
        var centre = new Vector3(p[0], p[1], p[2]);
        var delta = centre - pose.Position;
        float distance = delta.Length();
        bool current = bot.EnvelopeStation == plan.TargetId && world.MyCurrentCommand is { } cmd
            && cmd.TargetId == plan.TargetId && cmd.TargetType == IgcObjectType.Station;
        var feedback = world.MissileLockUpdates > bot.TargetFeedbackBase ? world.MissileLock : MissileLockState.None;
        bool gunConfirmed = magazine
            ? feedback.TargetType == IgcObjectType.Station && feedback.TargetId == plan.TargetId
            : !bot.WaitingForClear && world.TargetPoolUpdates > bot.ClearTargetBase && world.HasTargetPools;
        bool ready = StrikeWeaponEnvelope.Ready(w, plan.TargetId, distance,
            distance > 0 ? pose.Orientation.GetForward() * delta / distance : 0,
            current, feedback, gunConfirmed, w.Kind == StrikeWeaponEnvelope.Kind.Gun
                ? (bot.FinaleReleaseLogged || (world.Diag.ReceivedOwn == bot.GunQuietCount && now - bot.GunQuietSince >= w.Cycle))
                : now - bot.LastFireAt >= w.Cycle);
        return new(w, centre, pose.Position, pose.Velocity, pose.Orientation, distance, magazine, ready);
    }

    // Count only the weapon this pilot has acquired, and only rounds its
    // current magazine or owner pools can supply inside the planner's window.
    private static StrikeVolley.Loadout? ReadyLoadout(ActiveBot bot, ClientWorld world, StrikeApproach approach)
    {
        var core = Catalog.RepresentativeCore();
        var w = approach.Weapon;
        var part = bot.Granted.Parts!.First(p => p.PartId == w.Part && p.MountId == w.Mount);
        if (w.Kind != StrikeWeaponEnvelope.Kind.Gun)
        {
            var used = Math.Max(0, world.OwnMissilesFired - bot.FinaleMissileLaunchBase);
            part = part with { Amount = (short)Math.Max(0, part.Amount - used) };
        }
        if (StrikeVolley.FromParts(core, bot.Granted.HullId, [part]) is not { } loadout) return null;
        if (w.Kind == StrikeWeaponEnvelope.Kind.Gun)
        {
            var gun = (DataPartTypeIGC.Weapon)core.PartTypes[w.Part].Type;
            var hull = core.HullTypes[bot.Granted.HullId];
            var pose = world.Ships[bot.ShipId].Latest!;
            var mount = loadout.Mounts[0];
            int shots = mount.Shots;
            if (gun.CAmmoPerShot > 0) shots = Math.Min(shots, (int)(pose.AmmoFraction * hull.MaxAmmo / gun.CAmmoPerShot));
            if (gun.EnergyPerShot > 0) shots = Math.Min(shots, (int)(pose.EnergyFraction * hull.MaxEnergy
                * world.GlobalAttributesFor(bot.Side).Get(GlobalAttributeSet.MaxEnergy) / gun.EnergyPerShot));
            loadout.Mounts[0] = mount with { Shots = shots };
        }
        return loadout;
    }

    private static void AimStrikeStation(ActiveBot bot, ConnectionSession cs, FinalePlan plan, StrikeApproach approach, double now)
    {
        var world = cs.World;
        bool current = world.MyCurrentCommand is { TargetType: IgcObjectType.Station } cmd && cmd.TargetId == plan.TargetId;
        if (bot.EnvelopeStation != plan.TargetId)
        {
            bot.EnvelopeStation = plan.TargetId;
            bot.EnvelopeBearing = approach.Position - approach.Station;
            bot.TargetFeedbackBase = world.MissileLockUpdates;
            bot.ClearTargetBase = world.TargetPoolUpdates;
            bot.WaitingForClear = !approach.Magazine;
            bot.EnvelopeRetryAt = now;
        }
        bool cleared = bot.WaitingForClear && world.TargetPoolUpdates > bot.ClearTargetBase && world.TargetPoolsEmpty;
        if (cleared)
        {
            bot.WaitingForClear = false;
            bot.ClearTargetBase = world.TargetPoolUpdates;
            bot.EnvelopeRetryAt = now;
        }
        bool confirmed = approach.Magazine
            ? world.MissileLockUpdates > bot.TargetFeedbackBase && world.MissileLock.TargetType == IgcObjectType.Station
                && world.MissileLock.TargetId == plan.TargetId
            : world.TargetPoolUpdates > bot.ClearTargetBase && world.HasTargetPools;
        if ((!bot.WaitingForClear && current && confirmed) || now < bot.EnvelopeRetryAt) return;
        // After another order displaced a gun target, its untyped pools need
        // a fresh clear/acquire edge. A retry never moves the ingress bearing.
        if (!current && !approach.Magazine && !cleared) bot.WaitingForClear = true;
        if (bot.WaitingForClear)
        {
            bot.ClearTargetBase = world.TargetPoolUpdates;
            cs.SendOrderChange(bot.ShipId, OrderChange.CidNone, OrderChange.CmdCurrent, (IgcObjectType)(-1), -1);
        }
        else
        {
            bot.TargetFeedbackBase = world.MissileLockUpdates;
            cs.SendOrderChange(bot.ShipId, OrderChange.CidAttack, OrderChange.CmdCurrent, IgcObjectType.Station, plan.TargetId);
        }
        bot.EnvelopeRetryAt = now + StrikeWeaponEnvelope.OrderRetry;
    }

    // The same controls fly the granted lane and take the first shot. A
    // missile-only fit does not depend on EngageShipAt finding a lead gun.
    private bool FlyStrikeEnvelope(ActiveBot bot, ConnectionSession cs, FinaleRun run, FinalePlan plan, double now, bool permission)
    {
        // The same lock order as the coordinator and cloak policy. Read the
        // pose, target and owner echo in the same world epoch as the command.
        lock (run.Sync)
        lock (cs.Gate)
        {
            var world = cs.World;
            if (permission && !bot.FinaleStrikeSpent && bot.AcceptedAtRelease is { } start)
            {
                long accepted = bot.ReleasedGun ? world.Diag.ReceivedOwn : world.OwnMissilesFired;
                if (StrikeWeaponEnvelope.Spent(start, accepted))
                {
                    bot.FinaleStrikeSpent = true;
                    if (!plan.WaveEndLogged) Interlocked.Add(ref plan.WaveAcceptedRounds, accepted - start);
                    bot.AcceptedAtRelease = accepted;
                }
            }
            if (bot.FinaleStrikeSpent || ReadStrikeApproach(bot, world, plan, now) is not { Distance: > 1 } a)
            {
                bot.EnvelopeStation = -1;
                return false;
            }
            int cloak = permission ? 0 : CloakApproach(bot, cs, 0);
            if (a.Weapon.Kind == StrikeWeaponEnvelope.Kind.Gun &&
                (bot.EnvelopeStation != plan.TargetId || bot.GunQuietCount != world.Diag.ReceivedOwn))
            {
                bot.GunQuietSince = now;
                bot.GunQuietCount = world.Diag.ReceivedOwn;
            }
            bot.TargetId = null;
            bot.OrderedTargetId = null;
            AimStrikeStation(bot, cs, plan, a, now);
            var stationDirection = a.Station - a.Position;
            var aim = stationDirection.Normalize();
            var (yaw, pitch, _) = SteerTowardDirection(a.Orientation, aim);
            var goal = a.Station + StrikeWeaponEnvelope.Lane(bot.EnvelopeBearing, bot.FinaleLoneWolf, a.Weapon.Range);
            bool dodge = TryDodgeGoal(cs, bot, a.Position, out var escape);
            int state = dodge ? SiegeNavigation.DirectionButtons(escape - a.Position, a.Orientation)
                : StrikeWeaponEnvelope.Movement(a.Position, a.Velocity, a.Orientation, goal);
            // Inside the damping tolerance the radial controller asks for no
            // thrust. Strafe across the nose while exposed; the fixed lane and
            // velocity correction bring the pilot back as soon as it drifts.
            if (state == 0 && cloak == 0)
                state = (bot.ShipId & 1) == 0 ? ShipControlState.LeftButton : ShipControlState.RightButton;
            bool fire = permission && a.Ready && !dodge;
            if (fire && a.Weapon.Kind == StrikeWeaponEnvelope.Kind.Gun)
                fire = !FriendlyInCone(a.Position, aim, a.Distance, SameSideShipsInLine(cs, bot, a.Position, a.Distance), bot.Side);
            if (!permission) state |= cloak;
            else bot.CloakHeld = false;
            if (fire && a.Weapon.Kind == StrikeWeaponEnvelope.Kind.Gun)
                state |= ShipControlState.OneWeapon | a.Weapon.Mount;
            cs.CommandControls(new ControlData(yaw, pitch, 0, -1), state);
            if (fire && a.Weapon.Kind != StrikeWeaponEnvelope.Kind.Gun)
            {
                cs.SendFireMissile();
                bot.LastFireAt = now;
                WeaponCoverage.Global.RecordMissileLaunch(bot.ShipId, a.Weapon.Part);
            }
            return true;
        }
    }
}
