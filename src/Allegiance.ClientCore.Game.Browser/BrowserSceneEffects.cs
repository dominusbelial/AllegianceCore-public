using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Browser scene state, bounded/reused across frames. Draw eligibility is
/// supplied by the battle scene; this class never discovers hidden ships.</summary>
public sealed class BrowserSceneEffects
{
    public WarpTransitFlash Flash { get; } = new();
    public WarpTransitParticles Particles { get; } = new();
    public WarpPulseController Pulses { get; } = new();
    public List<NavStrobeView> Strobes { get; } = new(128);
    public List<WarpSparkView> Sparks { get; } = new(512);
    public List<DataWarpIGC> Warps { get; } = new(16);
    public List<GlimpseTarget> Glimpses { get; } = new(16);
    private readonly List<AlephTransitSignal> _signals = new(16);
    private readonly Dictionary<short, short> _lastSector = new(64);
    private readonly Dictionary<short, (SimVec Position, long Frame)> _lastPosition = new(64);
    private readonly TeleportEmitter[] _emitters = new TeleportEmitter[64];
    private readonly ITrailRandom _random = new RandomTrailSource(1066);
    private int _emitterCount, _warpCount = -1, _clusterCount = -1;
    private long _generation = -1, _viewGeneration = -1;
    private short? _sector, _own;
    private double _accumulator;
    private double _now;
    private long _frame;
    private (SimVec Position, SimVec Velocity, SimVec Forward, SimVec Right, float Radius)? _burst;
    public Func<short, float> PulseStrength { get; }

    public BrowserSceneEffects() => PulseStrength = id => Pulses.Strength(id, _now);

    public void Reset()
    {
        _generation = _viewGeneration = -1; _sector = _own = null; _warpCount = _clusterCount = -1;
        Flash.Reset(); Particles.Reset(); Pulses.Reset(); _lastSector.Clear(); _lastPosition.Clear();
        Warps.Clear(); Glimpses.Clear(); Strobes.Clear(); Sparks.Clear(); _signals.Clear();
        _burst = null; _accumulator = 0; _emitterCount = 0; _frame = 0;
    }

    public void BeginFrame(ClientWorld world, ShipEntity own, float dt, double now, WarpRenderer? warpRenderer = null)
    {
        if (_generation != world.MissionGeneration || _own != own.ShipId)
        {
            Reset(); _generation = world.MissionGeneration; _own = own.ShipId;
        }
        _now = now; _frame++;
        if (_viewGeneration != world.SectorGeneration || _sector != own.SectorId)
        {
            _viewGeneration = world.SectorGeneration; _sector = own.SectorId; _warpCount = -1;
            // Same per-view reset as desktop GatherRenderPoses: an old sighting
            // in another view is not evidence that this ship just arrived here.
            Particles.Reset(); Pulses.Reset(); _lastPosition.Clear(); _lastSector.Clear();
            _accumulator = 0; _burst = null; warpRenderer?.ResetTransits();
        }
        if (_warpCount != world.Warps.Count || _clusterCount != world.Clusters.Count)
        {
            Warps.Clear();
            foreach (var warp in world.Warps.Values)
                if (warp.ClusterId == own.SectorId) Warps.Add(warp);
            _warpCount = world.Warps.Count; _clusterCount = world.Clusters.Count;
        }
        Strobes.Clear(); _emitterCount = 0;
        Particles.Update(dt);
        if (_burst is { } burst)
        {
            NativeScene.EmitTransitBurst(Particles, burst.Position, burst.Velocity,
                burst.Forward, burst.Right, burst.Radius, _random);
            _burst = null;
        }
        world.DrainAlephTransits(_signals);
        for (int i = 0; i < _signals.Count; i++)
        {
            var signal = _signals[i];
            if (world.Warps.TryGetValue(signal.WarpId, out var warp) && warp.ClusterId == own.SectorId)
            { Pulses.Trigger(signal.WarpId, now); warpRenderer?.TriggerTransit(signal.WarpId); }
        }
        Pulses.Update(now);
    }

