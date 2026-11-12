using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using SimModel = Allegiance.Core.Sim.Model;

namespace Allegiance.Core.Model;

public sealed partial class Sector
{
    internal bool DefersMovement => Mission?.NativeCollisionQueueEnabled == true;
    private double _nativeMotionTime;
    private Dictionary<SimModel, double>? _nativeMovementStarts;
    private HashSet<Ship>? _nativeStoppedPlots;

    private double NativeMovementStart(SimModel model) => _nativeMovementStarts is not null
        && _nativeMovementStarts.TryGetValue(model, out double start) ? start : model.LastUpdate;

    internal bool NativePlotStopped(Ship ship) => _nativeStoppedPlots?.Contains(ship) == true;

    private bool NativeRescuePending(Ship ship)
    {
        for (int i = 0; i < _pendingRescues.Count; i++)
            if (ReferenceEquals(_pendingRescues[i].Rescuee, ship)) return true;
        for (int i = 0; i < _pendingStationRescues.Count; i++)
            if (ReferenceEquals(_pendingStationRescues[i].Rescuee, ship)) return true;
        return false;
    }

    // SIM-0063: clusterIGC.cpp:255-343. All supported contact classes share
    // one time axis. A bounce invalidates detection, as RecalculateCollisions
    // does at clusterIGC.cpp:592-706; no fixed iteration cap drops contacts.
    private void ResolveNativeCollisionQueue(double now, float dt)
    {
        _shipRescueDone.Clear();
        var spent = new HashSet<IOrdnance>();
        var picked = new HashSet<Treasure>();
        var mines = new Dictionary<Ship, MineHit>();
        var versions = new Dictionary<object, int>();
        var handled = new HashSet<(object, object, int, int)>();
        var probes = new Separate.ProbeCache();
        LastShipShipEvents = LastShipShipResolved = LastShipShipDebounced = 0;
        while (true)
        {
            float remaining = MathF.Max(0, (float)(now - _nativeMotionTime));
            var detector = BuildNativeColliders();
            var queue = new CollisionQueue();
            foreach (var detected in DetectNativeContacts(detector, remaining))
            {
                // A newborn's sweep starts at creation. The common broadphase
                // conservatively includes its backwards extension; re-test that
                // pair over the actual overlapping lifetime before queuing it.
                float born = (float)Math.Max(0, Math.Max(
                    detected.A.Data is SimModel da ? NativeMovementStart(da) : _nativeMotionTime,
                    detected.B.Data is SimModel db ? NativeMovementStart(db) : _nativeMotionTime) - _nativeMotionTime);
                if (detected.TCollision >= born) { queue.Add(detected); continue; }
                if (born > remaining) continue;
                var a = detected.A; var b = detected.B;
                var ap = a.Position; var bp = b.Position;
                a.Position += a.Velocity * born; b.Position += b.Velocity * born;
                var retest = remaining > born ? NarrowPhase.TestPair(a, b, remaining - born)
                    : NarrowPhase.TestBoundaryPair(a, b);
                a.Position = ap; b.Position = bp;
                if (retest.Found) queue.Add(new(a, b, born + retest.TCollision, retest.HtsA, retest.HtsB));
            }
            AddNativeWarpContacts(detector, queue, remaining);
            queue.Sort();
            bool recalculate = false;
            foreach (var ev in queue.Ordered())
            {
                object a = ev.A.Data!, b = ev.B.Data!;
                if (!NativeContactLive(a) || !NativeContactLive(b)) continue;
                int av = versions.GetValueOrDefault(a), bv = versions.GetValueOrDefault(b);
                if (!handled.Add((a, b, av, bv))) continue;
                double contactTime = _nativeMotionTime + ev.TCollision;
                if (a is SimModel am) contactTime = Math.Max(contactTime, NativeMovementStart(am));
                if (b is SimModel bm) contactTime = Math.Max(contactTime, NativeMovementStart(bm));
                var ap = (a as SimModel)?.Position; var bp = (b as SimModel)?.Position;
                var va = (a as Ship)?.Velocity; var vb = (b as Ship)?.Velocity;
                DispatchNativeContact(contactTime, (float)(now - contactTime), ev, spent, picked, mines, probes);
                bool changedA = a is SimModel ma && NativeContactLive(a) && (ma.Position != ap || a is Ship sa && sa.Velocity != va);
                bool changedB = b is SimModel mb && NativeContactLive(b) && (mb.Position != bp || b is Ship sb && sb.Velocity != vb);
                if (!changedA && !changedB) continue;
                // Separate has already advanced the changed participants to the
                // contact. Move all others to that time, then rebuild the tail.
                AdvanceNativeModels(contactTime, changedA ? a : null, changedB ? b : null);
                if (changedA) versions[a] = av + 1;
                if (changedB) versions[b] = bv + 1;
                recalculate = true;
                break;
            }
            if (!recalculate) break;
        }
        // Native accumulates the worst mine contact, then applies children
        // before parents, before the final Move (clusterIGC.cpp:322-343).
        var snapshot = _models.ToArray();
        for (int i = snapshot.Length - 1; i >= 0; i--)
            if (snapshot[i] is Ship ship && _modelMembership.ContainsKey(ship) && mines.TryGetValue(ship, out var hit))
            {
                if (hit.Time > ship.TimeLastMineExplosion + .5f)
                {
                    ship.TimeLastMineExplosion = hit.Time;
                    ship.Mission?.Site.MineHitEvent(hit.Mine, hit.P1, hit.P2);
                }
                ship.PendingHit = new(HitKind.Mine, hit.Mine.MineType.LauncherName, hit.Type, hit.Time, hit.Mine.MineType.ExpendableTypeId);
                ship.ReceiveDamage(hit.Type, hit.Damage, hit.Time, hit.P1, hit.P2, hit.Launcher);
            }
    }

