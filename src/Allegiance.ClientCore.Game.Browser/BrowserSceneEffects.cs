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
    /// <summary>P1069 — the desktop P358/P363 pools: exhaust particles and the
    /// per-ship ribbon history, plus this frame's gathered emitter inputs.</summary>
    public ShipEngineTrailParticles Engines { get; } = new();
    public ShipMotionTrailRibbons Motion { get; } = new();
    public List<EngineTrailShip> EngineTrailShips { get; } = new(64);
    public List<EngineGlowView> Glows { get; } = new(64);
    /// <summary>P1069 — the browser-only reduced-effect decision (P1066
    /// FullEffectLoad). Exhaust goes off first; glow and ribbons stay.</summary>
    public bool FullEffectLoad { get; set; } = true;
    private readonly Dictionary<short, float> _thrustPower = new(64);
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
        Engines.Reset(); Motion.Reset(); EngineTrailShips.Clear(); Glows.Clear(); _thrustPower.Clear();
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
            // P1069 — desktop FlightEffectPools.BeginView clears the same way.
            Engines.Reset(); Motion.Reset(); EngineTrailShips.Clear(); Glows.Clear(); _thrustPower.Clear();
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
        EngineTrailShips.Clear(); Glows.Clear();
        // P1069 — reduced effect load turns exhaust off FIRST (the pool is
        // cleared and nothing new is emitted); nozzle glow and ribbons stay.
        // desktop FlightEffectPools.Advance ages every pool each frame.
        if (!FullEffectLoad) Engines.Reset(); else Engines.Update(dt);
        Motion.Advance(dt);
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

    /// <summary>P1069 — gather one roster ship's three engine effects for this
    /// frame, exactly as the desktop pose pass does: the afterburner power ramp
    /// (every frame, so a camera-culled ship still decays), the nozzle glow and
    /// the ribbon anchor. Native: AddThrustParticles thinggeo.cpp:758-917;
    /// RenderGlow thinggeo.cpp:1419-1443; TrailGeo efgeo.cpp:492-661.
    /// A ship without a mesh (stand-in) supplies its hull half-length radius, so
    /// the glow/exhaust sit at the radius-based aft anchor; <paramref name="aftWorldOffset"/>
    /// is non-null only when the native hull mesh is loaded (P566).</summary>
    public void GatherEngines(ClientWorld world, ShipEntity ship, SimVec position, SimVec velocity, Orientation orientation,
        DataHullTypeIGC? hull, float dtSec, SimVec? aftWorldOffset = null)
    {
        float radius = (hull?.Length ?? 2) * .5f;
        int stateM = ship.Latest?.StateM ?? 0;
        float throttle = ship.Latest?.Controls.Throttle ?? 0f;
        // Browser has no granted loadout for remote ships (and no shop session for
        // the own ship): the desktop's remote path is the documented
        // StateM-afterburner-bit approximation; lifepods still never boost.
        bool lifepod = hull?.HasCapability(DataHullTypeIGC.HabmLifepod) == true;
        bool mounted = ShipEffectRules.HasAfterburner(false, false, lifepod);
        float power = NativeScene.ThrustPower(mounted,
            (stateM & ShipControlState.AfterburnerButton) != 0, _thrustPower.GetValueOrDefault(ship.ShipId), dtSec);
        _thrustPower[ship.ShipId] = power;
        if (FullEffectLoad && power > 0f)
            EngineTrailShips.Add(new EngineTrailShip(ship.ShipId, position, velocity, orientation, radius, power));
        float glow = NativeScene.NozzleGlow(mounted, stateM, throttle);
        if (glow > 0f && radius > 0f)
            Glows.Add(NativeScene.GlowView(position, orientation, radius, glow, aftWorldOffset));
        if (!world.TryGetSideColor(ship.SideId, out float r, out float g, out float b))
        {
            // No roster colour yet — native's absent-side gray (desktop fallback).
            r = 0.5f; g = 0.5f; b = 0.5f;
        }
        NativeScene.AppendMotionTrail(Motion, ship.ShipId, position, orientation, radius, r, g, b);
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
