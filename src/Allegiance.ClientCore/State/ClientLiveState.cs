namespace Allegiance.ClientCore.State;

/// <summary>
/// P423 — the live per-asteroid state a client learns from the periodic
/// <c>FM_S_ASTEROIDS_UPDATE</c> (179) sync. Distinct from the static
/// <see cref="DataAsteroidIGC"/> definition (arrived via
/// <c>EXPORT(26)/OT_asteroid</c>, held in <see cref="ClientWorld.Asteroids"/>),
/// this is the rock's <em>running</em> condition — refreshed each cadence tick
/// and decaying as it is mined / damaged — exactly the two real native readouts
/// (<c>GetOreFraction</c>, <c>GetFraction</c>, asteroidigc.h:172-190) mapped by
/// <c>ModelData::GetOre</c>/<c>GetPercentHitPoints</c> (consoledata.cpp).
/// </summary>
/// <param name="OreFraction">The rock's remaining He3 ore as a 0..1 fraction
/// (live <c>Asteroid.OreRemaining / OreMax</c>). This is <b>resource content</b>,
/// a completely separate value from <see cref="Fraction"/> (structural health).</param>
/// <param name="Fraction">The rock's current structural hit-points fraction
/// 0..1 (live <c>Asteroid.Fraction</c>, decremented only by weapon damage).</param>
public readonly record struct AsteroidLiveState(float OreFraction, float Fraction);

/// <summary>
/// P423 — the live per-station state a client learns from the periodic
/// <c>FM_S_STATIONS_UPDATE</c> (57) sync. Like <see cref="AsteroidLiveState"/>
/// this is the <em>running</em> counterpart of the static
/// <see cref="DataStationIGC"/> definition: a station is a real damageable
/// target (it has an armor pool + shield), so it participates in the generic
/// target health bar alongside ships and asteroids.
/// </summary>
/// <param name="HullFraction">The station's live armor/hull fraction 0..1
/// (<c>Station.ArmorFraction</c>).</param>
/// <param name="ShieldFraction">The station's live shield fraction 0..1
/// (<c>Station.ShieldFraction</c>).</param>
public readonly record struct StationLiveState(float HullFraction, float ShieldFraction);
