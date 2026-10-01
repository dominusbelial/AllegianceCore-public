# P466 — ship-vs-station collision: bounce and damage both (ramming bases)

## Relationship (why this exists)

From the 2026-09-29 gameplay-parity pass. Ramming and body-blocking around
bases is real Allegiance play (ram an enemy base to crack its hull; body-block
a pursuer against a rock/base). In the port, stations were **only ever
projectile colliders** (`Sector.ResolveCombatCollisions`,
`Sector.cs:1602-1608`): a ship flew straight through an enemy base — no bounce,
no impact damage. Native has a real `CshipIGC::HandleCollision` `OT_station`
case (`shipIGC.cpp:719-868`). This task lands the missing ship↔station physics:
bounce + damage to both, while leaving docking, rescue and boarding untouched.

## Native behaviour (authoritative source)

`CshipIGC::HandleCollision`, `OT_station` (`shipIGC.cpp:719-868`):

1. If the station type has the land capability for the ship's hull (fighter →
   `c_sabmLand`, else → `c_sabmCapLand`) and the ship is friendly-or-allied to
   the station's side (`shipIGC.cpp:727-740`), the collision first attempts a
   **dock** via `InGarage` + `DockWithStationEvent`. Two allied edge-cases
   (allied lifepod in an allied `c_sabmCapLand` station; allied **non-player**
   carrier in an allied station) bounce instead (`:737-740`).
2. Else a **board** hull at a different-side station with shields **down**
   boards/captures the base (`:849-858`).
3. Else a **lifepod** at a friendly `c_sabmRescue` station is rescued
   (`:861-867`).
4. A collision taking none of those paths **falls through** (no `break`) into
   the `OT_buildingEffect → OT_asteroid → OT_probe` **shared bounce block**
   (`shipIGC.cpp:868-960`) — the exact block P254 ported for asteroids. There,
   while the station's `GetFraction() > 0`:
   - **Infinite-mass elastic bounce** (`:758-778`): the station's mass is
     infinite, so only the ship's velocity changes — reflect the normal
     component, scale by `cElasticity = 0.75`.
   - **Damage to both** (`:789-822`): `damage2 = |Δv|² · c_impactDamageCoefficient`
     (`c_impactDamageCoefficient = 1/128`), `damage1 = 2·damage2`; the ship
     takes `hp_station·damage1/(hp_station+damage1)` and the station takes
     `hp_ship·damage2/(hp_ship+damage2)`, each via `ReceiveDamage(c_dmgidCollision,…)`.
   - A **lifepod** bounces but neither deals nor takes damage (`:803`).
   - If either body is killed the position/velocity correction is skipped
     (`:825-831`).

## What was built

A dedicated per-tick resolver `Sector.ResolveShipStationCollisions` (+
`ResolveShipStationCollision`), called from `Update` next to the P254 asteroid
and P354 ship-ship physics passes (`Sector.cs`). Modelled on the P254 asteroid
pass verbatim, with the station playing the role of the static rock:

- Own `CollisionDetector`: ships moving (radius `0.5·HullType.Length`),
  stations static spheres of `Station.Radius` — the same swept geometry the
  combat pass already uses for station colliders.
- Infinite-mass bounce: reflect the normal component, scale by
  `CollisionElasticity = 0.75` (the shared `cElasticity` constant).
- Damage to both bodies via the existing `Ship.ReceiveDamage` /
  `Station.ReceiveDamage`, `CollisionDamageType` (row 0), with the native
  `|Δv|² · (1/128)`, `damage1 = 2·damage2`, `hp·d/(hp+d)` split — reusing the
  P254 constants/helpers (`ImpactDamageCoefficient`, `CollisionDamageType`,
  `CollisionHpPool`).
- Lifepods bounce with no damage (`c_habmLifepod` gate, same as P254).
- Velocity-projection debounce (only an approaching ship, `v·n < 0`, bounces)
  — a resting ship inside a base is never hammered.
- Death bookkeeping reuses the existing resolvers' handling: a ship death is
  the P254 `_models.Remove`; a **station** death reuses the combat pass's
  station-kill handling (`Sector.cs:1853-1877`) — `RemoveStation` + the host
  `KillStationEvent` seam + P191 victory recompute + `SideLostRestartStation`,
  routed through the ramming ship's mission so the host broadcasts
  STATION_DESTROYED and vacates docked ships exactly as a weapon kill does.

## Dock-exclusion rule and its native mapping (scope point 2)

The resolver must **not pre-empt docking/rescue/boarding**. Native pre-empts
physics for the pairs it would dock/rescue/board; the port detects a friendly
landing in `ResolveDockCollisions` (`Sector.cs:3074`) as **any same-side ship
within `DockRadius` (200)** of the station's centre — a *capability-agnostic*
simplification of native's land-capability + friendly test (`shipIGC.cpp:727-740`).

Two facts force the exclusion to be **blanket over same-side pairs**:

1. The port's dock gate is capability-agnostic: it docks *any* same-side ship,
   so a pair the port would dock must never be bounced.
2. A base's collision radius (302.5–540 in PCore001) routinely **exceeds**
   `DockRadius` (200). A dock-bound ship must therefore *overlap the hull* to
   reach its dock point; a bounce at the hull surface would make friendly
   docking at every large base impossible.

So `IsDockOrBoardCandidate` excludes:

- **Dock / rescue — same side.** Every same-side pair is excluded (the port's
  friendly-dock gate) and left to `ResolveDockCollisions` /
  `ResolveStationRescueEvents`. This is the same-side collapse of native's
  `:727-740` friendly-dock test and `:861-867` rescue test (this port has no
  alliance concept, the same collapse every AlliedSides gate here makes). No
  friendly ship is ever bounced off its own base.
