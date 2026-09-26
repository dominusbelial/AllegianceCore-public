# P316 — Ship-id wraparound & pre-validation id burn on LOGONREQ

## Relationship to the review

Finding **H4** (ship-id space wraps after 65,536 allocations; `LOGONREQ`
burns an id before validation — a script sending 65,536 raw LOGONREQs causes
id collisions with live ships) in
`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`.
Interacts with finding **M1** (never-pruned `_teammateExportSent` cache): a
reused id stale-hits the cache, so a newly seated teammate is silently never
exported to some peers. M1's cache cleanup is P322 (explicitly out of scope
here); this task prevents the *duplication at allocation time* instead.

## The bug (two parts)

1. `Server.cs — LogonRequest dispatch` ran `short shipId = (short)(_nextShipId++);`
   for **every** LOGONREQ, valid or not, **before** the `validated` check — so
   an unauthenticated flood of LOGONREQs burned ids with no logon required.
   `Mission.cs — NextObjectId()` had the same unchecked `(short)` wraparound
   for projectiles/missiles/mines/probes/treasures/chaff.
2. The 16-bit id space wraps after 65,536 allocations; once ids repeat a newly
   seated player can receive an id still held by a live ship →
   `FindConnectionOfShip`/`FindSameSideShip` return the wrong ship for
   `POSITIONACK`/`ORDER_CHANGE`/boarding, and the never-pruned
   `_teammateExportSent` cache stale-hits on the reused id.

## Wraparound strategy chosen, and why

**Chosen: option (b) — detect a still-live id at allocation time and skip
forward past it.**

**Why not (a) widen the id space:** the real wire field width is 16 bits.
Native `Igc/igc.h:477` `typedef short ObjectID;` and `igc.h:480`
`typedef ObjectID ShipID;` — and every ported codec encodes ship/object ids as
`short`/`WriteI16` (e.g. `AddPart.cs`, `OrderChange.cs`, `HeavyShipsUpdate.cs`).
Widening to a 32-bit id would change every message on the wire and break
compatibility with the native client, so (a) is not possible without a
breaking protocol change (which is explicitly out of scope). Hence the only
correct fix is collision-avoidance at allocation time.

## Implementation

- `Server.AllocateShipId()`: walk `_nextShipId` forward (mod the 16-bit
  `short` space), returning the first id **not** currently held by a live ship;
  `IsShipIdLive(short)` treats as live: every seated peer's ship
  (`_peerShips`), every peer still awaiting side-select (its
  `PendingSideSelect.ShipId` is reserved even with no `Ship` object yet), and
  every NPC ship placed in a mission sector (P147). Used by BOTH the
  LOGONREQ path and `SpawnNpcShip` (same counter / same id space).
- `Mission.NextObjectId()`: identical skip-forward strategy against live
  models in the mission's sectors; sequence is unchanged when nothing
  collides (verified by unit test).
- **LOGONREQ order fix:** id allocation moved to **after** the `validated`
  check, and only when `_missions.Count > 0` — an unauthenticated/invalid
  LOGONREQ no longer burns any id.
- Both loops have an escape hatch: if the entire 16-bit space were somehow
  occupied they hand out the last visited value instead of spinning forever
  (effectively unreachable; defensive only).

## Proof performed

- **Live (in-process server + real bot), required:** `Invalid_logon_flood_does_not_advance_nextShipId`
  — boots a real server, floods 300 invalid (wrong-version) LOGONREQs, and
  asserts `_nextShipId` is unchanged; a separate `Valid_logon_advances_nextShipId`
  is the positive control (a valid logon still consumes an id, proving the
  reflection read/write is sound and allocation wasn't frozen).
- **Live collision-skip:** `Allocated_ship_id_skips_an_id_already_reserved_by_another_peer`
  — bot A logs on validly and stays pending (its id X is reserved in
  `_pendingSideSelect`, i.e. already "in use"); parks `_nextShipId` exactly on X,
  then a valid logon for bot B is handed X+1 (never X). Using the reservation
  window (rather than the slower full seating handshake) keeps the test
  deterministic under the box's live-test CPU contention.
- **Focused unit test, small reachable id space:** `NextObjectId_skips_live_models_across_the_wraparound_boundary`
  — builds a real `Mission.Assemble` over the corpus, places live models on
  the ids straddling the signed-16-bit wrap ((-2, -1, 0) = `(short)`65534/65535/65536)
  with `_nextObjectId` parked at 65534, and asserts `NextObjectId()` returns 1
  then 2 — the wrap is made reachable in four allocations instead of 65,536
  real ones.
- **Must-fail-if-removed (hard requirement):**
  - Reverting the LOGONREQ order fix → `Invalid_logon_flood...` goes RED.
  - Neutralizing `IsObjectIdLive`/`IsShipIdLive` (return false) → both
    collision-skip tests go RED (Server skip + Mission wraparound).
  All restored afterward; only the two documented pre-existing flakes expected.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- New tests: 5/5 pass in isolation (and in every full-suite run — none of the
  five has ever failed in a full run; only the original pre-hardening seated
  variant flaked once in run 1 and was replaced by the deterministic
  reservation-based version).
- Full suite (baseline 3248 before P316, 3253 with the 5 new tests) run **three
  times**: the only consistently-failing test is the pre-existing
  `LiveTurretGunnerBindingTests` timing flake; the remainder are the box's
  known rotating environmental live/timing flakes (e.g. `LiveInSpaceEjectTests`,
  `LiveWinConditionTests.Multi_mission_disconnect`, and miscellaneous mining /
  lobby / station-visibility live tests that flap under the box's CPU
  contention and are green in isolation with zero references to the P316
  symbols). No P316 test and no P316-attributable failure appeared in any run.

## Scope boundary / exclusions

- Out of scope, per task: connection-scoped cache cleanup on disconnect
  (M1 → P322); the two CRITICAL freeze bugs and the turret-slot loop.
- Did not touch native reference source (`~/Development/Allegiance` — read-only).
- The fix belongs at allocation time; no downstream consumers of duplicate
  ids were modified (preventing the duplication in the first place is the fix).
