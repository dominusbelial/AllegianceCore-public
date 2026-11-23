using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Browser orchestration of the unchanged desktop native particle pools.
/// View changes clear local particles; hits are drained once, even without artwork.
/// Native: thinggeo.cpp:699-755,758-917,1243-1345; projectileIGC.cpp:193,224.</summary>
public sealed class BrowserCombatEffects
{
    // Desktop ChaseCamera's default near plane. The browser's former 10-unit
    // plane clipped the entire small own-pod hull in its radius-scaled chase view.
    public const float FlightNearPlane = .5f;
    public MissileTrailParticles Trails { get; } = new();
    public DamageDebrisParticles Debris { get; } = new();
    public HullHitSparkParticles HullHits { get; } = new();
    public ShieldFlareParticles Flares { get; } = new();
    public List<TrailMissile> Missiles { get; } = new(64);
    public List<TrailParticleView> TrailViews { get; } = new(768);
    public List<DamageDebrisView> DebrisViews { get; } = new(512);
    public List<HullHitSparkView> HitViews { get; } = new(128);
    public List<HullHitFlashView> Flashes { get; } = new(128);
    public List<ShieldFlareView> FlareViews { get; } = new(64);
    private readonly ITrailRandom _random = new RandomTrailSource(1079);
    // Port-only delivery/performance policy: keep the newest smoke at pressure.
    private readonly ParticleDensityBudget _fullTrails = new(384), _fullDebris = new(128), _reduced = new(128);
    private readonly record struct ShipSnapshot(SimVec Position, SimVec Velocity, bool Fighter, long Frame, Orientation? Orientation);
    private readonly Dictionary<short, ShipSnapshot> _ships = new();
    private readonly List<short> _staleShips = new();
    private readonly List<ExplosionEntity> _expiredEjects = new();
    public Dictionary<ExplosionEntity, short> EjectExplosions { get; } = new();
    private long _frame, _killSequence;
    private long _mission = -1, _view = -1;
    private short _sector = -1;
    private double _clock;

    public void Reset()
    {
        Trails.Reset(); Debris.Reset(); HullHits.Reset(); Flares.Reset();
        Missiles.Clear(); TrailViews.Clear(); DebrisViews.Clear(); HitViews.Clear(); Flashes.Clear(); FlareViews.Clear();
        _ships.Clear(); _staleShips.Clear(); EjectExplosions.Clear(); _expiredEjects.Clear();
        _frame = _killSequence = 0;
        _mission = _view = -1; _sector = -1; _clock = 0;
    }

    public void BeginFrame(ClientWorld world, short sector, float dt, long? nowLocalMs = null)
    {
        if (_mission != world.MissionGeneration || _view != world.SectorGeneration || _sector != sector)
        {
            Reset(); _mission = world.MissionGeneration; _view = world.SectorGeneration; _sector = sector;
            _killSequence = world.Events.KillSequence;
        }
        _frame++;
        long now = nowLocalMs ?? Environment.TickCount64;
        // Native KILL_SHIP adds a hull blast even when bDeathCredit is false
        // (clintlib/appmsg.cpp:2850-2857), before EJECT replaces the hull. The
        // port server emits 207 only for true deaths. Preserve the previous
        // visible hull pose/velocity/type so the browser can make this native
        // ejection cue without any server or desktop rendering change.
        if (_killSequence != world.Events.KillSequence)
        {
            foreach (var kill in world.Events.KillsAfter(_killSequence))
                if (!kill.BDeathCredit && _ships.TryGetValue(kill.ShipId, out var ship) && ship.Frame == _frame - 1
                    && world.Ships.TryGetValue(kill.ShipId, out var present) && present.CanDrawInSector(sector))
                    EjectExplosions.Add(new ExplosionEntity { ClusterId = sector,
                        Type = ship.Fighter ? ExplosionType.SmallShip : ExplosionType.BigShip,
                        Position = ship.Position, Velocity = ship.Velocity, ReceivedLocalMs = now }, kill.ShipId);
            _killSequence = world.Events.KillSequence;
        }
        _staleShips.Clear();
        foreach (var entry in _ships) if (entry.Value.Frame < _frame - 1) _staleShips.Add(entry.Key);
        foreach (short id in _staleShips) _ships.Remove(id);
        _expiredEjects.Clear();
        foreach (var blast in EjectExplosions.Keys)
            if (now - blast.ReceivedLocalMs >= ExplosionPresets.For(blast.Type).TotalDurationSeconds * 1000)
                _expiredEjects.Add(blast);
        foreach (var blast in _expiredEjects) EjectExplosions.Remove(blast);
        _clock += Math.Max(0, dt);
        Missiles.Clear();
        Debris.Update(dt); HullHits.Update(dt); Flares.Update(dt);
    }