- **Board — different side, shields down.** A different-side `c_habmBoard`
  hull with the station's `ShieldFraction < DownedShieldFraction` boards /
  captures (native `:849-858`; the port's capture path,
  `Sector.cs:3133-3152`) and is excluded, so an infiltrator reaches its dock
  point. With shields up it bounces like any enemy ship.
- **Side-less ships.** Every real ship is sided; a `null`-side ship is an inert
  synthetic / test body (e.g. a physics test ship placed deep inside a base) that
  neither docks nor attacks a base, so it is excluded — zero gameplay cost.

Everything else — an enemy ship, an enemy lifepod, a board hull with shields
up — bounces. Documented in the pass/helper XML remarks.

> **A rejected alternative: the "surface-only" embedded-depth guard.** An early
> version skipped the bounce whenever the ship's centre sat inside the station
> sphere, to protect test ships placed at a base's interior point. That was
> abandoned because it silently let *small / fast* ships through: with the port's
> established detect-after-integration ordering, any ship moving more than its own
> radius per chunk is already embedded by the time the pass samples it, so a real
> fast fighter or lifepod ramming a base would be swallowed instead of bounced —
> exactly the behaviour P466 exists to fix. The P254 asteroid pass correctly has no
> such guard (it bounces any overlap out). The null-side exclusion above protects
> the physics test artefacts without that hole.

## Scope / exclusions

- Collision sound / force-feedback / camera jiggle (client-side) — out of
  scope; P463c may feed this resolver's event.
- Aleph bounce (`shipIGC.cpp:655`) — out of scope.
- Out-of-bounds damage and crowded-sector damage (`shipIGC.cpp:385-425`) —
  named follow-ups, out of scope.
- Station weapon damage, capture and destruction logic — unchanged (capture is
  only *protected* from the bounce by the board exclusion, never altered).
- The allied *exceptions* (`shipIGC.cpp:737-740`) collapse away: this port has
  no allied-vs-own distinction, so an "allied lifepod/carrier" is a same-side
  ship and is excluded (docks) rather than bounced.

## Verification

### Build
`/home/domingo/.dotnet/dotnet build` (Debug, whole solution): **0 warnings,
0 errors.**

### New sim tests — `ShipStationCollisionTests` (3, all pass)
1. `Enemy_ship_rams_enemy_station_bounces_and_both_take_real_damage` — an
   enemy (side B) ship coasts into an enemy (side A) station: velocity
   `-200 → +145.25` (elasticity 0.726 ≈ 0.75), pushed out past the surface;
   ship hull `1 → 0.7298` (drop 0.2702 vs computed 0.2633); station armor
   `1 → 0.8438` (drop 0.1562 vs computed 0.1607) — **both bodies take the
   native formula's damage**.
2. `Lifepod_bounces_off_station_with_no_damage` — an enemy lifepod bounces
   (`-200 → +148.9`) with the pod hull **and** the station armor both staying
   at 1.0.
3. `Same_side_ship_is_not_bounced_off_its_own_station` — a same-side ship
   coasting into its base keeps an inbound velocity, is never ejected to the
   bounce point, and takes no damage — the dock-exclusion guard (would fail if
   the pass over-bounced a friendly pair).

**Must-fail on unfixed code (recorded):** with the
`ResolveShipStationCollisions` call temporarily removed, the enemy-ram and
lifepod tests **fail** with "velocity must flip to outbound, got -193.45" /
"pod must bounce (velocity -197.3 should be outbound)" — the ship simply
passes through, reproducing the pre-P466 behaviour. Restored afterwards.

### Regression
Existing docking / collision / capture / rescue suites: **27/27 pass**, including
`DockDockingTests.FriendlyDock_...` (a friendly fighter still docks at its own
landable station, end-to-end over loopback TCP) and the `StationCaptureLiveTests`
hostile-board capture tests (boarding not broken by the bounce).

### Relevant-suite stability (3 consecutive clean runs)
The P466 sim tests + docking + asteroid/ship-ship collision + station capture /
rescue + the previously-affected flight-physics tests (`ShipInSectorTests`) ran
**3 consecutive times, 38/38 green each run**.

### Full suite
`Allegiance.Core.Tests` (3603 tests) across four full runs shows **only the
repo's documented pre-existing rotating LiveNetwork timing-flake families**
(conquest/game-over/win live tests, LiveMoney, LiveChat, StationsUpdate, and the
persistent `LiveTurretGunnerBindingTests`) — the failure sets rotate run to run
(14/17/10) and never touch collision, docking, capture, rescue or the new P466
tests. `LiveWinConditionTests.Multi_mission_disconnect` and the
`LiveTurretGunnerBindingTests` family were confirmed to fail on the **pristine
baseline** (my change stashed) too, so no failure is attributable to P466.

### Live MonoGame ram (manual, not performed headlessly)
The brief's "ram an enemy base in the MonoGame client" is a graphical-client
action that cannot be driven in this headless CLI environment. The server-side
mechanics it would display — the ship bouncing (velocity flip, ~0.75 elasticity),
the ship's hull fraction dropping, and the station's armor fraction dropping on
the target readout — are all computed in `Sector.Update` and ride the ordinary
server ship-update / station-update messages to the client (the same
mechanism P254's `Loopback_bounced_ship_velocity_and_position_reach_the_wire`
proved for the asteroid bounce), so the HUD shows them automatically. The sim
tests above assert each of these quantities directly.

## Files
- `src/Allegiance.Core/Model/Sector.cs` — new `ResolveShipStationCollisions`
  pass + `ResolveShipStationCollision` + `IsDockOrBoardCandidate`, called from
  `Update` beside the P254/P354 physics passes.
- `tests/Allegiance.Core.Tests/ShipStationCollisionTests.cs` — the 3 tests above.
