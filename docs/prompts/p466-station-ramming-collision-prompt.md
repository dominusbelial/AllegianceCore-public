# P466 — ship-vs-station collision: bounce and damage both (ramming bases)

## Why this task exists

This comes from the 2026-09-29 gameplay-parity pass. Ramming and
body-blocking around bases is real Allegiance play. In the port, ships
don't physically interact with stations at all.

- **Native.** `CshipIGC::HandleCollision` has an `OT_station` case
  (`~/Development/Allegiance/src/Igc/shipIGC.cpp:716-~845`).
  - A hull landing at a friendly station with the right land capability
    (`c_sabmLand` for fighters, `c_sabmCapLand` for capital ships,
    `:725-735`) docks, apart from the allied-lifepod and AI-carrier
    exceptions (`:738-740`).
  - Otherwise, while the station's `GetFraction() > 0`:
    - it bounces with elasticity `0.75`, treating the station's mass as
      infinite, so only the ship's velocity changes (`:758-785`);
    - it deals damage with the same `damage2 = |Δv|²·c_impactDamageCoefficient`
      and `damage1 = 2·damage2` formula as ship-ship, applied to both via
      `ReceiveDamage(c_dmgidCollision, …)` with the `hp·d/(hp+d)` form
      (`:785-830`);
    - lifepods take no damage (`:803`);
    - it recalculates collisions and plays `collisionSound` (`:834`).
  - `c_impactDamageCoefficient = 1/128` (`shipIGC.cpp:623`).
- **Port.** Collision resolvers exist only for ship-asteroid
  (`src/Allegiance.Core/Model/Sector.cs:2015-2140`, with constants at
  `:2046-2048`) and ship-ship (`Sector.cs:2221-2340`). Stations are added
  only as **projectile** colliders in `ResolveCombatCollisions`
  (`Sector.cs:1560-1575`). They take weapon damage through
  `station.ReceiveDamage` (`:1762`, `Sim/Station.cs`), but a ship flies
  straight through an enemy base.

## Scope

1. Add `ResolveShipStationCollisions`, modelled on
   `ResolveShipAsteroidCollisions`/`ResolveShipAsteroidCollision`. Use the
   same swept detector, with the station as a static sphere of
   `Station.Radius`, as the combat pass already does.
   - Native's infinite-mass bounce (elasticity 0.75): reflect the normal
     component and scale by 0.75.
   - Native's damage formula to the ship and to the station, through the
     existing `Ship.ReceiveDamage`/`Station.ReceiveDamage`, using the
     collision damage type (`Sector.cs:2048`).
   - No damage to lifepods. Skip when `station.Fraction <= 0`.
   - Death and kill bookkeeping reuse the existing resolvers' handling.
2. **Don't break docking.** Find how the port detects a friendly landing
   (`Server.Docking.cs`, `Sector.ResolveCarrierLandings` at `:1040`,
   `ResolveStationRescueEvents` at `:956`, and the landing trigger P53
   built). Exclude exactly the ship-station pairs native would dock or
   rescue (`shipIGC.cpp:725-740`) from the bounce, so a friendly ship
   approaching its own dockable base still docks.
3. Call the new resolver in the per-tick sector update, next to the other
   two resolvers.

## Explicitly out of scope

- Collision sound and camera jiggle (P463c, which should add this
  resolver's event if it runs after this task).
- Aleph bounce (`shipIGC.cpp:655`).
- Named follow-ups: **out-of-bounds damage** and **crowded-sector
  damage** (`shipIGC.cpp:385-425`).
- Any change to station weapon damage, capture or destruction logic.

## Verification

- Sim tests. Each must fail on unfixed code first, where the ship passes
  through; record that.
  - An enemy ship flying into an enemy station bounces: the normal
    velocity reverses and is scaled by 0.75.
  - Both the ship and the station lose the native formula's damage.
  - A lifepod bounces with no damage.
- Regression: a friendly fighter still docks at its own landable station.
  Reuse an existing docking test and show it still passes.
- Live: ram an enemy base in the MonoGame client. The ship bounces, the
  hull drops, and the station's hull drops on the HUD target readout.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Commit on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P466_STATION_RAMMING_COLLISION.md`, including the
   dock-exclusion rule and how it maps to native.
