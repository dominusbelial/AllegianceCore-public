# P495 — investment screen: native build-spot / existing-builder gates and the asteroid hint

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`.

## Problem

P487's `InvestmentPaneModel` carried the station-type sub-gate of native
`CanInvest` (`WinTrek/loadout.cpp:1124-1131`) as **host-fed, default open**
(P487 named follow-up). This task feeds it **real values** from the client's
world — the two native predicates `PartListItem::BuildSpotFoundForStationType`
(`loadout.cpp:213-225`) and `FindBuilderForStationType` (`loadout.cpp:227-240`)
— and adds the **asteroid hint** (native `HangarWindow::SetInvestAsteroid`,
`loadout.cpp:1396-1409`), the other P487 named follow-up.

Native refuses a station-type investment unless **both** hold (and the port's
`Sector.cs:378-388` already enforces the same rock-type rule server-side, so
this is the client mirror, **not** a price discount — the 2026-09-19 wiki note
misreading `Stations_AsteroidDiscountMask` is confirmed wrong).

## What was implemented

### 1. The two gates, fed from the client's world — `InvestmentBuildGates`

A new pure, MonoGame-free static class
`src/Allegiance.ClientCore.MonoGame/Rendering/InvestmentBuildGates.cs` ports
both native predicates exactly and is unit-testable headless:

- **`BuildSpotFound`** (= `BuildSpotFoundForStationType`, `loadout.cpp:213-225`):
  any known asteroid whose capability mask includes the station type's
  `AabmBuild`. The port scans `ClientWorld.Asteroids` (already populated from
  `EXPORT(26)/OT_asteroid`) via `DataAsteroidIGC.HasCapability(aabm)` against
  `DataStationTypeIGC.AabmBuild`.
- **`BuilderExists`** (= `FindBuilderForStationType`, `loadout.cpp:227-240`):
  native returns a side ship whose `GetBaseData() == pStationtype` — a
  constructor drone **committed to that exact station type**. The peer ship
  wire does **not** carry a ship's base data, so the port derives the verdict
  from the two observable signals that together tie a constructor to a specific
  station type:
  1. **constructor hull present** — the side has a ship whose real hull is the
     station type's construction-drone hull: the P458 station-type→
     construction-drone→hull mapping (`DataStationTypeIGC.ConstructionDroneTypeId`
     → `DataDroneTypeIGC.HullTypeId`, the real constructor hulls of P459),
     observed on the wire the P486 way (`SideHasConstructorHull`);
  2. **an active construction on a compatible rock** — a building effect sits
     on an asteroid whose capability matches `AabmBuild`
     (`ActiveConstructionOnCompatibleRock`, using `ClientWorld.BuildingEffects`).
- **`CanBuildStation`** = native's `CanInvest` station gate (`loadout.cpp:1124-1131`):
  `BuildSpotFound && !BuilderExists`.

Why **both** signals are required (documented in the class doc): the hull alone
would falsely refuse every station type sharing the same constructor hull (many
PCore001 station types share "Sm/Lg Std Constructor"); the rock alone would
count an enemy's construction — but requiring the own-side constructor hull
excludes it, since opposing factions build different station types on different
rock capabilities. **Documented approximation:** a constructor still en route
(spawned but not yet building) has no building effect yet, so `BuilderExists`
is momentarily false — but such a constructor exists only once its bucket is
100% funded, which already makes `CanInvest` refuse via the percent-bought gate
(`loadout.cpp:1138-1139`), so the two coincide in every observable outcome.

`Game1.Panes.cs` `BuildInvestmentModel` now feeds this real gate (snapshotting
`world.Asteroids` / `world.BuildingEffects` / the side's ship hulls **under the
lock**, so the delegate the pane calls later never races the live world). Without
the static core the gate stays open (the pre-P495 default).

### 2. The asteroid hint — radar build-spot highlight

When a station-type bucket is the investment-pane cursor item, the matching
rocks light up, following the P436 highlight precedent
(`RadarBracketRenderer`).

**Renderer choice (said plainly):** the port's *minimap* is a cluster-level
graph (no individual rocks), and the *radar* draws no asteroid blips (P436).
Native highlights the matching rocks in the sector view visible *behind* the
purchases screen; the port's docked pane was a near-opaque full-screen panel
covering both. So the hint lives on the **radar** (the sector instrument that
can show rocks), and the investment pane's backdrop is made translucent
(`DockedScreenRenderer.InvestBackdropAlpha = 0.52`) so the radar — and the
highlight — shows through in the panel margins while browsing investments,
matching native's "see the build rocks behind the buy screen". The shop and
teleport panes keep the near-opaque backdrop.

Mechanics:
- `RadarContactType.Asteroid` + `RadarContact.IsInvestBuildSpot` +
  `RadarBracketRenderer.BuildSpotHighlightColour` (amber) / `RockColour` +
  `DrawBuildSpotHighlight` (pulsing double-ring + dot — deliberately *no* target
  diamond so it cannot alias the P436 selected-target "lock") + a stroked
  hexagon "rock" icon.
- `RadarInvestAsteroidSelection.IsBuildSpot(activeAabm, caps)` — a pure
  per-rock decision (the P436 `RadarTargetSelection` discipline).
- `Game1.RefreshInvestState()` (called from `DrawRadar`) builds the frame's
  model once and resolves the hovered station bucket's `AabmBuild` into
  `_investAsteroidAabm`; `DrawRadar` adds only the **matching** rocks in the
  player's sector as highlighted asteroid contacts. The pane draw reuses that
  frame's model (`_investModel`) so the list and the highlight always agree.

Only the matching rocks are drawn (non-matching rocks keep no blip), so the
hint shows exactly where the selected station type may be built.

## Out of scope (honoured)

No server change; no construction-rule change (already enforced server-side,
`Sector.cs`). The P487 list-predecessor pruning and mid-mission-join id caveat
remain as documented in P487.

## Verification

### Fail-first (measured)

Before any source existed, the two test files referenced symbols absent from
the tree (`InvestmentBuildGates`, `RadarInvestAsteroidSelection`,
`RadarContactType.Asteroid`); `dotnet build` of the test project failed with
`error CS0103: The name 'InvestmentBuildGates' does not exist in the current
context` (and the other two) — red by construction, exactly the P487 precedent.

### Whole-solution build

`/home/domingo/.dotnet/dotnet build -m:8 -v:m` → **`Build succeeded. 0
Warning(s), 0 Error(s)`** (TreatWarningsAsErrors is on). Re-confirmed after the
final `BuildSpotFound` refactor.

### New tests (pure, no graphics device) — 20/20 pass

- `tests/…/InvestmentBuildGatesTests.cs` (16): every gate — build-spot found /
  not-found / unrelated-bit; construction-hull resolution and its null fallback;
  builder-exists each branch (no constructor, constructor-idle, constructor +
  active build on a compatible rock, **same-hull build on an incompatible rock →
  not this station type**, unknown drone type); `CanBuildStation` allowed /
  refused (no spot / builder exists); an integration case feeding the real gate
  into `InvestmentPaneModel.CanInvest`; and `MatchingAsteroidIds` (the matching
  build-spot set, the shared source behind `BuildSpotFound`).
- `tests/…/RadarInvestAsteroidSelectionTests.cs` (4): matching capability /
  subset match / null active mask / non-matching mask.

Focused run: `InvestmentBuildGatesTests|RadarInvestAsteroidSelectionTests` →
**20/20 passed**; the wider `…InvestmentPaneModelTests|…RadarTargetSelectionTests`
group → **42/42 passed**.

### Full suite — `scripts/run-tests.sh` (P471), 3 consecutive runs

| run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1105/1105 | 403/403 | 3697/3698 | `LiveRipcordTests.Re_target_activates_to_requester_only` |
| 2 | 12/12 | 1105/1105 | 403/403 | 3697/3698 | `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` |
| 3 | 12/12 | 1105/1105 | 402/403 | 3694/3698 | `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector…`, `AutoReloadOnEmpty…RELOAD129`, `WarpBombTests.Live_whole_mission_warp_bomb…`, `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine…`, `LoadoutChangeLiveTests.Boarding_and_leaving…` |

**P495's own tests passed in all three runs** (never in any failure list):
`InvestmentBuildGatesTests` 16/16, `RadarInvestAsteroidSelectionTests` 4/4,
plus the existing `InvestmentPaneModelTests`/`InvestmentLayoutTests`. Every
residual failure is a pre-existing live-socket timing flake in a Core/ClientCore
class that does not reference the changed assembly (`Allegiance.ClientCore.MonoGame`
rendering + a pure gates class), rotating run-to-run. Cited in
`docs/TEST_FLAKES.md` — three were already in the ledger (Ripcord-adjacent,
`Conquest…`, `ShipDeleteGone…`, `AutoReload…`); four newly-observed
(`LiveRipcordTests.Re_target…`, `WarpBombTests…`, `MineCrossSideVisibilityLiveTests…`,
`LoadoutChangeLiveTests…`) were added there under a **P495 session observations**
section, the same single-source convention P487 established.

### Commit

Committed on `master` (not pushed); `git branch --show-current` → `master`,
confirmed with `git log -1` below.

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/InvestmentBuildGates.cs` (new) — the two native predicates + the `CanInvest` station gate + the matching set.
- `src/Allegiance.ClientCore.MonoGame/Rendering/RadarInvestAsteroidSelection.cs` (new) — pure build-spot-per-rock decision for the highlight.
- `src/Allegiance.ClientCore.MonoGame/Rendering/RadarBracketRenderer.cs` — `RadarContactType.Asteroid`, `RadarContact.IsInvestBuildSpot`, rock/build-spot colours + draw.
- `src/Allegiance.ClientCore.MonoGame/Rendering/DockedScreenRenderer.cs` — translucent investment-pane backdrop (`backdropAlpha` param + `InvestBackdropAlpha`).
- `src/Allegiance.ClientCore.MonoGame/Game1.Panes.cs` — feed the real build gates; `RefreshInvestState` (frame model + hovered bucket's `AabmBuild`).
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — radar asteroid build-spot contacts; pane draw reuses the frame model + translucent backdrop.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_investModel` / `_investAsteroidAabm` fields.
- `tests/…/InvestmentBuildGatesTests.cs` (new, 16), `tests/…/RadarInvestAsteroidSelectionTests.cs` (new, 4).
- `docs/TEST_FLAKES.md` — P495 session-observation rows.