    /// <summary>Called only after the hull passes sector/scanner and camera culling.
    /// Native teleport intent: effect/thinggeo.cpp:503-528,619-696.</summary>
    public void GatherShip(ClientWorld world, ShipEntity ship, SimVec position, SimVec velocity,
        Orientation orientation, DataHullTypeIGC? hull, float dt, long localMs, WarpRenderer? warpRenderer = null)
    {
        float radius = (hull?.Length ?? 2) * .5f;
        NativeScene.AppendShipStrobes(position, orientation, radius, true, (float)_now, Strobes);
        if (ship.StatusSectorKnown)
        {
            if (_lastSector.TryGetValue(ship.ShipId, out short previous) && previous != ship.SectorId)
                for (int i = 0; i < Warps.Count; i++)
                {
                    var warp = Warps[i];
                    if (world.Warps.TryGetValue(warp.DestinationId, out var partner) && partner.ClusterId == previous)
                    { Pulses.Trigger(warp.WarpId, _now); warpRenderer?.TriggerTransit(warp.WarpId); }
                }
            _lastSector[ship.ShipId] = ship.SectorId;
        }
        // A culled ship has no previous render frame here. Re-entering the
        // camera uses wire velocity, never a many-frame displacement / one dt.
        SimVec frameVelocity = dt > 1e-5f && _lastPosition.TryGetValue(ship.ShipId, out var last) && last.Frame == _frame - 1
            ? (position - last.Position) / dt : velocity;
        _lastPosition[ship.ShipId] = (position, _frame);
        float distance = float.MaxValue;
        SimVec nearest = default;
        for (int i = 0; i < Warps.Count; i++)
        {
            var warp = Warps[i];
            var p = new SimVec(warp.Position[0], warp.Position[1], warp.Position[2]);
            float dsq = (p - position).LengthSquared();
            if (dsq < distance) { distance = dsq; nearest = p; }
        }
        float eta = distance == float.MaxValue ? -1 : RipcordLeadup.AlephEta(position, velocity, nearest);
        float ripcord = -1;
        long? start = ship.ShipId == _own ? world.RipcordActiveSinceLocalMs
            : world.TryGetRemoteRipcord(ship.ShipId, out long began) ? began : null;
        if (hull is { } h && start.HasValue)
        {
            float ga = world.GlobalAttributesFor(ship.SideId).Get(GlobalAttributeSet.RipcordTime);
            ripcord = RipcordLeadup.TimeUntilTeleport(start, localMs, ga > 0 ? h.RipcordSpeed / ga : h.RipcordSpeed, world.TimeScale);
        }
        float until = RipcordLeadup.SelectTeleportTime(ripcord, eta);
        if (until >= 0 && _emitterCount < _emitters.Length)
            _emitters[_emitterCount++] = new(ship.ShipId, position, frameVelocity,
                orientation.GetForward(), orientation.GetRight(), radius, until);
    }

    public void FinishParticles(float dt)
    {
        NativeScene.EmitTeleportLeadup(Particles, _emitters.AsSpan(0, _emitterCount), dt, ref _accumulator, _random);
        Sparks.Clear(); Particles.CopyTo(Sparks);
    }

    public float EndFrame(ShipEntity own, SimVec position, SimVec velocity, Orientation orientation, float radius)
    {
        float flash = Flash.Update((float)_now, own.SectorId);
        if (Flash.JustTransited)
            _burst = (position, velocity, orientation.GetForward(), orientation.GetRight(), radius);
        return flash;
    }

    public void GatherGlimpses(ClientWorld world, Vector3 eye)
    {
        Glimpses.Clear();
        for (int i = 0; i < Warps.Count; i++)
        {
            var warp = Warps[i];
            if (WarpDestinationResolver.DestinationSector(world.Warps, warp.WarpId) is not { } destination ||
                !world.Clusters.TryGetValue(destination, out var cluster)) continue;
            float dx = warp.Position[0] - eye.X, dy = warp.Position[1] - eye.Y, dz = warp.Position[2] - eye.Z;
            Glimpses.Add(new(warp, destination, cluster, dx * dx + dy * dy + dz * dz));
        }
    }
}
