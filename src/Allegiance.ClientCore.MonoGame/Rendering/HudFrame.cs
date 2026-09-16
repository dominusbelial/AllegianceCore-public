namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A single frame of HUD telemetry, lifted straight from the local ship's most
/// recent <c>ServerShipUpdate</c> — the exact per-tick wire payload that already
/// drives ship position/orientation, so the HUD costs zero extra wire work and
/// every value is real (see <c>Allegiance.Core.Wire.ServerShipUpdate</c> for the
/// field provenance). Plain data only, no MonoGame types, so a frame is trivially
/// constructible in unit tests; <see cref="Game1"/> builds it each frame in
/// <c>Draw()</c> and <see cref="HudRenderer.Draw"/> renders it.
/// </summary>
/// <param name="HullFraction">Hull integrity 0..1.</param>
/// <param name="ShieldFraction">Shield charge 0..1.</param>
/// <param name="AmmoFraction">Mounted ammo 0..1.</param>
/// <param name="FuelFraction">Fuel 0..1.</param>
/// <param name="EnergyFraction">Energy 0..1.</param>
/// <param name="Power">Reactor power 0..1.</param>
/// <param name="Speed">True speed, world units/sec (<c>Velocity.Length()</c>).</param>
/// <param name="Throttle">Throttle axis, -1 (idle) .. +1 (full forward) — the
/// same <c>Controls.Throttle</c> axis <see cref="Flight.FlightInput"/> sends.</param>
/// <param name="AfterburnerActive">True when <c>StateM</c> has the
/// <c>ShipControlState.AfterburnerButton</c> bit latched.</param>
/// <param name="Firing">True when <c>StateM</c> has the
/// <c>ShipControlState.AllWeapons</c> bit latched (weapon group-fire mode).</param>
public readonly record struct HudFrame(
    float HullFraction,
    float ShieldFraction,
    float AmmoFraction,
    float FuelFraction,
    float EnergyFraction,
    float Power,
    float Speed,
    float Throttle,
    bool AfterburnerActive,
    bool Firing);
