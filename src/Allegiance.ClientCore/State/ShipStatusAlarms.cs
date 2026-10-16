using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P711 — a snapshot of the LOCAL ship's own status relevant to the ship
/// low-status alarm one-shots native computes every client-sim frame in
/// <c>WinTrek/trekigc.cpp:1258-1282</c>. It carries the four resource
/// fractions the alarms threshold on (hull/ammo/fuel/energy) plus the two
/// "attempting to act with an exhausted resource" latches
/// (<c>TryingToFireWeaponWithoutAmmo</c> / <c>TryingToFireAfterburnersWithoutFuel</c>,
/// trekigc.cpp:829-889). Comparing consecutive snapshots yields the one-shot
/// cue edges. Plain data, no MonoGame types — trivially constructible in unit
/// tests.
/// </summary>
/// <param name="HullFraction">Hull integrity 0..1 (<c>GetHullFraction()</c>).</param>
/// <param name="AmmoFraction">Mounted ammo 0..1.</param>
/// <param name="FuelFraction">Fuel 0..1.</param>
/// <param name="EnergyFraction">Energy 0..1.</param>
/// <param name="TryingToFireWithoutAmmo">Native <c>TryingToFireWeaponWithoutAmmo()</c>
/// (trekigc.cpp:829) — the pilot is trying to fire an ammo weapon with the
/// magazine exhausted.</param>
/// <param name="TryingToBoostWithoutFuel">Native
/// <c>TryingToFireAfterburnersWithoutFuel()</c> (trekigc.cpp:866) — the pilot
/// is holding the afterburner with no fuel.</param>
public readonly record struct ShipStatusAlarmState(
    float HullFraction,
    float AmmoFraction,
    float FuelFraction,
    float EnergyFraction,
    bool TryingToFireWithoutAmmo,
    bool TryingToBoostWithoutFuel)
{
    /// <summary>The pre-first-update snapshot, with every fraction armed (≥
    /// every low threshold) so the first real sample never spuriously fires a
    /// downward-crossing alarm (native's <c>m_fHullFraction</c> etc. initialise
    /// to 1.0, trekigc.cpp:1189-1192).</summary>
    public static ShipStatusAlarmState Unarmed => new(
        HullFraction: 1f, AmmoFraction: 1f, FuelFraction: 1f, EnergyFraction: 1f,
        TryingToFireWithoutAmmo: false, TryingToBoostWithoutFuel: false);
}

/// <summary>
/// P711 — the pure edge-detector for the ship low-status alarm one-shots
/// native plays in <c>WinTrek/trekigc.cpp:1264-1275</c>:
/// <code>
///   if (bIsPlayersShip && fNewHullFraction < 0.15f && m_fHullFraction >= 0.15f)
///       trekClient.StartSound(hullLowSound, ...);      // SIM-0361
///   if (... fNewAmmoFraction < 0.15f && m_fAmmoFraction >= 0.15f)
///       trekClient.StartSound(ammoLowSound, ...);      // SIM-0362
///   if (... fNewFuelFraction < 0.15f && m_fFuelFraction >= 0.15f)
///       trekClient.StartSound(fuelLowSound, ...);      // SIM-0363
///   if (... fNewEnergyFraction < 0.05f && m_fEnergyFraction >= 0.05f)
///       trekClient.StartSound(energyLowSound, ...);    // SIM-0364
///   if (!m_bTryingToFireWeaponWithoutAmmo && fNewFiringWithoutAmmo)
///       trekClient.StartSound(salNoAmmoSound, ...);    // SIM-0365
///   if (!m_bTryingToUseAfterburnersWithoutFuel && fNewThrustingWithoutFuel)
///       trekClient.StartSound(salNoFuelSound, ...);    // SIM-0366
/// </code>
/// Exactly one <see cref="ClientSoundEventKind"/> per crossing, fired only on
/// the downward/intent false→true EDGE (never re-fired while the condition
/// persists — native's <c>m_f...</c> snapshots gate re-triggering).
/// </summary>
public static class ShipStatusAlarms
{
    /// <summary>Native <c>hullLow/ammoLow/fuelLow</c> threshold
    /// (<c>fNewX &lt; 0.15f</c>, trekigc.cpp:1264-1268).</summary>
    public const float LowThreshold = 0.15f;

    /// <summary>Native <c>energyLow</c> threshold (<c>&lt; 0.05f</c>,
    /// trekigc.cpp:1270).</summary>
    public const float EnergyLowThreshold = 0.05f;

    /// <summary>Ammo "effectively empty" for the no-ammo attempt predicate
    /// (<c>GetAmmoPerShot() &gt; GetAmmo()</c>, trekigc.cpp:839-866).</summary>
    public const float AmmoEmptyEpsilon = 0.002f;

    /// <summary>Fuel "effectively empty" for the no-fuel attempt predicate
    /// (<c>GetFuel() &lt;= 0.0f</c>, trekigc.cpp:878).</summary>
    public const float FuelEmptyEpsilon = 0.002f;

    /// <summary>P717 — shield "online" lower bound for the shields-offline
    /// edge (SIM-0243): a shield fraction at or below this is treated as
    /// downed/offline. Matches the native downed-shield notion — a shield
    /// knocked past <c>c_fcidDownedShield</c> absorbs nothing and its
    /// <c>salShieldsOfflineSound</c> plays (shieldIGC.cpp:185-186).</summary>
    public const float ShieldOfflineEpsilon = 0.004f;

    /// <summary>
    /// Edge-detect the one-shot status alarms between two consecutive own-ship
    /// snapshots. Returns the alarms whose trigger condition JUST became true
    /// (a fraction fell below its low threshold from at-or-above, or an
    /// exhausted-resource attempt edge false→true). Order is native's code
    /// order: hull, ammo, fuel, energy, no-ammo, no-fuel.
    /// </summary>
    public static IEnumerable<ClientSoundEventKind> Classify(
        ShipStatusAlarmState prev, ShipStatusAlarmState next)
    {
        if (prev.HullFraction >= LowThreshold && next.HullFraction < LowThreshold)
            yield return ClientSoundEventKind.HullLow;
        if (prev.AmmoFraction >= LowThreshold && next.AmmoFraction < LowThreshold)
            yield return ClientSoundEventKind.AmmoLow;
        if (prev.FuelFraction >= LowThreshold && next.FuelFraction < LowThreshold)
            yield return ClientSoundEventKind.FuelLow;
        if (prev.EnergyFraction >= EnergyLowThreshold && next.EnergyFraction < EnergyLowThreshold)
            yield return ClientSoundEventKind.EnergyLow;
        if (!prev.TryingToFireWithoutAmmo && next.TryingToFireWithoutAmmo)
            yield return ClientSoundEventKind.NoAmmo;
        if (!prev.TryingToBoostWithoutFuel && next.TryingToBoostWithoutFuel)
            yield return ClientSoundEventKind.NoFuel;
    }
}
