namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>All persistent flight particles belong to one viewed cluster.
/// Smoke renderers and billboard batchers only consume these pools; they hold
/// no particles themselves. Keep resets together so a warp cannot strand smoke.</summary>
public sealed class FlightEffectPools
{
    public MissileTrailParticles Missiles { get; } = new();
    public WarpTransitParticles Warp { get; } = new();
    public DamageDebrisParticles Damage { get; } = new();
    public ShipEngineTrailParticles Engines { get; } = new();
    public ShipMotionTrailRibbons Motion { get; } = new();
    public HullHitSparkParticles HullHits { get; } = new();
    public ShieldFlareParticles Shields { get; } = new();
    public Flight.OwnShotEcho OwnShots { get; } = new();
    public WarpPulseController WarpPulses { get; } = new();

    public long Generation { get; private set; } = -1;
    public short? SectorId { get; private set; }

    public bool BeginView(long generation, short? sectorId)
    {
        if (Generation == generation && SectorId == sectorId) return false;
        Generation = generation;
        SectorId = sectorId;
        Missiles.Reset();
        Warp.Reset();
        Damage.Reset();
        Engines.Reset();
        Motion.Reset();
        HullHits.Reset();
        Shields.Reset();
        OwnShots.Reset();
        WarpPulses.Reset();
        return true;
    }

    /// <summary>Age detached particles even when their source no longer emits.
    /// Native particles expire by elapsed age (effect/particlegeo.cpp:366-394).
    /// Missile aging is part of its Update, which also gathers live emitters.</summary>
    public void Advance(float dt)
    {
        Warp.Update(dt);
        Damage.Update(dt);
        Engines.Update(dt);
        Motion.Advance(dt);
        HullHits.Update(dt);
        Shields.Update(dt);
    }
}
