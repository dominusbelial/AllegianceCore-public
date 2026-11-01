using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using V = Allegiance.Core.Collision.Vector3;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    // The two native bodies are identical (apart from names/debug text/whitespace):
    // training/mission6.cpp:82-287; training/mission10.cpp:78-283.
    private bool HandleArenaDefaultOrder(Ship ship)
    {
        var lesson = _activeMission;
        if (!Mission.IsTraining || lesson is not (Mission6 or Mission10) || ship.Sector is not { } sector) return false;
        Ship? enemy = null, support = null, follow = null;
        float enemyDistance = 15000, supportDistance = 1000, followDistance = 1000;
        foreach (var other in sector.Models.OfType<Ship>())
        {
            if (other == ship || other.Dead || other.StationId is not null || other.ParentShip is not null
                || !ArenaCanSee(ship, other, other.SideId, other.Position, other.Radius, other.EffectiveSignature)) continue;
            float distance = (other.Position - ship.Position).Length();
            if (other.SideId == ship.SideId)
            {
                if (distance < supportDistance && other.PilotType == Ship.PilotTypeWingman
                    && other.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0)?.Type.Type
                        is DataPartTypeIGC.Weapon weapon
                    && Mission.Core.ProjectileTypes.TryGetValue(weapon.ProjectileTypeId, out var projectile) && projectile.Power > 0)
                { support = other; supportDistance = distance; }
                var (target, command) = other.GetNpcPlan();
                bool valid = command == OrderChange.CidAttack && target is Ship { Fraction: > 0, Dead: false }
                    || command == OrderChange.CidGoto && target is not null && target is not Ship
                        && target is not EquipmentTarget { ObjectType: IgcObjectType.Station }
                        && (target.Position - ship.Position).Length() > 700;
                if (distance < followDistance && valid) { follow = other; followDistance = distance; }
            }
            else if (distance < enemyDistance && other.Fraction > 0
                && (lesson.GetKillCount() > 2 || other.PilotType < Ship.PilotTypePlayer))
            {
                enemy = other;
                // Preserve native's asymmetric comparison: raw distance compared to the
                // previous candidate's weighted distance, not a global weighted minimum.
                enemyDistance = other.PilotType == Ship.PilotTypeMiner ? distance : distance * 1.5f;
            }
        }
        if (enemyDistance < 1000 && enemy is not null)
        {
            ArenaAttack(ship, enemy);
            if (support is not null && support.GetNpcPlan().Target != enemy) ArenaAttack(support, enemy, "*");
            return true;
        }
        if (follow is not null)
        {
            var (target, command) = follow.GetNpcPlan();
            if (command == OrderChange.CidAttack) ArenaAttack(ship, target!, "`");
            else
            {
                ArenaOrder(ship, target!, command);
                if ((follow.WingmanBehaviour & 0x80) != 0) ship.WingmanBehaviour |= 0x80;
            }
            return true;
        }
        var home = Mission.Sectors.First(s => s.Id == StartSectorId);
        bool homeOccupied = home.Models.OfType<Ship>().Any(s => !s.Dead && s.StationId is null && s.ParentShip is null);
        if (enemy is not null && (sector.Id == StartSectorId || sector.Id == 2082 || !homeOccupied))
        { ArenaAttack(ship, enemy); return true; }

        // mission6.cpp:213-286 / mission10.cpp:209-282. Hidden cluster pursuit,
        // or local station/He3/random patrol with temporary sector aggression.
        short destination = homeOccupied ? StartSectorId
            : PlayerShip.StationId is null ? PlayerShip.Sector?.Id ?? sector.Id : sector.Id;
        V point = V.Zero;
        int kind = DataBuoyIGC.CBuoyCluster;
        if (destination == sector.Id)
        {
            var points = new List<V>();
            foreach (var station in sector.StationRuntimes)
                if (!station.Dead && station.SideId != ship.SideId
                    && ArenaCanSee(ship, station, station.SideId, station.Center, station.Radius, station.Type.Signature))
                    points.Add(station.Center + new V(300, 300, 250));
            foreach (var rock in sector.Mineable)
                if (!rock.Dead && rock.CanMineHe3)
                    points.Add(new V(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2] + 350));
            for (int i = 0; i < 2; i++)
            {
                V random = new(Random.Shared.NextSingle() * 6000 - 3000,
                    Random.Shared.NextSingle() * 6000 - 3000, Random.Shared.NextSingle() * 2000 - 1000);
                // Native deliberately tests the SHIP position, not the random point.
                // Pickable static bodies are stations, asteroids, warps and probes.
                foreach (var body in ArenaPatrolObstacles(sector))
                {
                    V delta = ship.Position - body.Position;
                    float min = body.Radius + ship.Radius + 10;
                    float length = delta.Length();
                    if (length < min && length > 0) random = body.Position + delta / length * min;
                }
                points.Add(random);
            }
            point = points[Random.Shared.Next(points.Count)];
            kind = DataBuoyIGC.CBuoyWaypoint;
            ship.WingmanBehaviour |= 0x80;
        }
        int id;
        do { id = NextId(); } while (Mission.GetBuoy((short)id) is not null);
        CreateWaypoint(id, ToNum(point), destination, kind, false);
        ArenaOrder(ship, Mission.GetBuoy((short)id)!, OrderChange.CidGoto);
        return true;
    }

    private static IEnumerable<(V Position, float Radius)> ArenaPatrolObstacles(Sector sector)
    {
        foreach (var station in sector.StationRuntimes) if (!station.Dead) yield return (station.Center, station.Radius);
        foreach (var rock in sector.Mineable) if (!rock.Dead)
            yield return (new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]), rock.Radius);
        foreach (var warp in sector.Warps) yield return (warp.Position, warp.Radius);
        foreach (var probe in sector.Models.OfType<Probe>()) if (!probe.Dead) yield return (probe.Position, probe.ProbeType.Radius);
    }

    // Igc/shipIGC.h:341-389: side knowledge OR own directional scanner + LOS.
    private bool ArenaCanSee(Ship observer, ISideVisible body, short? owner, V position, float radius, float signature)
    {
        if (observer.SideId is not { } side || observer.Sector is not { } sector) return false;
        if (owner == side || body.SeenBySide(side)) return true;
        if (body is Ship { Flag: not null }) return true;
        if (owner is { } sid) signature /= Mission.Sides.First(s => s.Id == sid).GlobalAttributes.Get(GlobalAttributeSet.Signature);
        float range = VisibilityMath.DetectionRadius(observer.Radius, observer.EffectiveScannerRange(), radius, signature);
        V displacement = observer.Orientation.TimesInverse(observer.Position - position);
        if (displacement.Z < 0) displacement = displacement with { Z = displacement.Z * 4 };
        if (displacement.LengthSquared() > range * range) return false;
        foreach (var rock in sector.Mineable)
            if (!rock.Dead && !VisibilityMath.LineOfSightExist(observer.Position, new(position, radius),
                [new(new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]), rock.Radius)])) return false;
        return true;
    }

    private void ArenaAttack(Ship ship, Model target, string suffix = "")
    {
        Mission.Site.DroneChatEvent(ship, ship.SideId ?? -1, 1337, $"Attacking {(target is Ship victim ? victim.Name : target.Id.ToString())}{suffix}");
        ArenaOrder(ship, target, OrderChange.CidAttack);
    }

    private void ArenaOrder(Ship ship, Model target, short command)
    {
        // Host IssueCommand owns real command slots and buoy consumers.
        var type = EquipmentTarget.TypeOf(target);
        IssueCommand(ship.Id, TrainingMissionIds.CommandAccepted,
            type == IgcObjectType.Ship ? target.Id : null, type == IgcObjectType.Buoy ? target.Id : null,
            type == IgcObjectType.Station ? target.Id : null, command,
            type == IgcObjectType.Asteroid ? target.Id : null, type == IgcObjectType.Warp ? target.Id : null,
            type == IgcObjectType.Probe ? target.Id : null);
        if (command == OrderChange.CidAttack) ship.SetWingmanAttackOrder(target);
    }

    // training/mission6.cpp:319-346; mission10.cpp:315-342.
    private static void ApplyArenaTech(Mission mission)
    {
        var side = mission.Sides.First(s => s.Id == 0);
        var tech = side.DevelopmentTechs;
        foreach (short id in new short[] { 154, 104, 151, 14, 58, 232, 174, 39 })
        {
            if (!mission.Core.PartTypes.TryGetValue(id, out var part)
                && !mission.Core.TryBuildExpendableLauncher(id, out part)) continue;
            tech = tech.Or(TechTreeBitMask.FromBytes(part.TtbmEffects));
            if (id is not (154 or 151)) tech = tech.Or(TechTreeBitMask.FromBytes(part.TtbmRequired));
        }
        foreach (short id in new short[] { 488, 481, 486, 87 })
            if (mission.Core.HullTypes.TryGetValue(id, out var hull))
                tech = tech.Or(TechTreeBitMask.FromBytes(hull.TtbmEffects)).Or(TechTreeBitMask.FromBytes(hull.TtbmRequired));
        side.DevelopmentTechs = tech;
    }
}
