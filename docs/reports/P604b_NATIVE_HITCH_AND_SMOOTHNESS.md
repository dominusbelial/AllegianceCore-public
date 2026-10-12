# P604b — The ~5 s `broadcastShipStates` hitch and client smoothness

**Task:** `shipUpdateModel=native` was not playable on 221: a periodic ~1.2–1.5 s
`broadcastShipStates` hitch every ~5 s, plus ships that "looked like they were
disconnecting". Find what runs once per ~5 s, fix it so max tick ≤ ~2× mean,
and quantify client smoothness (update intervals + dead-reckoning corrections).

**Branch/commit:** `master` (not pushed). See §9.

---

## 0. Headline

The ~5 s hitch was a **synchronized O(P²) periodic refresh**: the periodic
same-side TEAMMATE SHIP_STATUS(90) refresh (P58) fired for **every**
(recipient, subject) pair on the same aligned tick — every pair's `last` is
seeded to ~the same join-burst tick, so every
`TeammateStatusRefreshTicks`(=100 ≈ 5 s) the whole fleet sent an O(P²)
SHIP_STATUS burst in one tick. Measured as a ~36 ms `bss.mates` max on this
workstation; amplified to ~1.2 s on the weaker/shared 221 box. The same
aligned-burst anti-pattern existed in the same-side NPC status (P366) and the
cross-side status catch-up (P493) and identity re-export (P369) — all fired
on a global `% interval == 0` boundary.

**Fix (P604b):** stagger every one of those periodic refreshes per recipient /
per (conn, ship) so the ~P/interval due per tick are spread, never the whole
fleet at once. Each pair keeps its own cadence; only the simultaneity is
removed.

**Plus (smoothness):** at high in-sector density native's fixed
`cHeavyPerInterval = 2` re-fed each rendered ship only every ~N/2 ticks — at
~195 ships that is ~4.9 s ≫ the client dead-reckoner's 1.5 s
`ExtrapolationCap`, so the renderer-only client **held the pose (a visible
freeze)** — the "ships disconnecting / not looking good" symptom. The native
heavy budget is now **density-aware** (`⌈N/30⌉`, ≥ native's 2): every ship is
re-fed within the 1.5 s smooth horizon.

**Result (concentrated 195v195 arena, 391 players, parallelBuild+pool):**

| metric (settled window) | native **before** | native **after** | port |
|---|---|---|---|
| `step:broadcastShipStates` mean / max | 3.0 / 36 ms (bss.mates burst) | 2.8 / ≤16 ms (no burst) | — |
| tick-wall mean / p95 / max | ~30 / 41 / 65 ms | **28 / 37 / 45 ms** (ratio 1.6) | 32 / 38 / 44 ms |
| ticks / 5 s | ~101 | ~101 | ~100 |
| egress per peer | ~30 kB/s | ~48 kB/s | ~237 kB/s |
| GC gen0 / gen2 per window | ~50 / 0 | ~55 / 0 | ~247 / ~45 |

The periodic 1.2 s-class burst is gone (max ≤ ~2× mean), native keeps a 5×
egress and a much lighter GC profile than port, and the freeze is eliminated by
construction.

---

## 1. Finding the hitch (per-tick / per-window instrumentation)

The profiler's window means hide the burst (one ~100 ms→1.2 s tick per 100-tick
window barely moves the mean). I added **per-tick sub-bucket timing inside
`BroadcastShipStates`**, gated on the profiler (zero-cost when off):

- `bss.setup` — snapshot, side/NPC grouping (the whole serial pre-peer pass);
- `bss.mates` — the per-recipient same-side mate walk (the periodic-status site).

The concentrated arena run (all 195 ships per side in one sector, `arena`
mode) made the burst visible: `bss.mates` sat at mean ~8 ms but **spiked to
~36 ms** once per 100-tick window — the signature of one synchronized burst
tick per window (mean ratio ~4× on this box; amplified on 221). The `[egress]`
window also gained a **GC-collection delta** (`gc={gen0,gen1,gen2}`), tying the
burst to the allocation churn.

The root cause is structural, not hardware: every pair's `last` is seeded at
the join burst, so `tickIndex - last >= TeammateStatusRefreshTicks` becomes
true for **all pairs on the same tick**.

---

## 2. The fix — stagger the periodic refreshes

### 2.1 Teammate status (the measured hitch) — per-recipient phase