    public void GatherMissile(MissileEntity missile)
    {
        if (missile.ClusterId == _sector)
            Missiles.Add(new(missile.MissileId, missile.Position, missile.Forward, _clock));
    }

    public void ObserveShip(short id, SimVec position, SimVec velocity, DataHullTypeIGC? hull, Orientation? orientation = null)
    {
        if (hull is { } type) _ships[id] = new(position, velocity, type.HasCapability(DataHullTypeIGC.HabmFighter), _frame, orientation);
    }

    public void AppendEjectExplosions(ClientWorld world, List<ExplosionEntity> destination)
    {
        foreach (var blast in EjectExplosions.Keys)
        {
            // A future host may supply the native eject cue itself. An exact
            // overlapping authoritative blast replaces this inferred one.
            bool authoritative = false;
            foreach (var server in world.Explosions)
                if (server.ClusterId == blast.ClusterId && server.Type == blast.Type
                    && Math.Abs(server.ReceivedLocalMs - blast.ReceivedLocalMs) < 1000
                    && (server.Position - blast.Position).LengthSquared() < 1)
                { authoritative = true; break; }
            if (!authoritative) destination.Add(blast);
        }
    }

    public static void GatherExplosions(ClientWorld world, short sector, long now, List<ExplosionEntity> destination)
    {
        world.PruneExpiredExplosions(now);
        destination.Clear();
        foreach (var blast in world.Explosions)
            if (blast.ClusterId == sector) destination.Add(blast);
    }

    /// <summary>Desktop Game1.Rendering.cs:1485-1496; native damage vents.
    /// The caller supplies only the drawn scanner-visible roster and its render pose.</summary>
    public void GatherDamage(ShipEntity ship, SimVec position, SimVec velocity, Orientation orientation,
        float radius, float dt, float cameraDistance, bool full)
    {
        if (!full) return; // P1069 reduced load sheds continuous smoke first.
        int vents = DamageDebrisTracker.DamageFrameCount(ship.Latest?.HullFraction ?? 1);
        if (vents == 0) return;
        Debris.Emit(position, velocity, orientation, radius, ship.ShipId, vents, dt, _random,
            ShipEffectRules.DamageSmokeScale(ship.IsMine, true, radius * 2, cameraDistance));
    }

    /// <summary>Native RenderTargetGeo applies m_scale = radius/m_radiusMesh
    /// before rendering flares (effect/thinggeo.cpp:1185-1187,1482-1495).
    /// The shared cap renderer consumes WORLD-space geometry instead. Normalize
    /// the authored mesh ellipse and mesh radius into that space exactly once,
    /// preserving the native per-axis shape without an oversized offscreen cap.</summary>
    public static (SimVec Ellipse, float MeshRadius) ShieldShape(SimVec authoredExtents, float authoredRadius, float radius)
    {
        if (authoredRadius <= 0 || authoredExtents.X <= 0 || authoredExtents.Y <= 0 || authoredExtents.Z <= 0)
            return (new(radius, radius, radius), radius);
        return (authoredExtents * (radius / authoredRadius), radius);
    }

