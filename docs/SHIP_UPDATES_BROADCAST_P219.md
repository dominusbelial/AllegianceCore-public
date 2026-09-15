# SHIP_UPDATES_BROADCAST_P219.md

**Task:** P219 — the deviation/staleness-based heavy/light ship-update budget,
the follow-up P139 named (as "P141") but never built (a later, unrelated task
had already consumed that number in this project's real history). Per peer,
per tick, `Server.BroadcastShipsUpdate` now sends **at most
`cHeavyPerInterval + cLightPerInterval = 4`** ship records instead of *every*
same-cluster candidate, selecting them by each ship's accumulated staleness
(`Ship.Deviation`, the port of `CFSShip::m_deviation`).
**Branch:** `master` (no push — Domingo pushes).

---

## 0. Headline

Pre-P219, `BroadcastShipsUpdate` sent every candidate from
`Sector.GetShipUpdateCandidates()` (P138) as a full `HEAVY_SHIPS_UPDATE(176)`
record to every seated peer every tick — naively **O(peers²)** work/data per
tick. P139's own doc (§2) explicitly deferred this whole budget ("P139
explicitly deferred that whole budget to P141"). P219 ports the real native
algorithm: a fixed per-interval heavy/light quota chosen by a per-ship
staleness counter that implements a **fair round-robin** (ships that wait
longest rise to the front and get served first).

The port keeps the ≤ 2-candidate fast path byte-for-byte identical to pre-P219
(the small-session case must not change), folds the unwired LIGHT tier into
HEAVY (scope call, §2), and keeps the P139 target-lock promotion intact (it
consumes one heavy slot, §3).

Files touched:

| File | Change |
|---|---|
| `src/Allegiance.Core/Sim/Ship.cs` | new `Ship.Deviation` float (port of `CFSShip::m_deviation`) |
| `src/Allegiance.Server/Server.cs` | `BroadcastShipsUpdate` now applies the budget + staleness bookkeeping |
| `tests/Allegiance.Core.Tests/ShipUpdateBudgetLiveTests.cs` | 3 new live loopback tests (bounded / fair / target) |

---

## 1. Native algorithm (read directly, cited exactly)

The budget is at `FedSrv.CPP:3844-3950`, inside `AddUpdate`; the sort
comparator at `FedSrv.CPP:3614-3621`; the per-player assembly that consumes
the budget at `FedSrv.CPP:4141-4170`.

- **Fixed constants, not bandwidth-adaptive** (`FedSrv.CPP:3844-3846`):
  `cHeavyPerInterval = 2`, `cLightPerInterval = 2`, `cActivePerInterval = 2`.
  The whole adaptive sizing — `pfsPlayer->GetBandwidth()` (`FedSrv.CPP:3841-3843`)
  — is **disabled/dead code** in the reference checkout (the "pkk May 6th
  Disabled bandwidth patch"), replaced by the community "w0dk4 Bandwidth
  Patch" with these fixed values. We port the shipped values; the disabled
  adaptive code is never restored (§ "out of scope" in the brief and §5 here).
- **Small-candidate fast path** (`FedSrv.CPP:3860-3864`): `if (cShipUpdates <= 2)`
  → every candidate heavy, no sort, no budget. This port's pre-P219 behavior
  is exactly this path, so small/typical sessions are unchanged.
- **Deviation is a staleness counter, not a physics metric.** Despite
  `CmpShipDeviation`'s header comment ("deviation of what the actual position
  is and the current calculated position based on interpolation of inputs",
  `FedSrv.CPP:3606-3609`), the real implementation (`FedSrv.CPP:3937-3950`)
  never touches position: served ships get `SetDeviation(0)`; skipped ships
  get `SetDeviation(dev + random(0,0.25) + (0.70 if light-served else 1.00))`.
  `CmpShipDeviation` sorts descending so most-stale ships are served first —
  a fair round-robin queue, not physics. Ported as such.
- **Shared per-ship, not per-(viewer,ship).** Native's `CFSShip::m_deviation`
  (`fsship.h:65-66,231`) lives on the ship and is mutated by whichever
  player's assembly loop touches it that tick — peer A serving ship X resets
  the value peer B later reads the same tick. This cross-viewer coupling is a
  real simplification native has; ported exactly (see the `Ship.Deviation`
  doc).
- **Deviation reset on construction:** the field starts at `0.0f` (a fresh
  ship has never waited).

---

## 2. Port decisions (stated plainly)

### 2.1 Budget quota and light-tier fold

`BroadcastShipsUpdate` computes the per-tick quota exactly as the fixed
constants dictate, then:

- if `all.Count <= 2` (**native fast path**, `FedSrv.CPP:3860-3864`) — send
  every candidate heavy, unchanged; served ships get their deviation reset
  (native's unconditional reset pass, `FedSrv.CPP:3937-3940`);
- else sort the non-self, non-target candidates by `Deviation` descending
  (`CmpShipDeviation`, `FedSrv.CPP:3923`; this port uses a **stable**
  `OrderByDescending`, whose tie order is deterministic — native qsort's tie
  order is explicitly not load-bearing per the brief) and serve the front
  `cHeavyPerInterval` as heavy plus the next `cLightPerInterval` folded into
  heavy; everyone served is reset, everyone skipped gets `+1.00`.

**Why fold the light tier into heavy (option (a), not (b)):** this port has no
`LIGHT_SHIPS_UPDATE` wiring at all yet — P139 explicitly deferred it ("LIGHT
is never populated/sent," §2 of the P139 doc) and §5 of that doc named this
task as the natural home but the LIGHT *codec* exists while no emission path
does. The task's goal is **fixing the O(peers²) cost**, not wire fidelity to
native's two-tier message; sending the light tier as heavy still bounds the
total per peer per tick to `cHeavyPerInterval + cLightPerInterval = 4`, which
achieves the bound with a much smaller diff. Real `LIGHT_SHIPS_UPDATE(175)`
emission is recorded as named follow-up (§5). The fold also cleanly removes
native's lighter `+0.70` increment (which compensates for light updates being
cheaper/interpolated): here everything served is a full heavy update, so the
only skip-increment is the deterministic `+1.00`.

**Deterministic increment, no live RNG:** native adds `random(0, 0.25)` jitter
per tick (likely just to de-tie simultaneous stale ships). This port's
established convention (server-authoritative, testable — cf. `Starfield.cs`'s
`seed`, `RockMeshFactory.cs`'s fixed-seed hash) prefers determinism, and the
jitter magnitude is not gameplay-load-bearing (the brief's recommended
default). The port drops the jitter entirely and uses a fixed `+1.00` per
skipped tick; the stable sort provides the tie-break that native's jitter was
papering over.

**Light-count simplification:** native's `cLightUpdates = (cShipUpdates *
cLightPerInterval) / cShips` (`FedSrv.CPP:3870`, bounded by `cShipUpdates -
cHeavyUpdates`) converges to `cLightPerInterval` (2) at realistic cluster
sizes, so the fixed quota is a faithful, simpler stand-in — noted in the
code comment.

### 2.2 Target-lock composition (checked at `FedSrv.CPP:4141-4146`, not guessed)

Native promotes the target **unconditionally** as the first heavy entry
(`if (pshipTarget) AddUpdate(..., pHeavy++)`) *before* iterating the sorted
candidates, and reserves that slot out of `cHeavyUpdates`. This port mirrors
it: the promoted target is always added as the first entry regardless of its
own deviation, and the remaining candidates compete for `cHeavyPerInterval -
1` (target present) + `cLightPerInterval` slots. Net: target + up to 3 others
= 4 total per tick, target never starves, budget still holds.

### 2.3 What does NOT change

- `Sector.GetShipUpdateCandidates()` (P138) — candidate *selection* untouched.
- Target-lock promotion logic (P139) — composed with, not rewritten.
- Turret active/inactive relay lists (`ActiveTurretUpdates`) — still deferred
  (P139's reason stands: no stored per-ship turret update to relay).
- The `HEAVY_SHIPS_UPDATE(176)` wire format — unchanged; only *which* ships
  are included changes.
- Anything client-side.

---

## 3. Verification

### 3.1 Correctness — live loopback tests

New `ShipUpdateBudgetLiveTests` (one seated bot peer + 5 ownerless NPC ships
in the same cluster, so only the single peer's per-player loop mutates the
shared deviations — a clean rotation), each of the three required properties:

1. **Bounded** (`Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count`):
   6 candidates (peer + 5 NPCs) → each peer's `HEAVY_SHIPS_UPDATE` carries
   **exactly 4** entries every tick, never the 5 "other" ships.
2. **Fairness** (`Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation`):
   exactly one of the 5 is dropped per tick; the dropped ship is served the
   **next** tick (its +1.00 bump rose it to the front), and every ship is
   served within the window — no permanent starvation.
3. **Locked target** (`Locked_target_is_always_included_regardless_of_its_deviation`):
   a peer's `CommandTarget` is in **slot 0 of every** update, exactly once,
   while the budget still caps total entries at 4.

All 3 fail when the budget is removed (servedLimit forced unbounded — the
P-series "must fail if removed" proof): bounded test sees 5 entries, fairness
test finds no dropped ship, target test sees 5 entries. Reverted clean.

The 3 existing P139 `ShipsUpdateLiveTests` pass **unchanged** (they use ≤ 2
candidates → untouched fast path, confirmed directly, not assumed).

### 3.2 Performance — real measured before/after (honest result)

Repeated this session's actual 150-peer load test: a real `Allegiance.Server`
at 60 upd/s on this box, 150 concurrent `Allegiance.Bot` peers (all in
`Sectors[0]` → worst-case same-cluster candidates), server CPU sampled from
`/proc/<pid>/stat` over 8 s:

| Configuration | avg %CPU-of-one-core |
|---|---|
| **Before P219** (unbounded: every candidate heavy, 149 entries/peer) | **105.7%** (1.06 cores) |
| **After P219** (budget: 4 entries/peer) | 107.2% (1.07 cores) |
| Control: `BroadcastShipsUpdate` **disabled entirely** | 107.4% (1.07 cores) |
| Sanity: 20 bots @ 60 (broadcast on) | 10.7% (0.11 cores) — matches the P139-era note "20 peers @ 20 Hz sat at 10.7%" |

**Honest finding:** the per-peer entry count drops ~37× (149 → 4), but the
server's **CPU does not drop**, because in this loopback scenario the
ship-update *encode* is **not** the CPU driver — the control run proves it:
disabling `BroadcastShipsUpdate` *entirely* leaves CPU at ~107%, and the 20↔150
scaling (10.7% → ~106%) tracks ship **simulation** and the per-peer state
broadcast/ingress work, not the HEAVY record encoding. So the P219 budget's
real value is **capping per-peer-per-tick ship-update data volume ~37×** (the
network/client-scalability fix that must exist before a real 150-player game
can run), and it does so with provably correct composition — but the ~110%
CPU ceiling at 150@60 this session measured is dominated by ship physics /
per-peer state, i.e. a **different** hot path than the one P219 touches.

The task brief's "leading suspect" (BroadcastShipsUpdate) was therefore not
borne out by measurement. The budget is still landed exactly as P139 intended
(it is the correct, faithful port of the native algorithm and the necessary
traffic-shaping fix), with the CPU bottleneck flagged as a follow-up (§5).

Full suite: baseline **3034 total (3033 pass / 1 documented flake)** →
**3037 total** after the 3 new tests. Three consecutive clean runs performed
before commit (the one documented `LiveTurretGunnerBindingTests` machine-state
flake aside, per the established convention).

---

## 4. Native citations (read in source)

| Port fact | Native |
|---|---|
| `Ship.Deviation` = `CFSShip::m_deviation` | `fsship.h:65-66,231` |
| `CmpShipDeviation` (descending) | `FedSrv.CPP:3614-3621` |
| Fixed quotas `2/2/2` (w0dk4) | `FedSrv.CPP:3844-3846` |
| Disabled bandwidth-adaptive sizing | `FedSrv.CPP:3841-3843` (dead code) |
| Small-candidate fast path `<= 2` | `FedSrv.CPP:3860-3864` |
| Light-count formula `(n*2)/cShips` | `FedSrv.CPP:3870` |
| Sort candidates by deviation | `FedSrv.CPP:3923` |
| effectiveHeavy / effectiveLight | `FedSrv.CPP:3931-3935` |
| Deviation reset / increment | `FedSrv.CPP:3937-3950` |
| Target promoted first, consumes a heavy slot | `FedSrv.CPP:4141-4146` |
| Assembly loop skips self & target | `FedSrv.CPP:4148-4154` |

---

## 5. Remaining work (named concretely)

1. **Real `LIGHT_SHIPS_UPDATE(175)` emission (option (b) from the brief).** The
   codec exists (P139 §2: "the LIGHT codec ... are already in place"); wiring
   the emission + the light-tier `+0.70` increment is a clean follow-up that
   this task deliberately deferred to keep the diff minimal. The budget shape
   already accommodates it.
2. **The actual 150@60 CPU ceiling.** Measurement shows the ship-update
   encode is *not* the driver (control run: broadcast disabled ≡ same CPU).
   The ~1-core sim-thread saturation at 150 peers tracks ship simulation and
   per-peer state broadcast — a profile of that hot path is the natural next
   perf task, not P219's budget.
3. **(Recorded limitation)** native's per-player bandwidth-adaptive sizing
   (`pfsPlayer->GetBandwidth()`) is dead code that never shipped and is not
   reintroduced — this port has no per-player bandwidth concept.

---

## 6. Session stats

Logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260915_150027_f2f716`
(the still-running session at commit time; 114 tool calls / ~11.75M tokens in /
~64K out at the time of writing).
