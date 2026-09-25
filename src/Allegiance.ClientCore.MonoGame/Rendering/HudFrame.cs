namespace Allegiance.ClientCore.MonoGame.Rendering;

using Allegiance.ClientCore.MonoGame.Flight;

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
/// <param name="GroupFire">True when the pilot is in group-fire ("all weapons")
/// mode; false = single-weapon mode firing the fixed mount
/// <see cref="SelectedMount"/>. Client-local (P225), not from the wire.</param>
/// <param name="SelectedMount">The selected fixed-mount id used when
/// <see cref="GroupFire"/> is false. Client-local (P225), not from the wire.</param>
/// <param name="Flashing">True when the local player's own ship has taken damage
/// recently and the HUD is mid flash-window (P228) — the local hull fraction
/// decreased since the previous frame and the 2.0s on/off flash is currently
/// lit. Drives the red full-viewport damage overlay; <see cref="Flight.DamageFlashTracker"/>
/// supplies it, derived purely from the existing <c>ServerShipUpdate</c> hull
/// fraction (no wire work).</param>
/// <param name="WarpFlash">Warp-transit flash opacity 0..1 (P245) — the
/// full-viewport black pulse that marks a sector transit. 0 = no flash; 1 =
/// fully black. Supplied by <see cref="Flight.WarpTransitFlash"/> from the
/// local ship's <c>SectorId</c> changing (the same one-shot SET_CLUSTER the
/// server sends per transit); drawn LAST over the whole frame so the brief
/// blackout reads as "you got flung through". Distinct from
/// <paramref name="Flashing"/> (damage), which is a translucent red wash under
/// the widgets.</param>
/// <param name="Mining">P255 — true while the local player's own ship is
/// actively mining a rock (its per-tick <c>MiningAsteroid</c> is set, streamed
/// on the owner-only <c>OwnShipMiningUpdate(206)</c>). Drives a simple "MINING"
/// HUD tag; the ship's rendered facing is independently oriented toward the
/// mine target in <see cref="Game1"/>.</param>
/// <param name="Cloaked">P270 — true while the local player's own ship carries
/// the <c>CloakActive</c> stateM bit (P269). Drives a small "CLOAKED" HUD tag;
/// the own ship's own view is a client presentation choice, separate from the
/// teammate/enemy transparency of <see cref="Allegiance.ClientCore.MonoGame.Flight.CloakVisual"/>.</param>
/// <param name="KillBonusPercent">P298 — the local ship's live Kill Bonus
/// readout (native <c>100·(GetExperienceMultiplier()−1)</c>, 0..50 cap 50),
/// streamed by the owner-only <c>OwnShipKillBonusUpdate(212)</c>. 0 = no bonus.
/// Feeds the Kill Bonus gauge.</param>
/// <param name="KillBonusStreak">P298 — the raw kill-streak count behind
/// <see cref="KillBonusPercent"/> (native <c>m_experience</c>), for the gauge's
/// kill tally. Same stream.</param>
/// <param name="RollDegrees">P298 — the local ship's current roll angle in
/// degrees about its forward axis, derived client-side from the real orientation
/// carried on the same <c>ServerShipUpdate</c> the HUD already reads (the
/// chase/cockpit cameras already track this orientation including roll). Positive
/// = the bank direction native's <c>Orientation.Roll(+θ)</c> produces. Feeds the
/// Roll gauge.</param>
/// <param name="WeaponSystems">P362 — the per-mounted-system status list for the
/// local ship (one <see cref="WeaponStatusRow"/> per mounted weapon/missile
/// system), derived by <see cref="WeaponReloadTracker"/> from the live ammo
/// fraction. Empty when the own ship's loadout is not yet known (no
/// <c>BUY_LOADOUT_ACK</c> observed) — the HUD panel then draws nothing.</param>
/// <param name="IsAdriftLifepod">P377 — true while the local player's own ship
/// is a Lifepod hull still adrift in space (hull carries <c>HabmLifepod</c>
/// AND <c>StationId</c> is NA), i.e. the pod whose oxygen is genuinely
/// burning. While true the HUD surfaces the FUEL slot's real oxygen fraction
/// as an OXYGEN countdown. False for a normal ship, a docked/rescued pod, or a
/// remote lifepod.</param>
/// <param name="OxygenSecondsRemaining">P377 — whole seconds of oxygen left
/// (ceil of <c>FuelFraction × enduranceSeconds</c>), for the mm:ss countdown.
/// Meaningful only when <see cref="IsAdriftLifepod"/> is true.</param>
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
    bool Firing,
    bool GroupFire,
    int SelectedMount,
    bool Flashing,
    float WarpFlash,
    bool Mining,
    bool Cloaked,
    float KillBonusPercent,
    short KillBonusStreak,
    float RollDegrees,
    IReadOnlyList<WeaponStatusRow> WeaponSystems,
    bool IsAdriftLifepod,
    int OxygenSecondsRemaining);