    public void IngestHit(HullHitEntity hit, ClientWorld world, Func<short, DataHullTypeIGC?> hull,
        Func<short, float, (SimVec Ellipse, float MeshRadius)> ellipse)
    {
        if (hit.ClusterId != _sector) return;
        float radius = hit.Radius > 0 ? hit.Radius : HullHitSparkParticles.ShipRadiusDefault;
        if (!hit.AbsorbedByShield) { HullHits.Emit(hit.Position, hit.Normal, radius, _random); return; }
        var center = hit.Position;
        var hitPoint = hit.Position;
        var orientation = Orientation.Identity;
        short hullId = -1;
        if (world.Ships.TryGetValue(hit.TargetId, out var target) && target.Latest is { } pose && !target.IsNotVisible)
        {
            center = pose.Position; orientation = pose.Orientation; hullId = target.HullId;
            if (hull(hullId) is { Length: > 0 } type) radius = type.Length * .5f;
            // Native ThingGeo renders the flare under the thing's world pose
            // (thinggeo.cpp:1482-1495). Browser meshes use predicted/interpolated
            // poses, so rebase the authoritative impact by the SAME offset.
            if (_ships.TryGetValue(hit.TargetId, out var rendered) && rendered.Frame == _frame
                && rendered.Orientation is { } renderOrientation)
            {
                center = rendered.Position; orientation = renderOrientation;
                hitPoint += center - pose.Position;
            }
        }
        var shape = ellipse(hullId, radius);
        Flares.Emit(hit.TargetId, center, orientation.Copy(), hitPoint, shape.Ellipse, radius, shape.MeshRadius);
    }

    public void FinishFrame(float dt, bool full)
    {
        Trails.Update(_clock, dt, Missiles, _random);
        // Split a bounded smoke budget between missile exhaust (always readable)
        // and damage smoke. Reduced load keeps event feedback and missile trails.
        CopyNewest(Trails.Enumerate(), TrailViews, full ? _fullTrails : _reduced, p => p.RemainingFrac);
        if (!full) { Debris.Reset(); DebrisViews.Clear(); }
        else CopyNewest(Debris.Enumerate(), DebrisViews, _fullDebris, p => p.RemainingFrac);
        HitViews.Clear(); HitViews.AddRange(HullHits.Enumerate());
        Flashes.Clear(); Flashes.AddRange(HullHits.EnumerateFlashes());
        FlareViews.Clear(); FlareViews.AddRange(Flares.Enumerate());
    }

    /// <summary>ParticleDensityBudget evicts by age; mirror the eviction in the
    /// actual draw list (TryAdmit alone does not remove previously added puffs).</summary>
    public static void CopyNewest<T>(IEnumerable<T> source, List<T> destination,
        ParticleDensityBudget budget, Func<T, float> remaining)
    {
        destination.Clear(); budget.Reset();
        foreach (var item in source)
        {
            if (budget.Capacity == 0) break;
            float key = remaining(item);
            if (!budget.IsFull)
            {
                budget.TryAdmit(key);
                destination.Add(item);
                // A min heap keeps oldest at the root without scanning the
                // entire draw list for every over-budget puff in WASM.
                int child = destination.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (remaining(destination[parent]) <= key) break;
                    destination[child] = destination[parent]; child = parent;
                }
                destination[child] = item;
            }
            else if (key > remaining(destination[0]))
            {
                int parent = 0;
                while (parent * 2 + 1 < destination.Count)
                {
                    int child = parent * 2 + 1;
                    if (child + 1 < destination.Count && remaining(destination[child + 1]) < remaining(destination[child])) child++;
                    if (remaining(destination[child]) >= key) break;
                    destination[parent] = destination[child]; parent = child;
                }
                destination[parent] = item;
            }
        }
    }
}
