# P495 — investment screen: native build-spot / existing-builder gates and the asteroid hint

## Why this task exists

These are P487's named follow-ups (`docs/reports/P487_INVESTMENT_SCREEN.md`:
"station build-spot and no-builder — fed by the host, default open";
"asteroid hint … named follow-up"). Native is at `~/Development/Allegiance/src/`.

Native's `CanInvest` (`WinTrek/loadout.cpp` ~1095-1135) refuses a
**station-type** investment unless both of these hold:
- `BuildSpotFoundForStationType` (`:213-225`): **some known asteroid has the
  station type's `GetBuildAABM()` capability**;
- `FindBuilderForStationType` (`:227+`) is NULL: **the side doesn't already
  have a constructor for that type**.

When a station-type bucket is selected, `SetInvestAsteroid(pst->GetBuildAABM())`
highlights matching rocks (`:1396-1409`).

Native requires the matching rock type to build at all, and so does the
port: `Sector.cs:378-388` refuses construction on the wrong asteroid class
(`shipIGC.h:1695-1707`, `m_abmOrders = GetBuildAABM()` at `shipIGC.h:2057`).
**It is not a price discount.** A 2026-09-19 wiki note misread the data
field name `Stations_AsteroidDiscountMask`.

## Scope

1. Feed P487's `InvestmentPaneModel` real values, from the client's world,
   for:
   - **build spot found**: any known asteroid whose capabilities include
     the station type's `AabmBuild` (`DataStationTypeIGC.AabmBuild`).
     Check that the client knows asteroid capabilities; if it doesn't, find
     the native wire source.
   - **existing builder**: the side has a constructor drone for that
     station type. Use P459's real drone hulls and P486's observation
     approach.

   Apply native's `CanInvest` exactly.
2. **The asteroid hint.** When a station-type investment is selected or
   hovered, highlight matching asteroids in the existing renderers (minimap
   or radar), following the P436 highlight precedent. Native highlights in
   its own view; match it as closely as the port's renderers allow, and
   say what you chose.
3. Pure tests (no graphics device) for the gates and the matching set,
   failing first.

## Explicitly out of scope

- Server changes.
- Construction rules (already enforced server-side).

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P495_INVESTMENT_BUILD_SPOT_GATES.md`.
