# P444 — owning a techbase unlocks nothing side-wide: add BuildingTechs from owned stations

Status: landed on `master`, not pushed. Base commit before this stage: `06e30b20`
(P467b). Branch: `master`.

## Problem

Native `side.GetTechs() = ttbmBuildingTechs | ttbmDevelopmentTechs`
(`sideigc.h:133-136`). `BuildingTechs` is the OR of every owned station type's
`EffectTechs` (kept current by `AddStation`/`DeleteStation`, `sideigc.h:274-287`),
which is what makes building a Supremacy/Tactical/Expansion/Shipyard base grant
that tier's hulls **at any of the side's stations** (`CanBuy`,
`stationIGC.h:383-391`). The port's `Side` only had `DevelopmentTechs`
(`Model/Side.cs:85`, seeded from civ base techs) and every buy gate used it
alone — so building a techbase did nothing outside that station's own
`TtbmLocal`. This closes that gap.

## Change (server half)

- `Model/Side.cs` — new `BuildingTechs` (recompute-on-access OR of every owned
  station's `Type.TtbmEffects`, read live from `Sector.StationRuntimes`) and
  `Techs => BuildingTechs | DevelopmentTechs` (the native `GetTechs()` term).
- `Server.Docking.cs:1353` — `StationSellsHull` now consults `side.Techs`
  instead of `side.DevelopmentTechs` (fixes the P283 hull gate and the P110
  docked-hull check that use it).
- `Server.ShipCommands.cs:1963` — the P441 BUY_LOADOUT part gate now consults
  `pSide.Techs` instead of `pSide.DevelopmentTechs`. The P441 hull gate at
  `:1921` already routes through `StationSellsHull`, so it is covered by the
  first bullet.

The development-obsolescence check (`DevelopmentLogic.IsObsolete`,
`Server.Economy.cs:705`) is deliberately left on development techs only, as
native does (`sideigc.h:629`). Money (P441/442/443/468), successor upgrades,
constructor-drone/techbase buckets and the asteroid-build discount are all out
of scope and untouched.

## Update-strategy choice

**Recompute-on-access**, not the native incremental-add / recompute-on-remove
bookkeeping. Rationale (documented in `Side.cs`): this port's `Side` does not
own a station list — stations live in `Sector.StationRuntimes` keyed by side
id — so a maintained cache on the side would need a hook at every mutation site
(`Mission.PlaceStation`, `Sector.RemoveStation`, `Station.Capture`) and could go
stale. Recomputation walks the mission's live stations on each read (the port's
analogue of native's recompute-from-scratch `ResetBuildingTechs`) and is cheap
(a mission holds at most dozens of stations), so placement / destruction /
capture are reflected automatically with no cache to invalidate. A mission-less
side reads as empty.

## Real PCore001 example (Scope 4)

Measured by decoding `PCore001.igc` (garrison type 15 `'Garrison'`, the type the
`XXV_simple.igc` map places for every side) against every civ's base techs:

- **Civ 5 'Omicron'**, base-tech mask popcount 33. Garage **type 15**
  `'Garrison'` (local pop 4, effect pop 1).
- Station type **7 `'Supremacy (Adv)'`** (class 6, effect pop 8) — and the
  basic type **11 `'Supremacy'`** (class 6, effect pop 4) — both cover hull
  **714 `'Enh Fighter'`** (price 0).
- Hull 714's `TtbmRequired` is **not** contained in
  `(garrison local | civ base | garrison own effects)` — the side owning only
  its garrison does not sell it — but is contained once a Supremacy's effects
  join `Side.Techs`.

This is the pair the live tests derive and assert (`side.Civilization.Name ==
"Omicron"`, `hull 714`). The tests search the real core dynamically
(preferring Supremacy/Shipyard class), so they are robust to the exact
Supremacy variant the dictionary order surfaces (the run selected type 7).

## Verification — pre-fix failing-test evidence

New test file `tests/Allegiance.Core.Tests/BuildingTechsLiveTests.cs` (3 live
loopback tests over a real in-process server + real TCP bots):

1. `Owning_a_supremacy_makes_the_garrison_sell_its_hull_on_the_wire` — a ship
   holding hull 714 docked at the garrison gets `TELEPORT_ACK bNewHull=true`;
   after placing an owning Supremacy for the side, a second ship holding 714
   gets `bNewHull=false` (two fresh ships are needed: a teleport that finds the
   hull unsellable converts the ship to a Lifepod, changing its current hull —
   the same reason P283 used two bots).
2. `BuildingTechs_tracks_place_capture_and_remove` — deterministic proof that
   `Side.Techs` stays current through all four native station-state changes:
   place → unlocked, capture → relocked, re-place → unlocked, remove →
   relocked.
3. `BuyLoadout_gate_grants_the_techbase_hull_after_the_station_is_placed` — the
   P441 BUY_LOADOUT path: a docked funded buyer buying hull 714 is refused
   before the station is placed, granted after.

**Must-fail-if-removed proof:** reverting the two gate lines back to
`side.DevelopmentTechs` makes tests 1 and 3 fail with the exact discriminating
assertion (`hull 714 MUST be buyable at the garrison after the Supremacy is
built`); test 2 still passes because it tests `Side.Techs` itself, which is the
mechanism the gates are wired to.

Whole-solution build: **0 warnings, 0 errors** (`/home/domingo/.dotnet/dotnet
build`). Related existing suites (P283 `LiveBuyGateDevelopmentTests`, P441
`BuyLoadoutPricingLiveTests`, `TechTreeDevelopmentLiveTests`,
`SideSwitchDevMaskLiveTests`, `P285PurchaseBuilderTargetLiveTests`) — all
green. Full suite `Allegiance.Core.Tests` run 3 times; totals 3563
(3553/3552/3554 passed), the residual failures being pre-existing live-wire
timing flakes (rotating set, all confirmed environmental — see the ledger
below); none reference or exercise the P444 change.

### Flake ledger (each seen across the 3 full runs, all pre-existing)

- `LiveTurretGunnerBindingTests.Board_binds_...` — documented persistent flake
  (recurs every run, fails on the pristine tree; do not chase).
- `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` and
  `LiveWinConditionTests.Board_refused_once_mission_over` — documented
  live-timing flaps.
- `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector...` —
  documented flake.
- `GameOverPlayersWireTests.Conquest_win_...` (2) and
  `ConquestTerritoryLiveTests.*` (3) — **proven pre-existing**: they fail
  identically on the pristine tree (`git stash` + test file moved aside).
- `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129...`,
  `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast...`,
  `CargoBayReloadTests.Reload_malformed...` / `SwapPart_out_of_range...`,
  `LiveMoneyTests.Board_refund...`, `BotFlightTests.Abrupt_disconnect...`,
  `LiveRipcordTests.Re_target_activates_to_requester_only` — sporadic
  LiveNetwork timing/socket-exhaustion flakes (the set rotates between runs,
  a hallmark of the heavy full-suite run); **each passes in isolation**.

## Client half — STOPPED at the server half (named follow-up)

Scope 3's gate asked to confirm that a station built mid-game by a builder
reaches the client's `Stations` table and to name the message; if it does not,
the instruction is to stop at the server half and report it.

**Finding: a mid-game-built station does NOT reach `ClientWorld.Stations`.** The
only writer to `_stations` is `ClientWorld.Apply(ExportStation)`
(`ClientWorld.cs:1200-1201`), and the server sends `ExportStation`
(EXPORT 26 / `OT_station`) exactly once per peer at join
(`Server.Lobby.cs:2705-2713`). `StationsUpdate(57)` (`ClientWorld.cs:1246-1252`)
only updates live hull/shield *fractions* for already-known station ids — its
wire record carries no `StationTypeId`/`SideId`, so it cannot populate a new
station. `StationCapture` re-sides an existing entry; `StationDestroyed`
removes one. When `Sector.CompleteBuild` places a finished base
(`Sector.cs:3514-3537 → Mission.PlaceStation`), nothing tells already-connected
clients the station exists. So the message that would carry a new station
(**`ExportStation` / `EXPORT(26)` / `OT_station`**) is only ever emitted at
join, never on builder completion.

Consequence: the client's BuildingTechs union over `ClientWorld.Stations` would
reflect only the stations that existed at join (the home garrisons), not ones
built mid-game, so it cannot faithfully reproduce the server's side-wide
unlock for newly-built bases. Per the brief's instruction the client change is
therefore **not implemented** here. Named follow-up: **P476 (client BuildingTechs
+ new-station wire)** — add a server broadcast of `ExportStation` for a newly
completed base (and/or a station-added record in `StationsUpdate`), then feed
`DevelopmentTechs | BuildingTechs` into `ShopCatalog.FilteredFor` /
`BuilderTargetsFor` at `Game1.Panes.cs` (computed from `ClientWorld.Stations`
owned by `MySideId` plus the catalogue's station types' `TtbmEffects`, as the
native client's own `CsideIGC` does). Until then the client shop remains a
display-only mirror; the server buy gate (this stage) is the authoritative
enforcement.

## Deliverables

- Change on `master`, not pushed (Domingo pushes): `Side.cs`,
  `Server.Docking.cs`, `Server.ShipCommands.cs`, plus
  `tests/Allegiance.Core.Tests/BuildingTechsLiveTests.cs` and this report.
