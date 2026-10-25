using System.Runtime.InteropServices;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using G = Allegiance.Core.Sim.NativeGotoPlan;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    private G.Waypoints _nativeWaypoints;
    private bool _nativeLayerPlanFinished;
    private List<G.Obstacle>? _nativeObstacles;
    private G.Edge[]? _nativeEdges;
    private G.Cluster[]? _nativeClusters;
    private G.Path[]? _nativePaths;
    private short[]? _nativeExplored;
    private HashSet<Sector>? _nativeTerritory;
    private List<VisibilityMath.SightBody>? _nativeSightRocks;

    /// <summary>Native GetWantBoost/SetWantBoost, common.cpp:1813-1821.
    /// Drone callers may request a long boost; the planner clears it within 2000
    /// units of the original target, even while travelling via a warp.</summary>
    public bool WantBoost { get; set; }

    /// <summary>P817 adapter, shipIGC.cpp:2795-2833. The combat-specific wingman
    /// controller precedes GotoPlan in native (:2350-2679) and keeps its own law.
    /// Every other NPC travel/idle case goes through the pure planner.</summary>
    internal bool ApplyNativeNpcAutopilot(double now)
    {
        if (!IsNpc || Mission?.NativeGotoPlanEnabled != true || Sector is not { } sector) return false;
        _nativeLayerPlanFinished = false;
        if (AttackTarget is not null && PlanTarget is null && GotoWaypoint is null && !IsRunningAway && !HasRepairOrder && !_isGettingAmmo)
            return false;
        IntendedNextWarpId = -1;
        // Native's busy builder block precedes GotoPlan (shipIGC.cpp:2262-2300).
        // ApplyBuilderDrillingControls owns these controls later in this tick.
        if (IsBuilding || _isDrilling && BuilderAsteroidTarget is { } drillingRock && BuildApproachOnLine(drillingRock)) return true;
        FillNativeSightRocks(sector);
        bool active = TryNativeTarget(out var target, out var station, out var carrier);
        float dt = (float)Math.Max(.001, now - LastUpdate);
        var mover = new G.Mover(Position, Velocity, Orientation, .5f * HullType.Length,
            DockApproach.TurnState.FromShip(this), EffectiveMaxTurnRate(ControlData.AxisRoll),
            EffectiveTurnTorque(ControlData.AxisRoll), TurnRates[ControlData.AxisRoll]);
        bool replan = active && _nativeWaypoints.Refresh(sector.Id, target);
        if (!active) _nativeWaypoints.Reset();
        // An explicit Enter-warp target completes on changing to its far cluster
        // (common.cpp:1850-1862), before any route back to the original cluster.
        if (active && _nativeWaypoints.Mask == 0 && target.Kind == G.Kind.Warp)
        {
            GotoWaypoint = null; Controls = ControlData.Neutral;
            StateM &= ~(ShipControlState.MoveButtonsMask | ShipControlState.AfterburnerButton | ShipControlState.CoastButton | ShipControlState.WeaponsMask);
            if (WantBoost) StateM |= ShipControlState.AfterburnerButton; // Execute applies a pending boost even on completion (:1813-1821)
            return true;
        }
        Vector3 boostTarget = target.Position;
        bool reachable = active && target.Present;
        if (reachable && target.Cluster != sector.Id)
        {
            // Native PreplotShipMove's ripcord intent remains ahead of travel.
            // The descriptor is constant: no interpolation/allocation each tick.
            if (TryRipcordOrDefer(target.Cluster, now, "native goto")) return true;
            if (replan || _nativeWaypoints.WarpId < 0)
                _nativeWaypoints.SetWarp(FindNativeWarp(target.Cluster));
            ResolvedWarp? warp = null;
            for (int i = 0; i < sector.Warps.Count; i++)
                if (sector.Warps[i].Warp.WarpId == _nativeWaypoints.WarpId) { warp = sector.Warps[i]; break; }
            if (warp is null) reachable = false;
            else
            {
                IntendedNextWarpId = warp.Warp.WarpId;
                target = new(NativeKey(G.Kind.Warp, warp.Warp.WarpId), sector.Id, G.Kind.Warp,
                    G.Objective.Enter, warp.Position, warp.Radius, WarpDestination: warp.Destination?.Id ?? -1);
            }
        }
        // GetGotoPosition geometry is value data, not a newly allocated bay list.
        Span<DockApproach.DockBay> bays = stackalloc DockApproach.DockBay[64];
        int bayCount = reachable && target.Kind != G.Kind.Warp ? NativeBays(station, carrier, bays) : 0;
        _nativeObstacles ??= new(64);
        FillNativeObstacles(sector, _nativeObstacles);
        var result = G.Execute(mover, target, bays[..bayCount], CollectionsMarshal.AsSpan(_nativeObstacles), dt,
            HullType.BackMultiplier, wantBoost: WantBoost && active, dodge: true, active: reachable,
            pending: active, boostTargetPosition: boostTarget);
        Controls = result.Controls;
        const int maneuver = ShipControlState.MoveButtonsMask | ShipControlState.AfterburnerButton |
            ShipControlState.CoastButton | ShipControlState.OneWeapon | ShipControlState.AllWeapons;
        StateM = (StateM & ~maneuver) | result.Buttons;
        // Ship's idle plot does not execute GotoPlan or consume its boost latch
        // (shipIGC.cpp:2308-2347). Active/pending plans use the pure result.
        if (active) WantBoost = result.WantBoost;
        if (PilotType == PilotTypeWingman && (_isGettingAmmo || IsRunningAway) && !result.Done && Controls.Throttle == 1)
            StateM |= ShipControlState.AfterburnerButton; // shipIGC.cpp:2752-2754,2830-2833
        if (result.Done && reachable)
        {
            _nativeWaypoints.Reset();
            if (PilotType == PilotTypeLayer && LayerBuildOrder is not null)
                _nativeLayerPlanFinished = true; // shipIGC.cpp:2823-2828; consumed before this tick's movement
            else if (BuilderAsteroidTarget is not null && target.Kind == G.Kind.Asteroid && target.Objective == G.Objective.Enter)
            {
                _isDrilling = true; // native completion latch, shipIGC.cpp:2810-2822
                StateM = 0; // drilling is the managed _isDrilling latch
                Controls = new(0, 0, 1, 0);
            }
            else if (GotoWaypoint is { } gw)
            {
                if (gw.TargetStationId is { } id && AcceptedCommandId == OrderChange.CidGoto && AcceptedTarget?.StationId == id)
                { AcceptedTarget = null; AcceptedCommandId = OrderChange.CidNone; }
                GotoWaypoint = null; // shipIGC.cpp:2802-2810
            }
        }
        return true;
    }

    private bool NativeFriendlySide(short? other)
    {
        if (SideId is not { } side || other is null) return false;
        if (side == other) return true;
        var sides = Mission!.Sides;
        sbyte? group = null;
        for (int i = 0; i < sides.Count; i++) if (sides[i].Id == side) { group = sides[i].AllyGroup; break; }
        if (group is null) return false;
        for (int i = 0; i < sides.Count; i++) if (sides[i].Id == other) return sides[i].AllyGroup == group;
        return false;
    }

    private static int NativeKey(G.Kind kind, short id) => ((int)kind + 1) * 65536 + (ushort)id;
    private bool TryNativeTarget(out G.Target target, out Station? station, out Ship? carrier)
    {
        target = default; station = null; carrier = null;
        if (IsRunningAway && _retreatRepairShip is { Dead: false, Sector: { } rs } repair)
        {
            var objective = HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier) && repair.HullType.HasCapability(DataHullTypeIGC.HabmCarrier)
                ? G.Objective.Enter : G.Objective.Goto; // ResetWaypoint, shipIGC.cpp:3999-4003
            target = ShipTarget(repair, rs.Id, objective); carrier = repair; return true;
        }
        if (HasRepairOrder)
        {
            if (RepairTarget is { Sector: { } ss } ship) { target = ShipTarget(ship, ss.Id, G.Objective.Goto); return true; }
            if (RepairStationTarget is { } st) { target = StationTarget(st.Data, G.Objective.Goto, out station); return true; }
        }
        if (_isGettingAmmo && _gettingAmmoCarrier is { Dead: false, Sector: { } cs } ammo)
        { target = ShipTarget(ammo, cs.Id, G.Objective.Enter); carrier = ammo; return true; }
        if (GotoWaypoint is { } gw)
        {
            if (Sector!.Id == gw.ClusterId && gw.Kind == GotoKind.Cluster)
            { GotoWaypoint = null; return false; } // FedSrv.CPP:3302
            if (gw.TargetId is { } objectId && gw.TargetType is { } objectType)
                return NativeOrderTarget(objectType, objectId, gw, out target, out station, out carrier);
            if (gw.TargetStationId is { } stationId)
                return NativeOrderTarget(IgcObjectType.Station, stationId, gw, out target, out station, out carrier);
            // Native buoys default visible (igc.h:1921), radius 20; the
            // wingman's internal investigation buoys are hidden, radius 2
            // (shipIGC.cpp:2435,2730; buoyigc.cpp:44-48).
            target = new(NativeKey(G.Kind.Point, 0), gw.ClusterId, G.Kind.Point, G.Objective.Goto, gw.Position,
                _acceptedWingmanAttackTarget is null ? 20 : 2);
            return true;
        }
        if (PlanTarget is { } destination)
        {
            target = StationTarget(destination, G.Objective.Enter, out station);
            // ResetWaypoint's capability gate, shipIGC.cpp:4020-4048.
            short need = HullType.HasCapability(DataHullTypeIGC.HabmFighter) ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand;
            if (station is null || !station.Type.HasCapability(need) || !NativeFriendlySide(station.SideId))
                target = target with { Objective = G.Objective.Goto };
            return true;
        }
        if (LayerBuildOrder is { } lb)
        { target = new(NativeKey(G.Kind.Point, 1), lb.ClusterId, G.Kind.Point, G.Objective.Goto, lb.Position, 20); return true; }
        var rock = MiningAsteroidTarget ?? BuilderAsteroidTarget ?? LayerAsteroidTarget;
        if (rock is not null)
        {
            var p = rock.Data.Position;
            var r = rock.Data.Rotation;
            Vector3 axis = r.Length >= 3 ? new(r[0], r[1], r[2]) : default;
            // Synthetic axis-less rocks cannot satisfy native DoApproach's unit-axis assert.
            // Retain the existing parked-reservation build fallback for those fixtures.
            var objective = rock == BuilderAsteroidTarget && PilotType == PilotTypeBuilder && axis.LengthSquared() > .25f ? G.Objective.Enter : G.Objective.Goto;
            target = new(NativeKey(G.Kind.Asteroid, rock.Data.AsteroidId), rock.Data.ClusterId, G.Kind.Asteroid,
                objective, new(p[0], p[1], p[2]), rock.Radius, Axis: axis, Present: !rock.Dead && NativeCanSee(rock, null, new(p[0], p[1], p[2]), rock.Radius, rock.Data.Signature, rock.Data.ClusterId));
            return true;
        }
        if (DefendTarget is { Sector: { } ds } charge)
        { target = ShipTarget(charge, ds.Id, G.Objective.Goto); return true; }
        if (PickupTarget is { } pod)
        {
            if (pod.Dead || pod.StationId is not null || pod.Sector is null)
            { PickupTarget = null; return false; }
            target = ShipTarget(pod, pod.Sector.Id, G.Objective.Enter) with { Pickup = true };
            return true;
        }
        return false;
    }
    // Keep typed command targets live instead of flattening them to buoy points.
    // Native ResetWaypoint chooses the objective by model type (shipIGC.cpp:3990-4151).
    private bool NativeOrderTarget(IgcObjectType type, short id, GotoOrder order,
        out G.Target target, out Station? station, out Ship? carrier)
    {
        station = null; carrier = null;
        for (int i = 0; i < Mission!.Sectors.Count; i++)
        {
            var sector = Mission.Sectors[i];
            if (type == IgcObjectType.Ship)
                for (int j = 0; j < sector.Models.Count; j++)
                    if (sector.Models[j] is Ship ship && ship.Id == id)
                    {
                        bool enter = HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier) && ship.HullType.HasCapability(DataHullTypeIGC.HabmCarrier);
                        carrier = enter ? ship : null;
                        target = ShipTarget(ship, sector.Id, enter ? G.Objective.Enter : G.Objective.Goto);
                        return true;
                    }
            if (type == IgcObjectType.Station)
                for (int j = 0; j < sector.StationRuntimes.Count; j++)
                    if (sector.StationRuntimes[j].StationId == id)
                    {
                        station = sector.StationRuntimes[j];
                        short need = HullType.HasCapability(DataHullTypeIGC.HabmFighter) ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand;
                        var objective = NativeFriendlySide(station.SideId) && station.Type.HasCapability(need) ? G.Objective.Enter : G.Objective.Goto;
                        target = StationTarget(station.Data, objective, out station);
                        return true;
                    }
            if (type == IgcObjectType.Asteroid)
                for (int j = 0; j < sector.Mineable.Count; j++)
                {
                    var rock = sector.Mineable[j];
                    if (rock.Data.AsteroidId != id) continue;
                    var p = rock.Data.Position;
                    target = new(NativeKey(G.Kind.Asteroid, id), sector.Id, G.Kind.Asteroid, G.Objective.Goto,
                        new(p[0], p[1], p[2]), rock.Radius, Present: !rock.Dead && NativeCanSee(rock, null,
                            new(p[0], p[1], p[2]), rock.Radius, rock.Data.Signature, sector.Id));
                    return true;
                }
            if (type == IgcObjectType.Warp)
                for (int j = 0; j < sector.Warps.Count; j++)
                {
                    var warp = sector.Warps[j];
                    if (warp.Warp.WarpId != id) continue;
                    target = new(NativeKey(G.Kind.Warp, id), sector.Id, G.Kind.Warp, G.Objective.Enter,
                        warp.Position, warp.Radius, WarpDestination: warp.Destination?.Id ?? -1,
                        Present: NativeCanSee(warp, null, warp.Position, warp.Radius, warp.Warp.Signature, sector.Id));
                    return true;
                }
        }
        target = new(NativeKey(G.Kind.Point, id), order.ClusterId, G.Kind.Point, G.Objective.Goto, order.Position, Present: false);
        return true;
    }
    private G.Target ShipTarget(Ship ship, short cluster, G.Objective objective) =>
        new(NativeKey(G.Kind.Ship, ship.Id), cluster, G.Kind.Ship, objective,
            ship.Position, .5f * ship.HullType.Length, ship.Velocity, Present: !ship.Dead && ship.StationId is null && NativeCanSee(ship, ship.SideId, ship.Position, .5f * ship.HullType.Length, ship.EffectiveSignature, cluster));
    private G.Target StationTarget(DataStationIGC data, G.Objective objective, out Station? station)
    {
        station = null;
        for (int i = 0; i < Mission!.Sectors.Count; i++)
        {
            var s = Mission.Sectors[i];
            if (s.Id != data.ClusterId) continue;
            for (int j = 0; j < s.StationRuntimes.Count; j++)
                if (s.StationRuntimes[j].StationId == data.StationId) { station = s.StationRuntimes[j]; break; }
            break;
        }
        return new(NativeKey(G.Kind.Station, data.StationId), data.ClusterId, G.Kind.Station, objective,
            new(data.Position[0], data.Position[1], data.Position[2]), station?.Radius ?? 0, Present: station is { Dead: false } && NativeCanSee(station, station.SideId, station.Center, station.Radius, station.Type.Signature, data.ClusterId));
    }
    private int NativeBays(Station? station, Ship? carrier, Span<DockApproach.DockBay> bays)
    {
        if (Sector!.StationLandingBays is { } cache && station is not null)
        {
            var geometry = cache.Bay(station.Type);
            if (geometry is null) return 0;
            int count = HullType.HasCapability(DataHullTypeIGC.HabmFighter) ? geometry.LandSlots.Count : geometry.CapLandSlots;
            count = Math.Min(count, bays.Length);
            for (int i = 0; i < count; i++)
            {
                var p = geometry.LandSlots[i].Planes[0];
                bays[i] = new(p.Position * station.Orientation, p.Direction.Normalize() * station.Orientation);
            }
            return count;
        }
        if (carrier is not null && Sector.LandingBays is { } hullCache)
        {
            var geometry = hullCache.Bay(carrier.HullType);
            if (geometry is null) return 0;
            int count = Math.Min(geometry.LandSlots.Count, bays.Length);
            for (int i = 0; i < count; i++)
            {
                var p = geometry.LandSlots[i].Planes[0];
                bays[i] = new(p.Position * carrier.Orientation, p.Direction.Normalize() * carrier.Orientation);
            }
            return count;
        }
        return 0;
    }

    // Igc/igc.h:4909-4913 + shipIGC.h:380-389: a hidden/dead/docked
    // target has no navigable cluster. Own-side, side-seen, or personal scanner.
    private bool NativeCanSee(ISideVisible body, short? owner, Vector3 position, float radius, float signature, short cluster)
    {
        if (SideId is not { } side) return true; // retained synthetic sideless mission support
        if (owner == side || body.SeenBySide(side)) return true;
        if (ParentShip is not null || Sector?.Id != cluster) return false;
        if (body is Ship { Flag: not null }) return true;
        // Native personal scanner, shipIGC.h:341-376: target signature GA,
        // fourfold rear displacement, flag carriers, then asteroid LOS.
        for (int i = 0; owner is not null && i < Mission!.Sides.Count; i++)
            if (Mission.Sides[i].Id == owner)
            {
                float scale = Mission.Sides[i].GlobalAttributes.Get(GlobalAttributeSet.Signature);
                if (scale > 0) signature /= scale;
                break;
            }
        float range = VisibilityMath.DetectionRadius(.5f * HullType.Length, EffectiveScannerRange(), radius, signature);
        Vector3 displacement = Orientation.TimesInverse(Position - position);
        if (displacement.Z < 0) displacement = displacement with { Z = displacement.Z * 4 };
        return displacement.LengthSquared() <= range * range &&
            VisibilityMath.LineOfSightExist(Position, new(position, radius), _nativeSightRocks!);
    }
    private void FillNativeSightRocks(Sector sector)
    {
        _nativeSightRocks ??= new(sector.Mineable.Count);
        _nativeSightRocks.Clear();
        for (int i = 0; i < sector.Mineable.Count; i++)
        {
            var rock = sector.Mineable[i];
            if (!rock.Dead) _nativeSightRocks.Add(new(new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]), rock.Radius));
        }
    }

    private short FindNativeWarp(short destination)
    {
        var mission = Mission!;
        int n = mission.Sectors.Count, e = mission.Warps.Count;
        _nativeEdges ??= new G.Edge[e]; _nativeClusters ??= new G.Cluster[n];
        _nativePaths ??= new G.Path[e + 1]; _nativeExplored ??= new short[n + 1];
        _nativeTerritory ??= new(n);
        for (int i = 0; i < n; i++)
        {
            var s = mission.Sectors[i];
            _nativeClusters[i] = new(s.Id, SideId is { } side && s.IsFriendlyCluster(side, _nativeTerritory));
        }
        int used = 0;
        for (int i = 0; i < e; i++)
        {
            var w = mission.Warps[i];
            if (w.Destination is null) continue;
            _nativeEdges[used++] = new(w.Warp.ClusterId, w.Destination.Id, w.Warp.WarpId, w.Position,
                NativeCanSee(w, null, w.Position, w.Radius, w.Warp.Signature, w.Warp.ClusterId));
                // CanSee includes our own scanner before the periodic side sweep (shipIGC.h:380-389).
        }
        bool coward = PilotType < PilotTypeCarrier;
        short warp = G.FindPath(Sector!.Id, destination, Position, coward, _nativeEdges.AsSpan(0, used), _nativeClusters, _nativePaths, _nativeExplored);
        if (coward && warp < 0)
            warp = G.FindPath(Sector.Id, destination, Position, false, _nativeEdges.AsSpan(0, used), _nativeClusters, _nativePaths, _nativeExplored);
        return warp;
    }

    /// <summary>Ignore common.cpp:1642-1690. Index loops avoid interface
    /// enumerator boxing. Lower-ID allied drones have right of way unless
    /// mining/ripcording; enemy ships, allied mines and rescue pairs are ignored.</summary>
    private void FillNativeObstacles(Sector sector, List<G.Obstacle> obstacles)
    {
        obstacles.Clear();
        for (int i = 0; i < sector.Models.Count; i++)
        {
            if (sector.Models[i] is not Ship { Dead: false, StationId: null, ParentShip: null } other || other == this) continue;
            bool friendly = NativeFriendlySide(other.SideId);
            bool ignore = !friendly && PilotType < PilotTypePlayer || other.Id < Id && PilotType < PilotTypePlayer && other.PilotType < PilotTypePlayer &&
                !other.IsMining && other.RipcordModel is null;
            short me = HullType.HabmCapabilities, him = other.HullType.HabmCapabilities;
            if (friendly && ((me & DataHullTypeIGC.HabmLifepod) != 0 && (him & DataHullTypeIGC.HabmRescue) != 0 ||
                (me & DataHullTypeIGC.HabmRescue) != 0 && (him & DataHullTypeIGC.HabmLifepod) != 0 ||
                (me & DataHullTypeIGC.HabmCarrier) != 0 && (him & DataHullTypeIGC.HabmLandOnCarrier) != 0)) ignore = true;
            obstacles.Add(new(NativeKey(G.Kind.Ship, other.Id), G.Kind.Ship, other.Position, other.Velocity, .5f * other.HullType.Length, ignore));
        }
        for (int i = 0; i < sector.Mineable.Count; i++)
        {
            var rock = sector.Mineable[i];
            if (rock.Dead) continue;
            var p = rock.Data.Position;
            obstacles.Add(new(NativeKey(G.Kind.Asteroid, rock.Data.AsteroidId), G.Kind.Asteroid, new(p[0], p[1], p[2]), default, rock.Radius));
        }
        for (int i = 0; i < sector.StationRuntimes.Count; i++)
        {
            var station = sector.StationRuntimes[i];
            if (!station.Dead) obstacles.Add(new(NativeKey(G.Kind.Station, station.StationId), G.Kind.Station, station.Center, default, station.Radius));
        }
        for (int i = 0; i < sector.Warps.Count; i++)
        {
            var warp = sector.Warps[i];
            obstacles.Add(new(NativeKey(G.Kind.Warp, warp.Warp.WarpId), G.Kind.Warp, warp.Position, default, warp.Radius));
        }
        for (int i = 0; i < sector.Models.Count; i++)
        {
            var model = sector.Models[i];
            if (model is Mine { Fraction: > 0 } mine) obstacles.Add(new(NativeKey(G.MineKind, mine.Id), G.MineKind, mine.Position, default, mine.MineType.Radius, NativeFriendlySide(mine.SideId)));
            else if (model is Probe { Dead: false } probe) obstacles.Add(new(NativeKey(G.Kind.Probe, probe.Id), G.Kind.Probe, probe.Position, default, probe.ProbeType.Radius));
        }
    }
}