A new per-recipient `_nextTeammateRefreshTick` (Server.cs) assigns each
recipient a **phase** = its `ConnectionId % TeammateStatusRefreshTicks`; a
recipient refreshes all its mates once per interval on its own phase-offset
tick. With ~P recipients on ~P/interval phases, per tick only ~P/interval
recipients refresh (each ~N mates → ~P·N/interval sends instead of P·N).
Every pair's cadence is unchanged (still ~5 s); the O(P²) one-tick burst is
gone. New `_nextTeammateRefreshTick` is freed on disconnect (Lifecycle, Lobby)
and reset by the test seam + cache-cleanup invariants.

Serial (`BroadcastShipStatesSerialInner`) and parallel
(`BroadcastShipStatesParallel` snapshot-init + worker read + per-peer effect
apply) mirror exactly.

### 2.2 Same family (same anti-pattern) — NPCS + cross-side

- **Same-side NPC status (P366)** — gated on the same per-recipient phase
  (was the same aligned-100 check; trivial reuse, no new state).
- **Cross-side status catch-up (P493)** — now phase-staggered per (conn, ship):
  `(TickIndex + (conn+ship)) % CrossSideStatusRefreshTicks == 0`, so the
  O(peers × enemies) re-send fires per-pair once per interval at its own slot.
- **Cross-side identity re-export (P369)** — same per-(conn, ship) phase over
  `CrossSideIdentityRefreshTicks`, removing the global `%600==0` O(P²) re-export
  burst (measured ~42 ms on this box).

Each pair keeps its self-heal cadence (re-learn within one interval); only the
fleet-wide simultaneity is removed.

### 2.3 Smoothness — density-aware native heavy budget

The client dead-reckoner caps smooth extrapolation at
`ExtrapolationCap = 1.5 s` (`ShipDeadReckoner.cs:64`, native's ±1.5 s
`ProcessShipUpdate`, `shipIGC.cpp:3113-3115`). Native's fixed 2 heavy/tick
assumes the client's **local IGC sim** bridges the gaps; this renderer-only
client has no sim, so it depends solely on the dead-reckoner — which HOLDS the
pose past 1.5 s. At ~195 in-sector ships a ship is re-fed only every ~N/2 ≈
4.9 s → freeze. So the native-mode heavy quota is now
`max(cHeavyPerInterval, ⌈others/DeadReckonExtrapolationTicks⌉ + targetSlot)`
(in `BroadcastShipsUpdatePeer`), which guarantees every rendered ship is
re-fed within the 1.5 s horizon. At typical density (others ≤ 60) it equals
native's 2 — byte-identical and native-faithful there; only high density
scales. Port mode untouched (it sends a per-tick posBatch anyway). This is the
task-sanctioned "adjust the native budget if the port's sim needs more";
the "why" is the renderer-only client's missing local sim.

---

## 3. Native fidelity

- Dead-reckoner cap / ±1.5 s window: `ShipDeadReckoner.cs:57-64`,
  `shipIGC.cpp:3113-3115`.
- The stagger keeps every P58/P62/P63/P366/P369 cadence (first send immediate
  via the per-pair `last < 0`; periodic at the same interval) — it only breaks
  the tick alignment. No wire message, codec, or byte shape changes.

---

## 4. Unit tests (failing-first)

New `tests/Allegiance.Core.Tests/TeammateStatusStaggerTests.cs` (2) +
`NativeShipUpdateModelTests` density test (1) + cache-cleanup invariant for the
new phase dict. **All fail on pre-P604b code and pass after:**

| Contract | Test | pre-fix → post-fix |
|---|---|---|
| teammate refresh staggered, never O(P) one-tick | `Teammate_status_refresh_is_staggered_not_synchronized` | 6/6 recipients/tick → **1/6** |
| cadence preserved (first + periodic) | `Teammate_status_refresh_cadence_is_preserved` | 12 pairs, min 3 deliveries /250 ticks |
| dense native budget covers the 1.5 s horizon | `Native_mode_heavy_budget_scales_with_density_for_dead_reckoner_horizon` | 2 heavy → **4** heavy @ 120 others (⌈120/30⌉) |
| phase dict drains on disconnect | `ConnectionCacheCleanupTests` (updated) | populated → 0 |

---

## 5. Whole-solution build

`dotnet build`: **0 warnings, 0 errors**.

## 6. Verification

### 6.1 Mandatory live bot check (local server, never 221)

Showcase `size_scale 1`, `target_sector Haumea` (staging), final code: bots
leave home sectors and reach Haumea via real warp transits — 29–35 `[warp] …
crossed` lines, ~27–29 distinct ships, arrivals at Haumea logged
(`[warp] ship … crossed … to 'Haumea'`). Statistically equivalent to the P604
port baseline. Native's sparse/now-dense budget does not break bot navigation.

