using System.Globalization;
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
    internal sealed record SupportSettings(double Share)
    {
        internal static SupportSettings Read(Func<string, string?> read) => new(
            double.TryParse(read("ALLEGIANCE_BOT_SUPPORT_SHARE"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : .10);
    }

    internal static bool SupportSlot(int ordinal, int count, double share) => count > 0
        && ordinal >= 0 && ordinal < count && share > 0
        && (int)Math.Ceiling((ordinal + 1) * share) > (int)Math.Ceiling(ordinal * share);
    private static bool IsSupport(ActiveBot bot) => bot.Role.Equals("support", StringComparison.OrdinalIgnoreCase);
    private static bool HasNanite(ActiveBot bot) => bot.WeaponPartId is { } id
        && Catalog.RepresentativeCore().PartTypes.TryGetValue(id, out var p) && p.Type is DataPartTypeIGC.Weapon w
        && Catalog.RepresentativeCore().ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r) && r.Power < 0;

    internal readonly record struct RepairTarget(short Id, bool Station, short Sector, Vector3 Position,
        Vector3 Velocity, float Radius, bool FinaleBomber = false, bool FinalePusher = false);

    // Selection is harness policy; native repair discriminant/lead/fire law:
    // Igc/shipIGC.cpp:2608-2676. Only observed, living, damaged own assets qualify.
    // P778a — during a finale, a damaged finale bomber in the sector outranks
    // stations and other ships (it IS the mission), and damaged pushers outrank
    // them too (they pay the native friendly-ram damage, shipIGC.cpp:1166-1168,
    // on every contact). Outside a finale both lists are empty and the existing
    // station-then-ship order is unchanged.
    internal static RepairTarget? PickRepairTarget(ClientWorld world, short side, short own, short sector, Vector3 position,
        IReadOnlyCollection<short>? finaleBombers = null, IReadOnlyCollection<short>? finalePushers = null)
    {
        var core = Catalog.RepresentativeCore();
        if (finaleBombers is { Count: > 0 })
        {
            var bomber = world.Ships.Values.FirstOrDefault(s => finaleBombers.Contains(s.ShipId) && s.SideId == side
                    && s.SectorId == sector && !s.IsNotVisible && !s.Unknown && s.StationId < 0 && s.ParentId < 0
                    && !Catalog.IsLifepodHull(s.HullId) && s.Latest is { HullFraction: > 0 and < 1 });
            if (bomber?.Latest is { } bp)
                return new(bomber.ShipId, false, sector, bp.Position, bp.Velocity, HullRadius(bomber.HullId),
                    FinaleBomber: true);
        }
        if (finalePushers is { Count: > 0 })
        {
            var pusher = world.Ships.Values.Where(s => finalePushers.Contains(s.ShipId) && s.SideId == side
                    && s.SectorId == sector && !s.IsNotVisible && !s.Unknown && s.StationId < 0 && s.ParentId < 0
                    && !Catalog.IsLifepodHull(s.HullId) && s.Latest is { HullFraction: > 0 and < 1 })
                .OrderBy(s => (s.Latest!.Position - position).LengthSquared()).ThenBy(s => s.ShipId).FirstOrDefault();
            if (pusher?.Latest is { } pp)
                return new(pusher.ShipId, false, sector, pp.Position, pp.Velocity, HullRadius(pusher.HullId),
                    FinalePusher: true);
        }
        var station = world.Stations.Values.Where(s => s.SideId == side && s.ClusterId == sector
                && s.Position is { Length: 3 } && world.StationLive.TryGetValue(s.StationId, out var live)
                && live.HullFraction > 0 && live.HullFraction < 1)
            .OrderBy(s => (Vec3(s.Position) - position).LengthSquared()).ThenBy(s => s.StationId).FirstOrDefault();
        if (station.Position is { Length: 3 })
            return new(station.StationId, true, sector, Vec3(station.Position), Vector3.Zero,
                core.StationTypes.TryGetValue(station.StationTypeId, out var st) ? st.Radius : 100);
        var ship = world.Ships.Values.Where(s => s.ShipId != own && s.SideId == side && !s.IsNotVisible && !s.Unknown
                && s.StationId < 0 && s.ParentId < 0 && s.SectorId == sector && !Catalog.IsLifepodHull(s.HullId)
                && s.Latest is { HullFraction: > 0 and < 1 })
            .OrderBy(s => (s.Latest!.Position - position).LengthSquared()).ThenBy(s => s.ShipId).FirstOrDefault();
        return ship?.Latest is { } pose ? new(ship.ShipId, false, sector, pose.Position, pose.Velocity, HullRadius(ship.HullId)) : null;
    }

    private bool TryDriveSupport(ActiveBot bot, double now)
    {
        if (!IsSupport(bot) || bot.Cs is not { } cs || bot.IsPod || !IsParticipating(bot)) return false;
        if (bot.SupportRefitting)
        {
            HandleRelaunchAck(bot, cs);
            short dock;
            lock (cs.Gate) dock = cs.World.Ships.TryGetValue(bot.ShipId, out var own) ? own.StationId : (short)-1;
            if (dock >= 0) { bot.PodDocked = true; RelaunchDockedPod(bot, cs, now); return true; }
            if (bot.PodDocked)
            {
                if (!bot.RelaunchAckHandled || bot.LoadoutRefused) return true;
                ResetNewLife(bot, now, reinforcement: false);
                bot.SupportRefitting = false;
            }
        }
        if (!HasNanite(bot))
        {
            // Tech can unlock while this pilot is alive. Return for a real dock,
            // legal purchase and ACK rather than waiting for an artificial death.
            if (!bot.SupportRefitting && now >= bot.SupportCheckAt)
            {
                bot.SupportCheckAt = now + 5;
                lock (cs.Gate)
                {
                    var world = cs.World;
                    if (world.DevelopmentTechsFor(bot.Side) is { } techs && world.MyMoney is { } money)
                    {
                        foreach (var s in world.Stations.Values.Where(s => s.SideId == bot.Side))
                            techs = techs.Or(TechTreeBitMask.FromBytes(Catalog.RepresentativeCore().StationTypes[s.StationTypeId].TtbmEffects));
                        short civ = world.SideRoster?.Sides.Where(s => s.SideId == bot.Side).Select(s => (short?)s.CivId).FirstOrDefault() ?? -1;
                        var core = Catalog.RepresentativeCore();
                        if (core.Civilizations.ContainsKey(civ))
                        {
                            short cur = CsSector(cs);
                            var shops = world.Stations.Values.Where(s => s.SideId == bot.Side && s.Position is { Length: 3 }
                                    && core.StationTypes.TryGetValue(s.StationTypeId, out var type) && type.HasCapability(DataStationTypeIGC.SabmLand)
                                    && (s.ClusterId == cur || world.Graph?.FindShortestPath(cur, s.ClusterId) is { Count: >= 2 }))
                                .Select(s => (Station: s, Loadout: TechLoadoutSelector.SelectSupport(core, civ, techs, s.StationTypeId, long.MaxValue)))
                                .Where(s => s.Loadout is not null).OrderBy(s => s.Loadout!.Price).ThenBy(s => s.Station.StationId).ToArray();
                            if (shops.FirstOrDefault(s => s.Loadout!.Price <= money) is { Loadout: not null } shop)
                            {
                                bot.SupportRefitting = true;
                                bot.PodStationId = shop.Station.StationId;
                                bot.PodStationSector = shop.Station.ClusterId;
                                bot.PodStationCenter = Vec3(shop.Station.Position);
                            }
                            else if (shops.Length > 0 && world.AutoDonateTargetByShipId.ContainsKey(bot.ShipId))
                                cs.SendAutoDonate(-1); // Save native payday for a legal refit (FedSrv/FedSrv.CPP:5535-5544).
                        }
                    }
                }
            }
            if (!bot.SupportRefitting) return false;
            bot.TargetId = null;
            short station;
            lock (cs.Gate) station = cs.World.Ships.TryGetValue(bot.ShipId, out var me) ? me.StationId : (short)-1;
            if (station >= 0) { bot.PodDocked = true; RelaunchDockedPod(bot, cs, now); }
            else DrivePodHome(bot, cs, now);
            return true;
        }
        bot.SupportRefitting = false;
        // Accepted commander repair owns the named target and route before
        // autonomous support selection (shipIGC.cpp:1936-1939).
        if (bot.OrderedCommand?.CommandId == OrderChange.CidRepair && DriveCommanderOrder(bot, now)) return true;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out _)) return true;
        short sector = CsSector(cs);
        // P778a — during a finale the wing's damaged bombers come first, then
        // the pushers that pay collision damage; the station/ship order follows.
        List<short>? finaleBombers = null, finalePushers = null;
        if (bot.Finale is { Rotated: false } activeFinale)
            lock (_gate)
            {
                finaleBombers = activeFinale.Bots.Where(b => b.Side == bot.Side && b.FinaleBomberActive && !b.IsPod)
                    .Select(b => b.ShipId).ToList();
                finalePushers = activeFinale.Bots.Where(b => b.Side == bot.Side && b.FinaleEscort && !b.IsPod && b.PushContacts > 0)
                    .Select(b => b.ShipId).ToList();
            }
        RepairTarget? target;
        lock (cs.Gate) target = PickRepairTarget(cs.World, bot.Side, bot.ShipId, sector, pos, finaleBombers, finalePushers);
        if (target is { } repair)
        {
            bot.TargetId = repair.Id;
            if (bot.SupportTraceTarget != repair.Id || bot.SupportTraceStation != repair.Station)
            {
                Console.WriteLine($"[support] {bot.Name} ship={bot.ShipId} side={bot.Side} "
                    + $"repair={(repair.Station ? "station" : repair.FinaleBomber ? "finale-bomber" : repair.FinalePusher ? "pusher" : "ship")} target={repair.Id} sector={sector}");
                bot.SupportTraceTarget = repair.Id; bot.SupportTraceStation = repair.Station;
            }
            FlyRepairTarget(bot, cs, repair);
            return true;
        }
        bot.TargetId = null; bot.SupportTraceTarget = null;
        // Recall to observed damaged stations first; otherwise accompany this
        // mission's strike group. Never inspect server entities or a human loadout.
        short? destination = null;
        Vector3? anchor = null;
        ActiveBot[] companions;
        lock (_gate) companions = _bots.Where(b => b.Side == bot.Side && !IsSupport(b) && !b.IsPod && IsParticipating(b)).ToArray();
        lock (cs.Gate)
        {
            destination = cs.World.Stations.Values.Where(s => s.SideId == bot.Side
                    && cs.World.StationLive.TryGetValue(s.StationId, out var live) && live.HullFraction is > 0 and < 1)
                .OrderBy(s => s.StationId).Select(s => (short?)s.ClusterId).FirstOrDefault();
            var strike = companions.Where(b => b.FinaleBomberActive)
                .Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId)).Where(s => s?.Latest is not null && !s.IsDead)
                .OrderBy(s => s!.ShipId).FirstOrDefault()
                ?? companions.Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId))
                    .Where(s => s?.Latest is not null && !s.IsNotVisible && s.StationId < 0 && s.SectorId == sector)
                    .OrderBy(s => (s!.Latest!.Position - pos).LengthSquared()).ThenBy(s => s!.ShipId).FirstOrDefault();
            if (destination is null && strike?.Latest is { } pose)
            { destination = strike.SectorId; anchor = pose.Position; }
        }
        destination ??= bot.TargetSector;
        if (destination is { } goal && goal != sector)
        {
            List<short>? path;
            lock (cs.Gate) path = cs.World.Graph?.FindShortestPath(sector, goal);
            if (path is { Count: >= 2 } && TransitWarp(cs, bot, sector, path[1])) return true;
        }
        if (anchor is { } center) CombatFlyTo(bot, center, 250, 600, 0);
        else LoiterCombat(bot, now);
        return true;
    }

    private static void FlyRepairTarget(ActiveBot bot, ConnectionSession cs, RepairTarget target)
    {
        if (!TryCsFreshPose(cs, bot.ShipId, out var pos, out var velocity, out var orientation)) return;
        if (bot.WeaponPartId is not { } gun || !TryWeaponBallistics(gun, out float speed, out _, out float life, out bool absolute)) return;
        bool lead = TrySolveLead(pos, velocity, target.Position, target.Velocity, target.Radius, speed, absolute, out var aim, out float t);
        if (!lead) aim = target.Position - pos;
        var (yaw, pitch, angle) = SteerTowardDirection(orientation, aim);
        int state = 0;
        bool dodging = TryDodgeGoal(cs, bot, pos, out var dodge);
        if (dodging) { CombatFlyTo(bot, dodge, 15, 200, 0); return; }
        if (lead && t <= life * .25f)
        {
            if (Showcase.StrafeEnabled(bot.WingmanBehaviour)) state |= ShipControlState.LeftButton;
            if (angle < MathF.PI * .5f) state |= angle > .10f ? ShipControlState.BackwardButton : ShipControlState.ForwardButton;
        }
        if (lead && angle <= .10f && t <= life * .95f) state |= ShipControlState.AllWeapons;
        cs.CommandControls(new ControlData(yaw, pitch, 0, AttackThrottle(angle)), state);
    }
}
