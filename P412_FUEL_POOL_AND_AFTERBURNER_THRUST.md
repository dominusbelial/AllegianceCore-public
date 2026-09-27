# P412 — real fuel pool + real afterburner thrust boost

**Branch:** `master` (Domingo pushes). Native source: `~/Development/Allegiance/src/Igc`.

P411 landed the dead-ship loot drops but left its fuel branch as dead code because this
port had no live per-ship fuel pool, and investigation revealed the deeper gap: the
afterburner — fuel's only real consumer — was itself a complete no-op (`Ship.cs` hardcoded
`thrustRatio == 0 (no AB)`). This stage builds BOTH halves, because neither works alone.

Everything below was verified against the live native source and the live port tree; every
claim carries a `file:line`. Whole-solution build is 0 warnings / 0 errors.

---

## 1. The real `Ship.Fuel` shape

Native: `CshipIGC::m_fuel` — a live `float` clamped to `GetMaxFuel()` (hull stat);
`SetFuel` posts "Fuel depleted." on the >0→0 transition with a half-mount-rate reload
debounce (`shipIGC.h:623-651`). This port's `Ship.Ammo` is a plain auto-property whose
zero-transition auto-reload is triggered at the consumption site (`Weapon.cs:342-343` →
`Ship.TryAutoReloadAmmo`), and the brief instructed mirroring *that* shape exactly.

Added to `Ship.cs`:
- `public float Fuel => _fuel;` — the pool, backed by `private float _fuel`, seeded to
  `Hull.MaxFuel` in the constructor (`Ship.cs:158`).
- `public void SetFuel(float newVal)` — port of native `SetFuel` (`shipIGC.h:627-651`):
  clamp to `Hull.MaxFuel`, assign, and on the >0→0 transition fire `TryAutoReloadFuel`
  (the debounce lives inside, mirroring how `TryAutoReloadAmmo` is triggered).
- `public AutoReloadOutcome TryAutoReloadFuel(double now)` — the exact structural mirror of
  `TryAutoReloadAmmo` for the `ET_Afterburner` case: rate-limited by the same
  `0.5 / FloatConstants[c_fcidMountRate]` debounce (`_timeReloadFuel`), drains the LAST
  `c_packFuel` pack in cargo into `Fuel` through the same P192 applicator, and surfaces
  the outcome via `IIgcSite.AutoReloadTriggered`. `LastFuelAutoReloadOutcome` /
  `LastFuelAutoReloadAt` parallel the ammo properties (for a fuel-reload HUD analog).
- `public void ApplyPowerup(int bits)` — the port of the `c_tcPowerup` case of native
  `HitTreasure` (`shipIGC.h:1326-1346`): hull→1.0, mounted shield→1.0, energy→max,
  `fuel → SetFuel(float.MaxValue)` (clamped to real max), ammo→max. The fuel leg is what
  the fuel-treasure refill test exercises.

Bug caught during test bring-up: the first draft declared `Fuel` as an auto-property
(`{ get; private set; }`) alongside a separate `_fuel` field — two disconnected storage
locations, so every refill path wrote `_fuel` while readers saw the dead auto-property (0).
Fixed to back `Fuel` by `_fuel`.

## 2. The afterburner state added

Native keeps `m_power` / `m_fActive` / `m_mountedFraction` on the per-mounted
`CafterburnerIGC` object (`afterburnerIGC.h:172-181`); this port realises the singular
`ET_Afterburner@0` slot, so those members fold onto the ship exactly like the cloak's do
(the `_mountedCloak` precedent, `Ship.cs:5964`):

- `private MountedPart? _mountedAfterburner` — the `ET_Afterburner@0` part (native engages
  only from mount 0, `afterburnerIGC.h:67-70`).
- `private float _afterburnerPower` / `private bool _afterburnerActive` — `m_power` /
  `m_fActive`.
