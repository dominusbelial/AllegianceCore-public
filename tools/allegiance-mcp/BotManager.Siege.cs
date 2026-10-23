using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private static bool ResearchSiegeDuty(ActiveBot bot) => (bot.FinaleBomberActive || bot.FinaleRefitting) && ResearchFinale(bot);

    // Port finale duty orchestration. Research strikes use already purchased
    // hardware immediately, or fly home for an ordinary dock/BUY_LOADOUT/ACK.
    // Native purchase/launch: FedSrv/FedSrv.CPP:6189-6190,6247-6259,6282-6303.
    // No death, teleport, grant or CanBuy bypass is used to form this wing.
    private bool TryDriveResearchFinaleRefit(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!bot.FinaleBomberReserved || !ResearchFinale(bot) || bot.Finale is not { Rotated: false } run
            || run.TriggeredAt is null || run.GameOverAt is not null || !run.Plans.TryGetValue(bot.Side, out var plan)) return false;
        short hullId, stationId;
        long money;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me)) return false;
            hullId = me.HullId; stationId = me.StationId; money = cs.World.MyMoney ?? 0;
        }
        if (!bot.FinaleRefitting)
        {
            bool resupply;
            lock (cs.Gate) resupply = ResearchSiegeNeedsResupply(bot, cs, Catalog.RepresentativeCore().StationTypes[plan.TargetTypeId]);
            bool upgradingFallback = bot.FinaleBomberActive && bot.FinaleGrantVerified
                && Catalog.RepresentativeCore().HullTypes.TryGetValue(hullId, out var currentHull)
                && Showcase.ClassifyHull(currentHull) != Showcase.HullRole.Bomber;
            if (!resupply && !upgradingFallback && bot.PreviousLegalLoadout is { Strike: not null } accepted && accepted.HullId == hullId
                && bot.Granted.HullId == hullId && bot.Granted.Parts is { } granted
                && accepted.Parts.All(p => granted.Contains(p)))
            {
                if (bot.FinaleBomberActive && bot.FinaleGrantVerified) return false;
                bot.FinaleOriginal ??= new(bot.HullId, bot.HullName, bot.Parts, bot.WeaponPartId,
                    bot.LauncherPartId, bot.MissileReadyTime, bot.WingmanBehaviour, bot.TargetSector, bot.RouteIndex, bot.Role);
                bot.FinaleBomberActive = bot.FinaleGrantVerified = true;
                bot.Role = "bomber"; bot.TargetSector = plan.Sector;
                bot.WingmanBehaviour |= Showcase.WbbmUseMissiles;
                bot.TargetId = bot.OrderedTargetId = null; bot.Arrived = false; bot.ArrivedAt = null;
                bot.StagingSector = -1; bot.StagePhase = StageReleasedPhase; bot.ReleasedAt = now;
                EndDefense(bot);
                bot.Path.Clear(); bot.PathGoal = -1; bot.RoutePath = null; bot.RouteIndex = -1;
                AssignResearchSiegeRoute(bot, run, plan);
                lock (run.Sync) run.StrikeStartedAt ??= now;
                Console.WriteLine($"[finale-duty] {bot.Name} ship={bot.ShipId} hull={hullId}: accepted strike takes station duty target={plan.TargetId} sector={plan.Sector}");
                return false;
            }
            if (now < bot.FinaleRefitCheckAt) return false;
            bot.FinaleRefitCheckAt = now + 5;
            var available = SelectFinaleLoadout(bot, Catalog.RepresentativeCore().StationTypes[plan.TargetTypeId]);
            if (available is null || (upgradingFallback
                && Showcase.ClassifyHull(Catalog.RepresentativeCore().HullTypes[available.HullId]) != Showcase.HullRole.Bomber)) return false;
            if (available.Price > money)
            {
                if (!bot.FinaleKeepsPayday)
                {
                    // Native voluntary auto-donation opt-out (FedSrv/FedSrv.CPP:5535-5544).
                    // A living pilot otherwise donates every payday to the commander
                    // and never gains the personal funds for a legal refit.
                    cs.SendAutoDonate(-1);
                    bot.FinaleKeepsPayday = true;
                    Console.WriteLine($"[finale-duty] {bot.Name} ship={bot.ShipId}: keep native payday for legal strike refit cost={available.Price} wallet={money}");
                }
                return false;
            }
            bot.FinaleRefitting = true;
            bot.TargetId = bot.OrderedTargetId = null; EndDefense(bot);
            bot.PodStationId = bot.PodStationSector = -1;
            Console.WriteLine($"[finale-duty] {bot.Name} ship={bot.ShipId}: return to dock for legal strike {(resupply ? "resupply" : "refit")} target={plan.TargetId}");
        }
        if (stationId >= 0)
        {
            bot.PodDocked = true;
            bot.FinaleRefitLaunchPending = true;
            RelaunchDockedPod(bot, cs, now);
            return true;
        }
        if (bot.FinaleRefitLaunchPending && bot.RelaunchAckHandled)
        {
            ResetNewLife(bot, now, reinforcement: true);
            if (bot.FinaleGrantVerified) lock (run.Sync) run.StrikeStartedAt ??= now;
            return false;
        }
        DrivePodHome(bot, cs, now);
        return true;
    }

    private static bool ResearchSiegeNeedsResupply(ActiveBot bot, ConnectionSession cs, DataStationTypeIGC target)
    {
        if (!bot.FinaleBomberActive || !bot.FinaleGrantVerified || !ResearchFinale(bot)
            || bot.PreviousLegalLoadout?.Strike is null || bot.Granted.Parts is not { } parts) return false;
        var core = Catalog.RepresentativeCore();
        bool DamagesStation(byte type, float power) => power > 0
            && core.Constants.DamageConstants[type & 0x1f][target.DefenseTypeArmor] > 0
            && core.Constants.DamageConstants[type & 0x1f][target.DefenseTypeShield] > 0;
        foreach (var part in parts.Where(p => p.MountId < 128))
            if (core.PartTypes.TryGetValue(part.PartId, out var type) && type.Type is DataPartTypeIGC.Weapon gun
                && core.ProjectileTypes.TryGetValue(gun.ProjectileTypeId, out var round)
                && DamagesStation(round.DamageType, round.Power)
                && (gun.CAmmoPerShot == 0 || cs.World.Ships.TryGetValue(bot.ShipId, out var own) && own.Latest?.AmmoFraction > 0))
                return false; // Still has a sustainable station weapon.
        int carried = parts.Where(p => core.Launchers.TryGetValue(p.PartId, out var launcher)
                && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile)
                && DamagesStation(missile.DamageType, missile.Power))
            .Sum(p => (int)p.Amount);
        return carried > 0 && cs.World.OwnMissilesFired - bot.FinaleMissileLaunchBase >= carried;
    }

    // Continue the existing station-attack duty after its original home falls.
    // Use only station identities observed by this client's side, preferring
    // restart bases because native elimination requires their loss
    // (FedSrv/fsmission.cpp:3669-3731,5318-5427).
    private static void AdvanceResearchSiegeDuty(ActiveBot bot, ConnectionSession cs)
    {
        if (!bot.FinaleBomberActive || !bot.FinaleGrantVerified || !ResearchFinale(bot)) return;
        lock (cs.Gate)
        {
            var core = Catalog.RepresentativeCore();
            bool Enemy(DataStationIGC station) => station.SideId >= 0 && station.SideId != bot.Side;
            if (cs.World.Stations.Values.Any(s => Enemy(s) && s.ClusterId == bot.TargetSector)) return;
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } pose) return;
            var next = cs.World.Stations.Values.Where(Enemy)
                .OrderByDescending(s => core.StationTypes.TryGetValue(s.StationTypeId, out var type)
                    && type.HasCapability(DataStationTypeIGC.SabmRestart))
                .ThenBy(s => cs.World.Graph?.FindShortestPath(own.SectorId, s.ClusterId)?.Count ?? int.MaxValue)
                .ThenBy(s => (Vec3(s.Position) - pose.Position).LengthSquared())
                .ThenBy(s => s.StationId).Select(s => (DataStationIGC?)s).FirstOrDefault();
            if (next is not { } target || bot.TargetSector == target.ClusterId) return;
            bot.TargetSector = target.ClusterId; bot.TargetId = bot.OrderedTargetId = null;
            bot.Arrived = false; bot.ArrivedAt = null;
            bot.StagingSector = -1; bot.StagePhase = StageReleasedPhase;
            bot.Path.Clear(); bot.PathGoal = -1; bot.RoutePath = null;
            if (cs.World.Graph is { } graph)
            {
                var routes = graph.FindDistinctEntryRoutes(own.SectorId, target.ClusterId, 3);
                if (routes.Count > 0) bot.RoutePath = routes[Math.Abs(bot.RouteIndex) % routes.Count];
            }
            Console.WriteLine($"[finale-duty] {bot.Name} ship={bot.ShipId}: continue siege at observed station={target.StationId} sector={target.ClusterId}");
        }
    }

    // Native ship-delete caches unseen ships in the null cluster
    // (clintlib/appmsg.cpp:475-488); Dodge scans cluster pickables only
    // (Igc/common.cpp:1715). Retained ClientWorld poses are not obstacles.
    private static bool TryResearchSiegeDodge(ConnectionSession cs, ActiveBot bot, short sector,
        Vector3 position, Vector3 velocity, DataHullTypeIGC hull, out Vector3 goal)
    {
        var core = Catalog.RepresentativeCore();
        var obstacles = new List<ShipDodgeLaw.Obstacle>();
        foreach (var rock in cs.World.Asteroids.Values)
            if (rock.ClusterId == sector && rock.Position is { Length: 3 })
                obstacles.Add(new(Vec3(rock.Position), Vector3.Zero, rock.Radius, false));
        if (!hull.HasCapability(DataHullTypeIGC.HabmBoard))
            foreach (var station in cs.World.Stations.Values)
                if (station.ClusterId == sector && station.Position is { Length: 3 }
                    && !(bot.FinaleBomberActive && station.StationId == bot.TargetId && station.SideId != bot.Side))
                {
                    float radius = core.StationTypes.TryGetValue(station.StationTypeId, out var type) && type.Radius > 0
                        ? type.Radius : 150f;
                    obstacles.Add(new(Vec3(station.Position), Vector3.Zero, radius, false));
                }
        foreach (var other in cs.World.Ships.Values)
            if (other.ShipId != bot.ShipId && other.CanDrawInSector(sector) && other.Latest is { } pose)
                obstacles.Add(new(pose.Position, pose.Velocity, HullRadius(other.HullId), false));
        var mover = new ShipDodgeLaw.Mover(position, velocity, hull.Length * .5f + 1, hull.Thrust / hull.Mass);
        return ShipDodgeLaw.TryComputeDodgeGoal(mover, obstacles,
            Allegiance.ClientCore.Flight.ClientAutopilotDodge.DodgeGoalDistance,
            Allegiance.ClientCore.Flight.ClientAutopilotDodge.FallbackTMax, out goal);
    }

    // Native GotoPlan faces the route while dodge applies directional thrust
    // (Igc/common.cpp:2000-2016), then diverts around static blockers (:2120-2224).
    // This fixes the harness's nose-away/full-throttle orbit in dense home sectors.
    private static bool TryDriveResearchSiegeTransit(ActiveBot bot, ConnectionSession cs, Vector3 destination)
    {
        if (!ResearchSiegeDuty(bot)) return false;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } pose
                || !Catalog.RepresentativeCore().HullTypes.TryGetValue(me.HullId, out var hull)) return false;
            var core = Catalog.RepresentativeCore();
            var obstacles = new List<SiegeNavigation.Obstacle>();
            foreach (var rock in cs.World.Asteroids.Values.Where(r => r.ClusterId == me.SectorId))
                obstacles.Add(new(Vec3(rock.Position), rock.Radius));
            foreach (var station in cs.World.Stations.Values.Where(s => s.ClusterId == me.SectorId))
                if (core.StationTypes.TryGetValue(station.StationTypeId, out var type))
                    obstacles.Add(new(Vec3(station.Position), type.Radius));
            foreach (var other in cs.World.Ships.Values.Where(s => s.ShipId != bot.ShipId && s.StationId < 0
                         && s.CanDrawInSector(me.SectorId) && s.Latest is not null))
                if (other.Latest is { } stationary && stationary.Velocity.LengthSquared() < 1)
                    obstacles.Add(new(stationary.Position, HullRadius(other.HullId)));
            var position = pose.Position;
            bool dodging = TryDodgeGoal(cs, bot, position, out var dodgeGoal);
            var goal = dodging ? destination : SiegeNavigation.DivertGoal(position, hull.Length * .5f, destination, obstacles);
            var attributes = cs.World.GlobalAttributesFor(bot.Side);
            var turn = DockApproach.TurnState.FromHull(hull, attributes,
                pose.TurnRates[ControlData.AxisYaw], pose.TurnRates[ControlData.AxisPitch]);
            var step = new DockApproach.Step(DockApproach.Mask.Fast, 0, goal, Vector3.Zero);
            // Native turnToFace brakes angular drift before crossing the bearing
            // (Igc/common.cpp:210-246). A cross-product-only servo oscillates a
            // heavy bomber around the launch area despite commanding full thrust.
            var controls = SiegeNavigation.Steer(step, position, pose.Orientation, pose.Velocity, turn);
            int state;
            if (dodging)
            {
                controls = controls with { Throttle = -1 };
                state = SiegeNavigation.DirectionButtons(dodgeGoal - position, pose.Orientation);
            }
            else
            {
                // Native common.cpp:2231-2235,2309: kill forward throttle during
                // a wide turn, using directional RCS to seek the route instead.
                var delta = goal - position;
                if (delta.LengthSquared() > .1f && pose.Orientation.GetForward() * delta.Normalize() <= MathF.Cos(MathF.PI / 8))
                    controls = controls with { Throttle = -1 };
                state = DockApproach.MovementButtons(step, position, pose.Orientation, pose.Velocity, turn);
            }
            cs.CommandControls(controls, CloakApproach(bot, cs, state));
            return true;
        }
    }
}