    private static IReadOnlyList<CollisionEvent> DetectNativeContacts(CollisionDetector detector, float remaining)
    {
        if (remaining > 0) return detector.Detect(remaining, NativeContactPair);
        // Recalculation at the tick end still processes simultaneous contacts
        // (clusterIGC.cpp:627 uses <=). Never invoke the positive-step API at 0.
        var events = new List<CollisionEvent>();
        foreach (var collider in detector.Colliders) collider.SetStep(0, 0);
        foreach (var (i, j) in BroadPhase.OverlappingPairs(detector.Colliders, NativeContactPair))
        {
            var a = detector.Colliders[i]; var b = detector.Colliders[j];
            var hit = NarrowPhase.TestBoundaryPair(a, b);
            if (hit.Found) events.Add(new(a, b, hit.TCollision, hit.HtsA, hit.HtsB));
        }
        return events;
    }

    private CollisionDetector BuildNativeColliders()
    {
        var detector = new CollisionDetector();
        foreach (var model in _models)
        {
            if (!NativeContactLive(model)) continue;
            var position = model.Position - model.CollisionVelocity * (float)Math.Max(0, NativeMovementStart(model) - _nativeMotionTime);
            Collider? collider = model switch
            {
                Ship s => new(s, position, s.Velocity, s.Orientation, EnsureShipHull(s), s.HullType.Length * .5f, false),
                IOrdnance o => new(o, position, o.Velocity, Orientation.Identity, null, o.Radius, false),
                Probe p => new(p, position, Vector3.Zero, Orientation.Identity, null, p.ProbeType.Radius, true),
                Mine m => new(m, position, Vector3.Zero, Orientation.Identity, null, m.MineType.MineRadius, true),
                Treasure t => new(t, position, t.CollisionVelocity, Orientation.Identity, null, Treasure.PickupRadius, false),
                _ => null
            };
            if (collider is not null) detector.Add(collider);
        }
        foreach (var rock in _mineable)
            if (!rock.Dead && rock.Data.Position is { Length: 3 } p)
                detector.Add(new(rock, new(p[0], p[1], p[2]), Vector3.Zero, Orientation.Identity,
                    CollisionHulls?.Resolve(rock.Data.ModelName), rock.Radius, true));
        foreach (var station in _stationRuntimes)
            if (!station.Dead)
                detector.Add(new(station, station.Center, Vector3.Zero, Orientation.Identity,
                    CollisionHulls?.Resolve(station.Type.ModelName), station.Radius, true));
        return detector;
    }

    private bool NativeContactLive(object value) => value switch
    {
        Ship s => !s.Dead && s.StationId is null && s.ParentShip is null && ReferenceEquals(s.Sector, this) && !NativeRescuePending(s) && _modelMembership.ContainsKey(s),
        Missile m => !m.Dead && _modelMembership.ContainsKey(m),
        Probe p => !p.Dead && _modelMembership.ContainsKey(p),
        SimModel m => _modelMembership.ContainsKey(m),
        Asteroid a => !a.Dead && _mineable.Contains(a),
        Station s => !s.Dead && _stationRuntimes.Contains(s),
        ResolvedWarp w => _warps.Contains(w),
        _ => false
    };

