# SHIP_UPDATE_CANDIDATE_LIST_P138.md

**Task:** P138 — the real selection mechanism for the LIGHT/HEAVY_SHIPS_UPDATE
family: a per-cluster ship-update candidate list.
**Branch:** `master` (started at `5dd82a9` P137; top now P138).
**Scope met:** investigation (correction confirmed) + smallest honest slice =
a per-cluster candidate-list builder on `Sector`, wired into nothing (foundation
slice, the same shape as P131/P132/P136/P137).

---

## 0. Headline: P136's breakdown was WRONG about this message's visibility model

P136's report (§7.2) and P137's "next task" framed P138 as *"extend the P132
cross-side sweep so foreign ships become per-side visibility targets …
Establishes `Ship.SeenBySide`."* Reading the real native candidate-list
construction **corrects that**: ship selection for this message family is
**cluster-membership only**.

- The candidate array is built straight from `pcluster->GetShips()`
  (`FedSrv.CPP:3727`) — the cluster's own ship list, **regardless of side**.
- There is **no `SeenBySide` check anywhere in the whole block**
  (`FedSrv.CPP:3738-4216` — confirmed by reading every line; the only gates are
  player-vs-drone, boarded-vs-not, the recent-client-update timestamp, and the
  turret routing).

This is a genuinely different visibility model than `STATIONS_UPDATE`/
`ASTEROIDS_UPDATE`/`PROBES_UPDATE` (P133-135), which *are* `SeenBySide`-gated.
The distinction is the whole reason this task exists. Consequently the P138
deliverable is **not** "extend the P132 sweep" — it is a **per-cluster candidate
list that does NOT consult `SeenBySide` at all**.

The same separation holds for broadcast shape: STATIONS/ASTEROIDS/PROBES are
**per-side** broadcasts of what that side can see; this message family is
**per-player** — each connected player gets their own assembled update (their
own target-lock takes the first heavy slot, `FedSrv.CPP:4141-4146`). Neither the
per-side sweep nor `SeenBySide` is the input this family consumes.

---

## 1. Investigation — what the real native mechanism does (re-read fresh)

All cited lines were re-read in
`/home/domingo/Development/Allegiance/src/FedSrv/FedSrv.CPP` during this task,
not trusted from any summary.

### 1.1 Candidate construction (`FedSrv.CPP:3723-3817`)

Loop per cluster: `pshiplist = pcluster->GetShips()` (`:3727`), walked at
`:3751`. For each ship:

| Case | Native test | Selection |
|---|---|---|
| Player, non-boarded | `IsPlayer() && GetParentShip()==NULL` | `ucLastUpdate==c_ucShipUpdate` **and** `dt∈[-1.5,1.5]` (`:3767-3776`) → added to `rgShips` |
| Player, boarded | `GetParentShip()!=NULL && GetTurretID()!=NA` | active-vs-inactive turret routing to *separate* lists (`:3779-3793`) — **not** `rgShips` |
| Drone | `!IsPlayer()` | `dt∈[-1.5,1.5]` (`:3809-3810`) → added to `rgShips` |

- **Cluster-scoped, any side**: the loop source is the cluster's own ship list;
  nothing in the block reads side or `SeenBySide`.
- **`c_cShipsPerSectorMax` cap** (declared arrays `:3738-3740`, assert `:3736`):
  a bandwidth-era fixed-array bound. A `static` reuse of a `[c_cShipsPerSectorMax]`
  buffer. The budget task (below) deals with counts; the cap itself is a native
  fixed-array constraint this port's unbounded `List<Ship>` drops honestly.

### 1.2 Priority sort & budget (`FedSrv.CPP:3919-3951`)

When not every candidate fits the heavy/light budget, `qsort(..., CmpShipDeviation)`
(`:3923`) orders by prediction deviation, then a heavy/light/turret budget of
2/2/2 per interval (`:3844-3846`) demotes the low-deviation tail to light/nothing
with a random jitter so no ship starves (`:3937-3951`).