- **Mount ramp reuses the ship's existing part-mount mechanism.** Rather than a new float,
  the afterburner mount fraction IS the `MountedPart.FractionMounted` wire byte (255 = 1.0),
  advanced in `UpdateAfterburner` at `dt · c_fcidMountRate` and written back via the
  existing `WithFraction` rewrite helper. The brief said "reuse the existing part mount-ramp
  mechanism" — confirmed there is no prior time-based ramp in this port (weapons/cloak treat
  mounted-ready as "present", `Ship.cs:5999-6003`), so this is the port's first real ramp,
  built on the existing `FractionMounted` field + `WithFraction` (the "reuse" is the field,
  not a reinvented float). Fresh mounts come in mounted-ready (255) so they work immediately;
  a fuel-pack drain resets it to 0 (`ResetEquippedFraction`) and the ramp rebuilds it — the
  re-mount delay native has (`afterburnerIGC.cpp:111-125`).

**`UpdateAfterburner(float dt)`** — the port of `CafterburnerIGC::Update` →
`IncrementalUpdate(GetLastUpdate(), now, TRUE)` (`afterburnerIGC.cpp:60-63,103-164`), the real
fuel-consuming tick, driven from `OnUpdate` exactly where native's `CshipIGC::Update` calls
its mounted parts' Update (`shipIGC.cpp:358-359`) alongside `UpdateCloak`/`UpdateShield`.
Sequence, faithful to native: mount-ramp gate (only the bUseFuel=true call advances the
fraction) → activation (`bActivated = button-held && fuel != 0`) → power ramp
(`+dt·onRate` held / `−dt·offRate` released, clamped [0,1]) → fuel burn
`fuelUsed = power·fuelConsumption·maxThrust·dt`, draining or zeroing+deactivating on
overdraw ("out of gas", `afterburnerIGC.cpp:148-160`).

Stats read from the mounted part's real decoded `DataPartTypeIGC.Afterburner`
(`FuelConsumption`/`MaxThrust`/`OnRate`/`OffRate` — already decoded faithfully,
`DataPartTypeIGC.cs:121-131`). `GetMaxThrustWithGA`'s `c_gaThrust` side-attribute term is
treated as 1.0 — the port's established no-GlobalAttributeSet simplification, identical to
`Weapon.cs:26-30` and `Ship.cs:3628`.

## 3. ExecuteMove thrust/drag wiring (`Ship.cs:3902-3930,3983`)

Replaced the hardcoded `thrustRatio == 0 (no AB)` with the real block
(`shipIGC.cpp:2939-2959`): `thrustRatio = abThrust / thrust` when the button is held with a
mounted afterburner, and `drag += (power · abThrust) · backward` when power ≠ 0; the throttle
branch now uses `maxSpeed · (−1 − thrustRatio)` (`shipIGC.cpp:2992-2993`).

**Documented divergence (deliberate):** native calls `afterburner->IncrementalUpdate(
timeStart, timeStop, FALSE)` inside ExecuteMove (power ramp, no fuel burn) AND the ship's
Update calls it again with TRUE each tick — two ramps per tick. This port's single OnUpdate
pass runs ExecuteMove before the per-tick part dispatch, so the power ramp is driven solely
by `UpdateAfterburner` (bUseFuel=TRUE) and ExecuteMove READS the resulting power — not
doubled per tick (one-tick lag in the drag contribution). Steady-state depletion and the
speed boost are unaffected (verified). Also documented: native's `thrustRatio` is NOT
fuel-gated (`shipIGC.cpp:2947-2949`), so "out of gas" faithfully manifests as the power/
drag-boost stopping (and in practice decelerating toward a lower equilibrium), not an instant
return to base cruise — the "cuts out" the live proof measures is the powered-boost cutoff.

## 4. Every fuel touch point wired

