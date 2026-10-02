# P493 — Enemy spotting: a side learns the status of enemy ships its probes and teammates see

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`. Branch verified with
`git branch --show-current` → `master`.

## Summary

Native reports an enemy ship's status to **every side that has seen it**, not
just its own (`CFSShip::ShipStatusRecalculate`, FedSrv/fsship.cpp:405-425 →
`ShipStatusSpotted` per seeing side, :237-285; `ShipStatusHidden` :287+ covers
losing sight). This is what makes probes and scouts **eyes for the team**: the
sector map and radar know where enemies were last seen, and in which sector.

In the port, `SHIP_STATUS(90)` was **same-side only** (own ship + P58 teammate +
P366 NPC paths in `Server.Broadcast.cs`), so a side's probe/scout gave it **no
remote vision of enemies** — the cross-side gap P375's parity pass flagged. P493
closes it: when a side comes to see (or loses sight of) an enemy ship through the
existing P132/P270/P476 visibility rules (probes, ships, scan range), the server
delivers that enemy's SHIP_STATUS to every seated peer of the seeing side, and
the client records the last-known sector/state and surfaces it on the sector map.

**Out of scope (unchanged):** the visibility rules themselves (P270 cloak /
P476 scan-range-signature GAs) and alliances.

## The change

### Server — `SweepCrossSideShipStatuses` (edge-driven spotted/hidden)

A new SimPass step (`crossSideShipStatusSweep`, running after
`crossSideExportSweep` so a freshly-spotted enemy's identity EXPORT lands before
its status — native's ExportObject-then-status ordering) iterates every ship in
every Started/Starting mission and reads its latched
`SeenBySide(seeingSide)` — the P132/P270/P476 sweep result already maintained
per tick (every 10 passes, the faithful `c_nPassesPerUpdate` cadence). It tracks
the previous value per `(seeingSide, ship)` in `_crossSideShipStatusSeen` and
fires exactly the native Show/Hide transitions:

- **false→true edge ("spotted")** — the port of `ShowObject` →
  `ShipStatusSpotted` (FedSrv.CPP:10838): sends the side's seated peers a
  SHIP_STATUS with `State=c_ssFlying`, `ParentID=NA`, `HullID` = the ship's
  hull, `SectorID` = the ship's current cluster (the probe/scout's sector —
  the last-known position), `Unknown=false`.
- **true→false edge ("hidden")** — the port of `HideObject` →
  `ShipStatusHidden` (FedSrv.CPP:10870): sends the same peers a SHIP_STATUS
  with `Unknown=true`, preserving the last-known sector/hull/state (native
  `SetUnknown` only flips the unknown bit, fsship.cpp:290).

**Cadence — edges, not per-tick.** A status is sent only when a side's
visibility of a ship CHANGES, never while it stays visible. A slow catch-up
refresh (`CrossSideStatusRefreshTicks = 100` ≈ 5 s, mirroring the same-side
`TeammateStatusRefreshTicks` and the P369 identity self-heal) re-sends a
currently-seen enemy's spotted status so a peer that joins an already-informed
side (or loses an edge packet) still learns the last-known sector — bounded,
never every tick.

**Audience — per-side sensor net, any sector.** When a side comes to see an
enemy, EVERY seated peer of that side receives the status regardless of which
sector that peer sits in — a probe anywhere is eyes for the whole side. This
deliberately does NOT reuse P375's `CrossSideViewers` seated-peer-in-subject-
sector gate, which exists for live per-tick telemetry; a coarse last-known
sector status is exactly the cross-sector info a fleet member is owed. The
ship's own side is skipped (the same-side teammate path already serves it).

**Coverage.** Any `Ship` in any sector — peer-owned or NPC — is eligible,
matching native (every ship's `CFSShip` carries ship status, and the port
already gives same-side NPCs a status, P366). A cloak-hidden enemy (P270) drops
out because its shrunken effective signature makes `SeenBySide` false.

Two connection-scoped latches (`_crossSideShipStatusSeen`,
`_crossSideShipStatusTick`) are freed on disconnect/re-login exactly like their
teammate/NPC siblings (Server.Lifecycle.cs, Server.Lobby.cs).

### Client — last-known sector/state + sector-map "known enemy" markers

- `ClientWorld.Apply(ShipStatusMessage)` already records the status fields onto
  the cross-side `ShipEntity` (SectorId / State / Unknown / HullId / …), so a
  cross-side status now genuinely updates the enemy's last-known sector and
  state with no extra plumbing. Stale doc comments ("no scanning system") were
  corrected.
- The sector map now surfaces that data: `SectorMapQueries.KnownEnemySectors`
  (pure, unit-tested) returns, per sector, the count of cross-side ships whose
  last-known sector is valid, have a real identity (EXPORT), and are not
  `Unknown`. `StarmapRenderer` draws a small solid-red "known enemy here" cross
  beside each such sector (both the expanded starmap and the compact mini-map),
  wired in `Game1.DrawStarmap` under the gate lock. **Checked and wired:** P374's
  map threat icons derive from the server's `ClusterWarningUpdate(214)` stream
  (computed from physical ship presence), NOT the ShipEntity last-known sectors —
  so the two are complementary, and this is the minimal piece that carries the
  cross-side SHIP_STATUS last-known data to a rendered surface.

## Native citations (all file:line)

- `CFSShip::ShipStatusSpotted` — FedSrv/fsship.cpp:237-285 (state=c_ssFlying,
  ParentID=NA, HullID, SectorID, :254-260).
- `CFSShip::ShipStatusHidden` — FedSrv/fsship.cpp:287-335 (`SetUnknown`, :290).
- `CFSShip::ShipStatusRecalculate` — FedSrv/fsship.cpp:405-425 (per seeing side).
- Trigger points — FedSrv.CPP:10838 (`ShowObject`→Spotted) and FedSrv.CPP:10870
  (`HideObject`→Hidden).
- ShipStatus wire layout — `ShipStatus.cs` (FMID 90, compiler-verified).
- Visibility rules consumed (unchanged) — `Sector.ResolveCrossSideVisibility`
  (P132), P270 cloak, P476 scan-range/signature GAs.

## Verification

### Fail-first (recorded)

- **Live test** `P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_...`:
  with the `crossSideShipStatusSweep` Step temporarily removed, it **fails**
  ("side-0 peer must receive the spotted status of the enemy ship"); restored,
  it passes. (The earlier "fail-first" attempt only commented a doc line — the
  Step was still active and the test still passed, confirming the discriminator
  is the Step wiring, not a stale assert.)
- **Pure-query tests** `SectorMapQueries.KnownEnemySectors` (6) and the client
  `ClientWorld` last-known tests (2) exercise new logic that did not exist
  before P493.

### New tests

- `tests/Allegiance.Core.Tests/P493EnemySpottingLiveTests.cs` (1, live): a real
  booted `Allegiance.Server` on `XXV_simple.igc` with a side-0 recon probe
  (real PCore001 type, ScannerRange 5000) in a station-free sector and a side-1
  ship flown in/out of probe range; a real `FltBot` side-0 peer in a DIFFERENT
  sector receives the spotted status (Unknown=false, State=flying, correct
  sector) then the hidden transition (Unknown=true). Measured run:
  ```
  P493-LIVE: probe(id 9001, range 5000) in sector 1052 spotted side-1 ship 8099
  → side-0 peer got status (sector 1052, state 3, hull 59); hidden after 10+0 pumps
  ```
  The hidden transition fires whenever the side loses sight — the ship leaving
  probe range (this test) or the probe being destroyed (both drop `SeenBySide`;
  the latter is the same edge in `ResolveCrossSideVisibility`).
- `tests/Allegiance.ClientCore.Tests/P493ClientWorldShipStatusTests.cs` (2):
  a cross-side spotted status records last-known sector/state; the hidden status
  flips `Unknown` while preserving the last-known fields.
- `tests/Allegiance.ClientCore.MonoGame.Tests/SectorMapQueriesTests.cs` (+6):
  `KnownEnemySectors` counting, cross-side/same-side/unknown/no-identity/
  no-own-side/grouping.

### Regression set (visibility/cloak/probe), green

`P493EnemySpottingLiveTests`, `CrossSideExportLiveTests`,
`MineCrossSideVisibilityLiveTests`, `CrossSideVisibilitySweepTests`,
`CrossSideVisibilityFoundationTests`, `CloakSimTests`,
`MineVisibilitySweepTests`, `ProbeClusterVisibilityTests`,
`SideClusterVisibilityTests`, `ProbeBroadcastTests`, `ClusterWarningLiveTests`
— **49/49 passed**.

### Build & full suite

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`):
  **0 warnings, 0 errors.**
