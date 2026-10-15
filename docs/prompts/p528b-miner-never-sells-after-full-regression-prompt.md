# P528b — REGRESSION: after P528, miners fill to native capacity and then never leave the rock (no sell leg) on 221's map

## Why this task exists

P528 (`942e1dd4`) moved He3 mining to native values: rate `GA[17]` ≈ 0.8 ore/s
instead of 100×, capacity `FloatConstants[7] × GA[19]`, price
`FloatConstants[8] × GA[18]`. Its headless proof on `training_1.igc` showed a
full mine → sell cycle (IC miner full in 78.2 s, sold 67.5 @ 80 = 5,400).

**Live on the 221 test server (deployed `942e1dd4`, 2026-10-04,
`docker/compose.server.json`: map `m2xspiderw.igc`, `civIds [25,3]` = IC vs
GT, `liveSettings.testUnlock {enabled, allTech, money:1000000}`):**

```
17:20:25.30 [mine] ship 8096 began mining asteroid 3194 'He1051' (ore 7992/7992)
17:21:15.74 [mine] ship 8097 began mining asteroid 3195 'He1052' (ore 11691/11691)
17:21:44.61 [mine] ship 8096 is full (67.5/67.5 ore) — mining paused     ← 79.3 s, correct
17:23:08.05 [mine] ship 8097 is full (90/90 ore) — mining paused         ← 112.3 s, correct
… then NOTHING for 25+ minutes: no [warp], no [npc] decision line, no [mine-sell], no exception.
```

**Before P528, on the same server/map/civs** (build `5f0a8156`), the same two
initial miners filled (in ~1.5 s), then **immediately warped home** and sold:
`8096 full 15:56:22 → [warp] Alderan→Plato→Ganymede → [mine-sell] 15:59:55 →
[npc-autolaunch] 16:00:05 → back to Alderan → full again` (and likewise for
8097 via Juniper → Organia). The sell leg there crosses **2–3 sectors** (the
mining field is Alderan, sector 1052; the stations are Garrison 4328 in
Ganymede 1050 and Garrison 4329 in Organia 1051). In P528's own proof
(`training_1`) the station was close by.

**What Claude already checked (don't redo, but do verify if you rely on
it):**
- The miner AI uses the new capacity everywhere:
  `src/Allegiance.Core/Sim/Ship.cs:5731` (`DecideMiningOrSelling`: `Ore <
  EffectiveMineCapacity()` → mine, else sell, `:5755` log "full … selling at
  station", `:5761` `LogNpcStuckOnce` "no unload-capable friendly station
  reachable"), and `:6845` (miner cycle: clear `MiningAsteroidTarget` once
  `Ore >= EffectiveMineCapacity()`, then call `DecideMiningOrSelling()` only
  when `MiningAsteroidTarget is null && StationId is null && GotoWaypoint is
  null`).
- Accrual and cap: `src/Allegiance.Core/Model/Sector.cs:3643` (skip if full),
  `:3670-3683` (clamp to cap, "is full" log).
- The sell happens on dock: `src/Allegiance.Server/ServerHost.cs:775-800`.
- No exception in the server log, and **no `[npc]` line at all** for 8096/8097
  after "full". So the decision either never runs, or runs into a silent
  path (`LogNpcStuckOnce` latches after one print).

**Hypotheses (rank them by evidence, don't assume):**
1. With mining now ~80–112 s instead of ~1.5 s, some *other* state gets set
   during the long dwell (`PlanTarget`/`AcceptedTarget` promotion,
   `GotoWaypoint`, take-cover, the P151 Plan-slot logic at `Ship.cs:~6810-6827`,
   asteroid contention, or `IsMining`/`MiningAsteroid` bookkeeping), so the
   `:6845-6853` idle gate never opens.
2. The cross-sector station search / path fails (latched silently by
   `LogNpcStuckOnce`), e.g. a reachability or unload-capability check that
   depends on a value P528 changed.
3. A float-equality edge: `Ore` vs `EffectiveMineCapacity()` (with
   `Asteroid.MineOre` throttling/rounding, `asteroidigc.h:221-251`) leaves
   `Ore` a hair under the cap. Then mining is paused, but the cycle's `>=`
   check never fires.
4. Something else you find.

**Native reference for the intended flow:** `CshipIGC` mining → full →
`FindTarget(… c_ttFriendly | c_ttStation | c_ttNearest | c_ttAnyCluster |
c_ttCowardly … c_sabmUnload)` (`~/Development/Allegiance/src/Igc/shipIGC.cpp:2205-2235`)
and the offload + `PickDefaultOrder` at `:1978-1992`.

## Scope

1. **Reproduce headless first**, faithful to 221. Use an in-process server
   or sim with **`m2xspiderw.igc`, civs 25 vs 3, the test-unlock live
   settings**, and the initial miners at native rates. Confirm the stuck
   miner and capture the ship's relevant state at the moment it's full and
   for several seconds after: `Ore`, cap, `MiningAsteroidTarget`, `IsMining`,
   `PlanTarget`, `AcceptedTarget`, `GotoWaypoint`, `StationId`, the decision
   branch taken.
2. **Find the real root cause**, cite file:line, and fix it minimally,
   faithful to native's flow above.
3. If the root cause is a silent latch, make the stuck path **visible**
   (log the reason at least once per state change) as part of the fix.

## Explicitly out of scope

- Re-tuning any P528 constant (rate, capacity and price are correct and
  live-verified).
- Asteroid lighting (P529) and anything client-side.
- Miner routing quality beyond "goes to the nearest unload-capable friendly
  station and sells".

## Verification

- A **failing-first** test that reproduces the stuck-after-full miner (with
  a cross-sector sell leg, the 221 shape, and native mining rates, or the
  real `m2xspiderw` map if feasible in a test). It must fail against
  `942e1dd4`.
- A **live headless proof** on `m2xspiderw.igc` with IC vs GT: each initial
  miner completes **two full cycles** (began → full → warp(s) → `[mine-sell]`
  with the P528 amounts → relaunch → began again). Quote the log lines with
  timestamps in the report.
- Keep all P528 tests green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs **on the final code**. Cite `docs/TEST_FLAKES.md` for any remaining
  flake. **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P528b_MINER_SELL_REGRESSION.md`: the repro, the
   root cause with file:line, the native citation, the fix, test results and
   the live proof lines.