| Touch point | Native | Port |
|---|---|---|
| Afterburner burn + out-of-gas | `afterburnerIGC.cpp:153,157` | `UpdateAfterburner` → `SetFuel` |
| Fuel-pack RELOAD drain | `FedSrv.CPP:6587` | `TryApplyReloadEntry` fuel branch now `SetFuel(_fuel + amount)` (was the documented no-op) |
| Station launch top-up | `stationIGC.cpp:474` | `RepairAndRefuel` now `SetFuel(maxFuel)` (was the documented no-op) |
| Fuel-treasure pickup | `shipIGC.h:1342` (`SetFuel(FLT_MAX)`) | new `c_tcPowerup` pickup → `Ship.ApplyPowerup(Fuel)`; `Sector.ResolveTreasurePickups` gate + `Server.PerformTreasurePickup` Powerup case added |
| Child-ship fuel inheritance | `shipIGC.cpp:3538` (`SetFuel(parent->GetFuel())`) | `Promote` now `SetFuel(parent.Fuel)` |

(The c_tcPowerup pickup was previously "separately-scoped follow-up, left in place" in
`Sector.cs:1149-1155`; this stage wires it with the full native PowerupCode bitmap.)

## 5. P411 closure — the fuel-drop connection

`Server.DropKillShipLoot` (`Server.Combat.cs:1462-1487`) previously had a comment explaining
fuel was always 0 (the port had no fuel pool) so native's `if (fuel > 0)` was never true.
Now that `Ship.Fuel` is real, the section is wired: mirror the ammo branch exactly —
`pmission->GetFuelPack()` (the `c_packFuel` part type), the same 1-in-5 roll, the treasure
amount cast `short(fuel)` with dv=100/lifespan=30, and `SetFuel(0)` EITHER WAY
(`FedSrv.CPP:11714-11728`).

## 6. Verification

**Tests (Allegiance.Core.Tests):**
- `FuelPoolAndAfterburnerTests` (new, 7): depletion at EXACTLY native's rate at full power;
  out-of-gas (hits exactly 0, deactivates, powered acceleration cuts out — measured
  +4.53→−1.84 Δspeed/tick on the live server, `LastFuelAutoReloadOutcome == Depleted`);
  engaged-vs-inactive-vs-unmounted ExecuteMove cruise (measured 466 vs 50 vs 78, hull
  maxSpeed 100 — a real, 6× flight-model difference); refill to the real max from a fuel
  pack drain, station RepairAndRefuel, and a fuel-treasure powerup; PLUS the LIVE real-server
  proof (real PCore001 hull in a real XXV_simple sector, real TickSim): peak 724.9 vs hull
  maxSpeed 180 while fuelled, fuel draining over ~8s to exactly 0, acceleration collapsing at
  exhaustion.
- `KillShipLootDropTests` (extended, +2): fuel drops at ~1-in-5 (measured 19% over 400
  kills, same odds as parts/ammo), and zero-fuel ships never drop a fuel treasure.

**Must-fail (proven by temporarily reverting the wiring, watching it red, restoring):**
- Removing the ExecuteMove afterburner block (thrustRatio + drag) → engaged speed collapses
  to equal the unmounted ship (test red).
- Removing the fuel burn in `UpdateAfterburner` → depletion + out-of-gas tests red (fuel
  stays 1000).
- Removing the P411 fuel-drop roll → 0 fuel treasures (test red).
- Removing the fuel-pack `SetFuel` drain → refill test red.
(The refill/setFuel wiring also failed organically during bring-up when `Fuel` was a
mis-wired auto-property — all three refill tests showed 0.)

**Suite:** full `Allegiance.Core.Tests` run; only the pre-existing documented live-wire
timing flakes are expected (see below). Whole solution: 0 warnings, 0 errors.

## Scope / exclusions

- No visual/audio afterburner effects (engine glow, trail, sound) — flight-model and
  resource-pool correctness only. (Cheap hookup note: P235's afterburner-smoke particle
  parameters in `missileigc.cpp:53-55` reference the afterburner exhaust system, but wiring
  a visual is separate client work; not built speculatively.)
- P368–P411 untouched (P411's own fuel branch is closed here by necessity).
- The active-emitter SIGNATURE change native's afterburner Activate/Deactivate makes
  (`ChangeSignature(±signature)`, `afterburnerIGC.h:78,89`) is omitted, same lazy-handling
  stance as the cloak's — out of P412's flight/resource scope.