### 1.3 Per-player assembly (`FedSrv.CPP:4029-4207`)

For **each flying player in the cluster** (`:4029`), a fresh message is
assembled: the recipient's own source-ship fractions/position populate the
scalar region (`:4044-4047, 4128-4133`); the recipient's command target, if a
ship with a fresh update, fills the **first heavy slot** (`:4081-4103,
4141-4146`); then the rest of `rgShips` (minus self, minus target) fill heavy to
`cHeavyUpdates`, overflow to light to `cLightUpdates` (`:4148-4170`). Heavy+light
are memcpy'd into one UDP frame (`:4185-4189`).

### 1.4 What the port already had (P136/P137)

`LightShipsUpdate`(175) + `HeavyShipsUpdate`(176) codecs with `FromShips`
builders that include **every supplied ship** (no budget/sort), and the typed
turret blobs. Nothing consumes them yet; there is no broadcast wiring and no
ship-source feeding them.

---

## 2. Simplification decisions (each stated with reasoning)

Per the task's discipline — nothing dropped silently:

| Piece | Decision | Reasoning |
|---|---|---|
| **Cluster-membership, not `SeenBySide`** | **BUILT as the load-bearing correction.** The builder queries this sector's model membership (`_models`), any side, no `SeenBySide` read. | It is the real native gate (`FedSrv.CPP:3727,3738-4216`); a `SeenBySide`-gated list would be a *different, wrong* visibility model. |
| **Recent-client-update timestamp gate** (`ucLastUpdate==c_ucShipUpdate` + `dt∈[-1.5,1.5]`, `:3767-3776`) | **SKIPPED.** Send every non-boarded flying ship's current state. | It is a bandwidth/throttling detail tied to the C→S `ClientShipUpdate` cadence; this server-authoritative port has no per-ship "a fresh update arrived this interval" concept, and its codec `FromShips` already "send[s] every supplied ship" (P136). |
| **Drone throttling** (address-hash frame schedule, `:3804-3805`) | **SKIPPED.** | The frame-hash throttle is **commented out in native itself** — current native already sends every drone each frame (`:3805` is disabled). The port has no player/drone subtype; sending every `Ship` is the faithful effective behaviour. |
| **Boarded-ship exclusion** (turret/observer routing, `:3779`) | **BUILT.** `ParentShip != null` ships are excluded from `rgShips` (they belong to the turret relay lists, P137 territory). | Direct native gate; unambiguous. |
| **Non-flying exclusion** (docked / dead) | **BUILT.** `StationId != null` and `Dead` ships excluded. | A docked ship is not physically in the cluster (absent from native `cluster->GetShips()`); the `Dead` guard mirrors the port's own rescue pass (`Sector.cs:592-597`) and is belt-and-suspenders over the death-contract removal (`Sector.cs:1105`). |
| **Target-lock priority** (recipient's target takes first heavy slot, `:4141-4146`) | **DEFERRED** to the broadcast task (P139). | It is a **per-recipient assembly** concern, not a candidate-list concern — the list is per-cluster and has no recipient. It can only exist once P139 loops per player. |
| **Deviation-sort budget** (qsort + 2/2/2 heavy/light/turret, `:3844-3923`) | **DEFERRED** to its own task (P141, named by P136). | Traffic-shaping heuristic orthogonal to selection; this port's scale doesn't need it. The list keeps native's unordered membership order (native's own pre-sort order is immaterial because it immediately qsorts). |
| **`c_cShipsPerSectorMax` cap / fixed arrays** | **SKIPPED** — unbounded `List<Ship>`. | A bandwidth-era fixed-array bound; this port's established preference is simple unbounded collections (cf. `SideVisibilityState` uses a `Dictionary`, not a fixed side array, P132 §4). |

Net: the only things **built** are the selection gates that are genuinely about
*which ships* are candidates; everything about *ordering/throttling/how a
specific recipient is served* is deferred with reasoning.

