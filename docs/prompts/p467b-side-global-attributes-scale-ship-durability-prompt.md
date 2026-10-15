# P467b — researched developments scale ship durability (max energy, max armor, max shield, shield regen)

Part 2 of the combat half of side-global-attribute (GA) wiring. Run it
after P467 and reuse the same GA accessor, which is P446's or P467's.
**Also run it after P461** (energy recharge), because the max-energy
ceiling it scales is the one P461 clamps to. If P461 or the accessor
hasn't landed, stop and report.

## Why this task exists

2026-09-29 gameplay-parity pass. Developments compose into
`side.GlobalAttributes` (`src/Allegiance.Server/Server.Economy.cs:708`),
but nothing in the sim reads them.

Native wraps these hull and shield maxima in GA multipliers:

| Native site | Getter | Attribute (index) |
|---|---|---|
| `Igc/shipIGC.cpp:4563` | `MyHullType::GetMaxEnergy` | `c_gaMaxEnergy` (13) |
| `Igc/shipIGC.cpp:4572` | `MyHullType::GetHitPoints` | `c_gaMaxArmorShip` (8) |
| `Igc/shieldIGC.h:115` | `GetMaxStrength` | `c_gaMaxShieldShip` (9) |
| `Igc/shieldIGC.h:109` | `GetRegeneration` | `c_gaShieldRegenerationShip` (10) |

The indices are at `igc.h:585-619`.

The port reads raw hull and part values:
- `_hull.HitPoints` at `src/Allegiance.Core/Sim/Ship.cs:437,3597`
- `shieldType.MaxStrength` at `Ship.cs:3662`
- the shield regeneration at `Ship.cs:3840-3845`. Its own docstring
  (`:3819-3822,3860`) names the missing GA terms.
- `HullType.MaxEnergy` wherever energy is filled or clamped
  (`Ship.cs:168,349,3778`, P461's recharge clamp, and the server refits
  at `Server.Docking.cs:577,1132` and `Server.ShipCommands.cs:1911`).

## Scope

1. Add GA-aware effective getters on `Ship`, mirroring native `MyHullType`
   and the shield's getters: `EffectiveMaxEnergy`,
   `EffectiveMaxHitPoints`, `EffectiveMaxShieldStrength` and
   `EffectiveShieldRegen`. Use the shared accessor, with identity when
   there is no side.
2. Route every sim read through them, following native per site:
   - the damage math: hull fraction uses max HP, shield absorption uses
     max strength
   - the shield regen
   - the energy fills and clamps
   - the server refits

   **Keep fractions as the stored state.** Native stores fraction too, so
   a GA change rescales absolute values without changing the fraction.
   Confirm this against native and document it.
3. Check that the HUD and the wire still carry fractions, so no client
   change is needed. If an absolute value crosses the wire anywhere,
   document it.

## Explicitly out of scope

- Weapon output GAs (P467) and flight GAs (P446).
- Named follow-ups:
  - station GAs (`stationIGC.cpp:596-622`)
  - scan range and signature
  - `c_gaRegenerationShield` in the cloak (`cloakigc.h:129`); check what
    it is before scoping
  - **out-of-bounds damage** and **crowded-sector damage**
    (`shipIGC.cpp:385-425`), both of which use max HP plus max shield

## Verification

Each test **fails first**; record that.
- A side with `MaxArmorShip = 1.5`: the same absolute damage removes a
  smaller hull fraction, matching native's formula.
- `MaxShieldShip` raises absorption capacity.
- `ShieldRegenerationShip` speeds regeneration.
- `MaxEnergy` raises the ceiling, and P461's recharge clamps to it.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Commit on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P467B_SIDE_GA_SCALE_SHIP_DURABILITY.md`, with every
   site mapped from native file:line to port file:line.