    private static bool NativeContactPair(Collider a, Collider b) => CombatPair(a, b)
        || a.Data is Ship && b.Data is Ship or Asteroid or Station or Probe or Mine or Treasure
        || b.Data is Ship && a.Data is Asteroid or Station or Probe or Mine or Treasure;

    private void AddNativeWarpContacts(CollisionDetector detector, CollisionQueue queue, float dt)
    {
        int ordinal = detector.Colliders.Count;
        foreach (var warp in _warps)
        {
            if (warp.Warp.Position is not { Length: 3 } p) continue;
            var geometry = AlephConeHitTest.Geometry.From(warp.Warp);
            var wc = new Collider(warp, new(p[0], p[1], p[2]), Vector3.Zero, Orientation.Identity, null, warp.Warp.Radius, true)
                { Ordinal = ordinal++ };
            foreach (var collider in detector.Colliders)
            {
                if (collider.Data is not (Ship or Missile)) continue;
                if (collider.Data is Ship && warp.Destination is null) continue;
                float born = (float)Math.Max(0, NativeMovementStart((SimModel)collider.Data) - _nativeMotionTime);
                if (born > dt) continue;
                if (AlephConeHitTest.SweptFirstContact(in geometry, collider.Position + collider.Velocity * born,
                    collider.Position + collider.Velocity * dt, collider.EffectiveRadius, out float t))
                    queue.Add(new(collider, wc, born + t * (dt - born), collider.Shape, HitShape.Sphere));
            }
        }
    }