---

## 3. What was actually built (the slice)

| File | Change |
|---|---|
| `src/Allegiance.Core/Model/Sector.cs` | **`GetShipUpdateCandidates()`** — walks `_models`, returns every `Ship` with `ParentShip == null`, `!Dead`, `StationId == null`, any side, in stable membership order. The method's XML doc states the cluster-membership-not-SeenBySide correction and every gate decision with citations. Public and ready for P139's broadcast to consume. |
| `tests/Allegiance.Core.Tests/ShipUpdateCandidateListTests.cs` | 6 isolated-predicate tests (below). |

The method is deliberately **not wired anywhere** — no broadcast, no sim-loop
hook. Justification: the port's established foundation pattern ships slices
unwired and proves them with predicate tests (P131/P132/P136/P137 all did this),
and the only consumer is P139's broadcast wiring, which is its own task. Wiring
it early would be speculative and untestable until P139 exists.

### Tests

1. **Foreign ships with unseen `SeenBySide` flags are still candidates** — THE
   correction: a side-1 ship whose `SeenBySide(0)` is `false` is still in the
   list (no sweep ran in the isolated sector, so every cross-side flag is false,
   yet both sides' ships are selected).
2. **All non-boarded flying ships are candidates in stable order** — mixed-side
   trio selected wholesale; deterministic across two calls.
3. **Boarded ships are excluded** — a gunner on a `ParentShip` is dropped.
4. **Docked ships are excluded** — `StationId` set ⇒ not a candidate.
5. **Dead ships are excluded** — killed via the real damage path (no mission
   core ⇒ armor multiplier 1.0, knocked-down 0 shield ⇒ full leakage), dropped.
6. **Non-ship models excluded, no duplicates** — a real `Probe` in the sector
   is not a candidate; ships remain in stable order.

---

## 4. Verification

- Baseline re-confirmed first: `2678 passed / 0 failed` before any change
  (matches the P137 checkpoint exactly).
- New focused run: `6/6` `ShipUpdateCandidateListTests` pass.
- Full suite runs: **2684 passed / 0 failed** (2678 + 6 new). See commit
  message for the run log. One documented-timing flake fired once mid-sequence
  (`2683` with a single transient failure) and **did not reproduce on immediate
  rerun** (clean `2684`) — per the arc's standing rule it is a known
  `LiveChatSectorWireTests`/`LiveCombatWireTests`/`LiveInSpaceEjectTests`-class
  flake, not a P138 regression (none of the new tests touch timing/live wiring;
  they are deterministic predicates).
- No VM used (as required). No P103-P137 behaviour changed — the only prior-code
  edit is an additive method on `Sector` plus a new test file.

---

## 5. Concrete next tasks (updated breakdown)

P136's §7 stays largely intact; only P138's description is corrected:

1. **P139 — broadcast wiring (next).** A periodic per-cluster pass that calls
   `Sector.GetShipUpdateCandidates()`, assembles `LIGHT_SHIPS_UPDATE`/
   `HEAVY_SHIPS_UPDATE` via the P136/P137 `FromShips` codecs, and sends **per
   player** (this family is per-player, not per-side) — resolving the
   light-vs-heavy assignment policy (simplest honest default: every candidate
   gets a heavy record; optionally reintroduce the 2/2 budget in P141).
   Target-lock priority lands here too, since it needs the per-recipient loop.
2. **P140 — receive/decode on the peer.** Decode 175/176 in the receiving
   transport and apply to `Ship` instances (the counterpart to native
   `ProcessShipUpdate`).
3. **(optional) P141 — deviation budget.** Reintroduce the sorted heavy/light
   throttle if sector populations warrant it.

The candidate-list piece this task builds is the corrected, minimal input that
P139 consumes.

---

## 6. Session stats

Logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260910_060309_865e70`
(see shell history / commit log for the id used).