- Full suite via `scripts/run-tests.sh` (P471 sequential runner), **3 consecutive
  runs** — see the run-by-run table below. Only the **pre-existing documented
  live-wire load flakes** in `docs/TEST_FLAKES.md` appeared (each confirmed green
  in isolation); no P493-attributable failure. P493 adds 9 tests: Core 1,
  ClientCore 2, MonoGame 6 (running totals: Core 3695, ClientCore 403,
  MonoGame 1085).

#### Run-by-run (P493, `scripts/run-tests.sh`)

| run | Core | ClientCore | MonoGame | Lobby | flakes (all documented, green in isolation) |
|---|---|---|---|---|---|
| 1 | 3694/3695 | 402/403 | 1085/1085 | 12/12 | `LiveTreasurePickup…` (TEST_FLAKES.md P487 tbl); `ReadyCheckLiveTests…` (P188 ready-up race) |
| 2 | 3691/3695 | 403/403 | 1085/1085 | 12/12 | `AutoReloadOnEmpty…RELOAD129`, `WarpBombTests.Live_whole…`, `SideEliminationLiveTests…` (all in ledger) + `AddPartTests.Part_pickup…` (live-network test, no P493 refs, passes isolated — live-timing class) |
| 3 | 3693/3695 | 403/403 | 1085/1085 | 12/12 | `SideEliminationLiveTests…` (ledger line 39); `LoadoutChangeLiveTests…` (ledger follow-up #4 "LoadoutChange") |

Each flake reproduced above is in the `docs/TEST_FLAKES.md` ledger (except the
two named here explicitly as same-class live-timing observations) and passes in
isolation; **none references a P493 symbol**. The 9 P493-added tests
(Core 1, ClientCore 2, MonoGame 6) passed in all three runs.

## Files changed

- `src/Allegiance.Server/Server.Broadcast.cs` — `crossSideShipStatusSweep` Step +
  `SweepCrossSideShipStatuses` + `MakeCrossSideShipStatus`.
- `src/Allegiance.Server/Server.cs` — edge/per-recipient latch fields +
  `CrossSideStatusRefreshTicks`.
- `src/Allegiance.Server/Server.Lifecycle.cs`, `Server.Lobby.cs` — latch cleanup
  on disconnect / re-login.
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — corrected Unknown/Detected
  doc comments (P493 populates them now).
- `src/Allegiance.ClientCore.MonoGame/Rendering/SectorMapQueries.cs` —
  `KnownEnemySectors` (pure).
- `src/Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs` — known-enemy
  marker in `Draw` / `DrawMini`.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — wires
  `KnownEnemySectors` under the gate lock.
- Tests: `P493EnemySpottingLiveTests.cs`, `P493ClientWorldShipStatusTests.cs`,
  `SectorMapQueriesTests.cs` (+6).

## Out of scope (unchanged)

- Changing the visibility rules (P270/P476) — untouched.
- Alliances — the friendly leg collapses to own-side, the established port
  simplification.
- P374's `ClusterWarningUpdate` threat machinery and P375's live-telemetry
  audience gate — reused/complemented, not altered.