    private void DispatchNativeContact(double time, float remaining, CollisionEvent ev,
        HashSet<IOrdnance> spent, HashSet<Treasure> picked, Dictionary<Ship, MineHit> mines, Separate.ProbeCache probes)
    {
        if (CombatPair(ev.A, ev.B)) { ResolveCombatCollision(time, ev, spent); return; }
        if (ev.B.Data is ResolvedWarp warp)
        {
            if (ev.A.Data is Missile missile && !missile.IsDud)
                ResolveAlephMissileContact(missile, warp, ev.A.Position + missile.Velocity * ev.TCollision, time);
            else if (ev.A.Data is Ship ship && ship.WarpReady && (ship.IsNpc || time - ship.LastWarpTime >= WarpCooldownSeconds)
                && !(ship.LayerBuildOrder is { } lb && ship.Sector?.Id == lb.ClusterId))
            {
                // Site uses the collision position to compute the exit transform.
                AtNativeContact(ship, ev, () => ship.Mission!.Site.HitWarpEvent(ship, warp));
            }
            return;
        }
        var s = ev.A.Data as Ship ?? ev.B.Data as Ship;
        if (s is null) return;
        var other = ReferenceEquals(s, ev.A.Data) ? ev.B.Data : ev.A.Data;
        switch (other)
        {
            case Ship second:
                LastShipShipEvents++;
                if (!TryNativeCarrierContact(s, second, ev)) ResolveShipShipCollision(time, ev, s, second, probes);
                break;
            case Asteroid rock: ResolveShipAsteroidCollision(time, ev, s, rock); break;
            case Station station:
                AtNativeContact(s, ev, () => { ResolveDockCollisionsCore(time, s, station); ResolveStationRescueEventsCore(s, station); });
                if (NativeContactLive(s)) ResolveShipStationCollision(time, ev, s, station);
                break;
            case Probe probe:
                if (s.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) && s.SideId is { } side
                    && ((probe.ProbeType.EabmCapabilities & DataProbeTypeIGC.EabmRescueAny) != 0
                        || (probe.ProbeType.EabmCapabilities & DataProbeTypeIGC.EabmRescue) != 0
                        && probe.SideId is { } ps && IsAlliedSide(side, ps)))
                    AtNativeContact(s, ev, () => { if (Mission!.Site.RescueShipEvent(s, null)) _pendingStationRescues.Add((s, null)); });
                break;
            case Treasure treasure:
                if (!picked.Contains(treasure) && s.PilotType >= Ship.PilotTypePlayer
                    && s.HullType.HasCapability(DataHullTypeIGC.HabmRescue)
                    && treasure.Code is TreasureCode.Cash or TreasureCode.Part or TreasureCode.Flag or TreasureCode.Powerup)
                    AtNativeContact(s, ev, () => {
                        if (Mission!.Site.HitTreasureEvent(time, s, treasure))
                        { picked.Add(treasure); _pendingTreasurePickups.Add((s, treasure)); }
                    });
                break;
            case Mine mine: AccumulateNativeMineContact(time, remaining, ev, s, mine, mines); break;
        }
    }

    // Reuse the production docking/site handlers at the swept contact, retaining
    // their position only when the callback actually relocates the ship.
    private static void AtNativeContact(Ship ship, CollisionEvent ev, Action action)
    {
        var before = ship.Position;
        var sector = ship.Sector;
        var station = ship.StationId;
        var collider = ReferenceEquals(ev.A.Data, ship) ? ev.A : ev.B;
        var contact = collider.Position + collider.Velocity * ev.TCollision;
        ship.Position = contact;
        action();
        if (ship.Position == contact && ship.StationId == station && ReferenceEquals(ship.Sector, sector)) ship.Position = before;
    }

    private bool TryNativeCarrierContact(Ship a, Ship b, CollisionEvent ev)
    {
        if (a.SideId != b.SideId || LandingBays is null) return false;
        Ship carrier, lander;
        if (a.HullType.HasCapability(DataHullTypeIGC.HabmCarrier) && b.HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier))
        { carrier = a; lander = b; }
        else if (b.HullType.HasCapability(DataHullTypeIGC.HabmCarrier) && a.HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier))
        { carrier = b; lander = a; }
        else return false;
        var bay = LandingBays.Bay(carrier.HullType);
        return bay is not null && carrier.InGarage(lander, ev.TCollision, bay)
            && Mission!.Site.LandOnCarrierEvent(carrier, lander, ev.TCollision);
    }

    private void AccumulateNativeMineContact(double time, float remaining, CollisionEvent ev, Ship ship, Mine mine, Dictionary<Ship, MineHit> worst)
    {
        // mineigc.h:75-142: full dwell from entry; accumulate, do not damage yet.
        if (time < mine.DeployTime + Mine.ArmTimeSeconds || ReferenceEquals(ship, mine.Launcher)
            || mine.SideId is { } side && ship.SideId is { } shipSide && Mission!.AreSidesFriendly(side, shipSide)) return;
        float speed2 = ship.Velocity.LengthSquared();
        if (speed2 <= 1) return;
        var collider = ReferenceEquals(ev.A.Data, ship) ? ev.A : ev.B;
        var p = collider.Position + ship.Velocity * ev.TCollision;
        float rShip = ship.HullType.Length * .5f, radius = mine.MineType.MineRadius + rShip;
        var dp = mine.Position - p;
        double halfB = dp * ship.Velocity;
        double discriminant = halfB * halfB - speed2 * (dp.LengthSquared() - radius * radius);
        if (discriminant < 0) return;
        float dwell = Math.Clamp((float)((halfB + Math.Sqrt(discriminant)) / speed2), 0, remaining);
        const float damageMultiplier = 1f / (5f * 5f * 100f);
        float amount = dwell * speed2 * mine.MineType.Power * rShip * rShip * damageMultiplier / (2f * mine.MineType.MineRadius);
        float damage = mine.TakeAttritionDamage(amount);
        if (damage > 0 && (!worst.TryGetValue(ship, out var previous) || damage > previous.Damage))
        {
            var p2 = p + ship.Velocity * ((10 + rShip) / MathF.Sqrt(speed2)) + DeterministicRandomPosition(10, time, ship.Id, mine.Id);
            worst[ship] = new(mine, mine.MineType.DamageType, damage, time, p, p2, mine.Launcher);
        }
    }

    private void AdvanceNativeModels(double time, object? advancedA = null, object? advancedB = null)
    {
        foreach (var model in _models)
        {
            if (ReferenceEquals(model, advancedA) || ReferenceEquals(model, advancedB)) continue;
            if (model is Ship s && (s.Dead || s.StationId is not null || s.ParentShip is not null || NativeRescuePending(s))) continue;
            float elapsed = (float)Math.Max(0, time - Math.Max(_nativeMotionTime, NativeMovementStart(model)));
            model.Position += model.CollisionVelocity * elapsed;
        }
        _nativeMotionTime = time;
    }

    private void CompleteNativeMovement(double now) => AdvanceNativeModels(now);
}
