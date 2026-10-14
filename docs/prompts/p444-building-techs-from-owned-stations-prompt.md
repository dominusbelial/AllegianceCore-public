# P444 — owning a techbase unlocks nothing side-wide: add BuildingTechs from owned stations

## Why this task exists

Domingo: "i dont have to use any of the builders so there is no tech
development." Verified 2026-09-29, with line numbers at `HEAD` `d5df96ec` (re-check
them): this is the core reason building a techbase is pointless in the port.

- **Native side techs = BuildingTechs | DevelopmentTechs.** `sideigc.h:133-136`,
  `GetTechs() { return m_ttbmBuildingTechs | m_data.ttbmDevelopmentTechs; }`.
  **BuildingTechs is the OR of every owned station type's `EffectTechs`**:
  - `AddStation` ORs in the new station's `GetStationType()->GetEffectTechs()`
    (`sideigc.h:274-280`).
  - `DeleteStation` recomputes it from scratch through `ResetBuildingTechs`
    (`:142-158, 282-287`).
  This is how building a Supremacy / Tactical / Expansion / Shipyard base makes
  that tier's hulls buyable **at any of the side's stations** and its
  developments researchable (`CanBuy`, `sideigc.h:224-260`, checks
  `RequiredTechs ⊆ BuildingTechs | DevelopmentTechs`).
- **The native client runs the same code.** Its own IGC `CsideIGC` keeps
  BuildingTechs from the stations it knows about. `SIDE_TECH_CHANGE(75)` only
  carries development techs.
- **The port has no BuildingTechs at all.** `Side` has only
  `DevelopmentTechs` (`Model/Side.cs:85`, seeded from the civ's base techs at
  `:356`). `DataStationTypeIGC.TtbmEffects` is decoded
  (`DataStationTypeIGC.cs:30,269`) and **read nowhere**. Every `CanBuy`
  equivalent uses `DevelopmentTechs` alone:
  - server `StationSellsHull` (`Server.Docking.cs:1347-1357`), used by the
    dock hull check (`:1802`) and by P441's buy gate if that has landed;
  - client `ShopCatalog.FilteredFor(stationLocal, sideDev)`
    (`ShopCatalog.cs:107-121`) and `BuilderTargetsFor` (`:146-160`), fed from
    `world.DevelopmentTechsFor(MySideId)` at `Game1.Panes.cs:~73-83`.
  So a new techbase only matters while you're docked **at it** (its
  `TtbmLocal`). It never unlocks anything side-wide.
- Station state changes that must keep BuildingTechs current:
  - `Mission.PlaceStation` (`Model/Mission.cs:733`), used at mission build and
    when a builder completes a base;
  - `Sector.RemoveStation` (`Model/Sector.cs:253`) on destruction;
  - `Station.Capture(newSideId)` (`Sim/Station.cs:349`) on capture.
  On the client, stations live in `ClientWorld.Stations` as `DataStationIGC`
  with `SideId`/`StationTypeId` (`DataStationIGC.cs:27,29`), re-sided on
  capture (`ClientWorld.cs:~1405-1425`).

## Scope

1. **Server: `Side.BuildingTechs`.** Compute it as the OR of `Type.TtbmEffects`
   over every live station the side owns. Choose incremental add plus
   recompute-on-remove (native's shape) or recompute-on-change, and
   document it. Keep it current through placement, removal/destruction and
   capture (both the losing and gaining side). Expose `Side.Techs =
   BuildingTechs | DevelopmentTechs`.
2. **Use `Side.Techs` wherever the server evaluates `CanBuy`**:
   `StationSellsHull` and, if P441 has landed, its hull and part buy gate.
   Leave the development obsolescence check (`DevelopmentLogic.IsObsolete`)
   on development techs only, as native does (`sideigc.h:629`).
3. **Client: the same union.** Compute the local side's BuildingTechs from
   `ClientWorld.Stations` (owned by `MySideId`) plus the catalogue's station
   types' `TtbmEffects`, as the native client's own `CsideIGC` does. Feed
   `DevelopmentTechs | BuildingTechs` into `ShopCatalog.FilteredFor` and
   `BuilderTargetsFor` at `Game1.Panes.cs`. Confirm that a station built
   mid-game by a builder actually reaches the client's `Stations` table, and
   name the message. If it doesn't, stop at the server half and report it as
   a named follow-up.
4. **Prove it with real data.** Pick a real PCore001 example: a station type
   whose `TtbmEffects` covers a real hull's `TtbmRequired` that the side's civ
   base techs plus developments do not. For example, a Supremacy/Shipyard-class
   station and a hull only it unlocks. Record the real names and ids in the
   report.

## Explicitly out of scope

- Station-type/drone-type buckets and constructor drones (the "fund a
  techbase" flow, later task S6), the investment UI (S5), and the donate
  `CanBuy` gate.
- Successor upgrades (`GetSuccessorStationType`, part/hull successors).
- Money: P441, P442, P443 and P468.
- The asteroid-type build discount (`Stations_AsteroidDiscountMask`).

## Verification

- Loopback wire tests with `Bot` + in-process `Server`. Run each **against
  the unfixed code first** and record the failure in the report:
  1. Docked at the **home garrison**, the real hull from Scope 4 is refused by
     `StationSellsHull` (and by the buy path if P441 landed). Place a real
     owning station of the unlocking type anywhere for that side, and the same
     hull becomes sellable **at the garrison**.
  2. Capture or remove that station, and the hull is unsellable again.
- A pure client test: the ClientWorld/ShopCatalog union offers the hull once
  an owned station of that type is in `Stations`, and stops offering it when
  it's gone.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub.
- Full suite with `/home/domingo/.dotnet/dotnet test`, **3 consecutive clean
  runs**. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The change plus tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P444_BUILDING_TECHS.md` with the pre-fix
   failing-test evidence, the real station-type→hull unlock example, the
   update-strategy choice, and the client station-arrival finding.