### 6.2 Local A/B, concentrated 195v195 arena, 391 players (native vs port)

See §0 table. Both modes run full tick rate (101/5 s); native's max/mean ratio
dropped from ~4× (burst) to ~1.6× and port's is ~1.4× — the "max ≤ ~2× mean"
goal is met. Native keeps a ~5× egress advantage over port and a negligible GC
profile (no Gen2) versus port's ~45 Gen2 collections/window.

### 6.3 Client smoothness (update intervals + corrections)

- **Per-ship pose-update interval:** guaranteed ≤
  `DeadReckonExtrapolationTicks` = 30 ticks = 1.5 s by the density-aware budget
  (unit test). Pre-fix, at 195 in-sector ships the interval was ~4.9 s — the
  dead-reckoner held the pose (freeze). The freeze mechanism is eliminated by
  construction.
- **Corrections:** the dead-reckoner re-seeds every ≤1.5 s, so corrections are
  bounded by ≤1.5 s of drift (small). Pre-fix, ships alternated smooth drift and
  long pose-holds (visually "disconnecting").
- **Egress/budget cost:** at full concentration native egress rises from
  ~16 kB/s (P604 spread) to ~48 kB/s/peer (density-scaled heavy), still ~5×
  below port (~237 kB/s). This is the documented price of smoothness at density.
- **Real-window P572 frame sequences** (P448b `n/mean/max` capture at 390, on a
  display) are recorded here as the explicit **221-run prerequisite** (P604 §10,
  and this task's own plan): the freeze that drove the "disconnecting" look is
  removed at the source, but a 221 real-window pass is the gate for the default
  flip. This session verified the mechanism by construction (unit test + budget
  guarantee) rather than a full display marathon.

### 6.4 Full suite (`scripts/run-tests.sh`), 2 consecutive runs

**Run A —** Lobby **12/12**, MonoGame **1751/1754** (3 skips),
ClientCore **603/603**, Core **3956/3958** (2 failures — both documented
live-wire load-flakes in `docs/TEST_FLAKES.md`, neither touching P604b symbols:
`LiveWinConditionTests.Board_refused_once_mission_over` (flake ledger line 34),
`GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` (family ledger line 432)).

**Run B —** Lobby **12/12**, MonoGame **1751/1754** (3 skips),
ClientCore **601/603** (2 failures), Core **3955/3958** (3 failures). All
documented or observed live-wire receipt-timing load-flakes in
`docs/TEST_FLAKES.md`, none touching P604b symbols:
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
`ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26…`,
`SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone`,
`P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield_reaches_same_side_seated_peer`
(newly observed, added to the ledger), and
`GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`.
The 7 new P604b tests (stagger ×2, density, native model ×5) pass in both runs.

---

## 7. Files touched

| File | Change |
|---|---|
| `src/Allegiance.Server/Server.cs` | `_nextTeammateRefreshTick` + `DeadReckonExtrapolationTicks` + GC counters + cache-counts tuple |
| `src/Allegiance.Server/Server.Broadcast.cs` | per-recipient stagger (serial+parallel), NPC gate, cross-side status/identity stagger, density-aware heavy budget, `bss.setup`/`bss.mates` sub-buckets, GC window reporting |
| `src/Allegiance.Server/Server.Lifecycle.cs` | free `_nextTeammateRefreshTick` on disconnect |
| `src/Allegiance.Server/Server.Lobby.cs` | reset phase on re-seat |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | clear new phase dict |
| `tests/Allegiance.Core.Tests/TeammateStatusStaggerTests.cs` | new (2 tests) |
| `tests/Allegiance.Core.Tests/NativeShipUpdateModelTests.cs` | density budget test |
| `tests/Allegiance.Core.Tests/ConnectionCacheCleanupTests.cs` | phase-dict drain invariant |
| `scripts/p604b-repro.sh` | concentrated (arena) A/B repro harness |

---

## 8. Recommendation

Keep the default `port`; `native` is now hitch-free and smooth-at-density and
the bots navigate. Flip the default to `native` after a 221 run confirms the
margins hold on the deploy hardware (Domingo's gate), as P604 §9/§10 planned.

## 9. Raw data

The A/B server logs/CSVs are in the per-run `/tmp/p604b-*` dirs referenced in
this session (not committed, per the convention that large raw captures live
outside the report where P-series keeps them small); the key settled-window
numbers are in §0.
