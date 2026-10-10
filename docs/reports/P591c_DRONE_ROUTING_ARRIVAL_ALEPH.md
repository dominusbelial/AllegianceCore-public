# P591c — Drones no longer turn back toward the aleph they just arrived through (native routing root-cause fix; P591b guard re-labelled as a non-native safety net)

**Branch:** `master` (not pushed). **Commit:** `b9415a59` (amended with suite numbers).

## TL;DR

Our drones (and bots/pods — they share the same autopilot) sometimes turned back
and re-crossed the very aleph they had just arrived through, ping-ponging between
two sectors forever. P591b had stopped the symptom with a **re-entry guard**
(`LastArrivalWarpId`, refuse to re-transit the arrival warp until clear) and
claimed native "holds `m_warpState` until it clears". **Both the mechanism and
the attribution were wrong**, and this task found and fixed the real root cause.

**Root cause (measured, real server + `m2xspiderw`):** the port's shared steering
(`Ship.SteerToward`) applied its arrival-brake distance ramp to **every** goal —
including an **intermediate warp** on a cross-cluster leg. Native `GotoPlan`
flies a ship **through** a warp at cruise speed ("Hit the front: go through",
`shipIGC.cpp:629-636`) and only decelerates for the FINAL waypoint. Because our
drones braked to ~0 before every warp, they transited at ~0, exited with the weak
push `backward·(0+15)` (`fsship.cpp:126`), could not clear the (large) destination
aleph's transit volume within native's ~2 s `m_warpState` re-arm
(`fsship.cpp:1185-1191`), and re-crossed the aleph they had just arrived through.

**Fix (native-faithful):** `SteerToward` gained a `brakeOnArrival` flag; the
cross-cluster warp legs (`CrossClusterWarpGoal`) pass `false`, so drones fly
through warps at cruise speed exactly as native does — the exit push carries them
clear of the arrival aleph on their own. This mirrors the client autopilot's
`AutopilotSteering.Pursue` (P540b), which already flies through warp mouths at
full throttle; the server NPC autopilot simply lacked the equivalent.

**The P591b guard is retained only as a documented non-native safety net**, gated
by `Sector.WarpReEntryGuardEnabled` (default on) with a `Ship.NumWarpReEntryBlocks`
counter that must stay **≈0** after the fix. Proof: with the guard **off**, both
layers reach Alderan with exactly the route-length crossings and lay; with it
**on**, the block counter measures **0**.

---

## 1. Why the task's "routing/plan bug" hypothesis was partly wrong — and what it actually is

The task hypothesised the drone's *goal* was "still pointing at a warp in the
previous sector after a crossing / a 2-cycle path / the buoy not resolved / the
shared steering recomputing toward the arrival warp." We instrumented the routing
per tick (after each crossing: current sector, plan target, BFS cluster path,
chosen next warp, steering goal) with the guard off. The trace showed the **goal
was correct**:

```
[rdiag] ship 8097 sector 1062 'Gaia' ... layer-target 1052@(-3066,2666,-381)
        sameCluster=False bfsWarp=2188 pos=(-4770,-3924,-364) goal=(3037,5072,413) vel=(22,18,2)
```

In the intermediate sector (Gaia) the drone correctly re-pathed (`FindPathToward`
returned **2188 → Alderan**, not 2175 back to Organia) and aimed away from the
arrival aleph. So the routing/planning was **not** the defect. The defect was that
the drone could **not get clear** of the arrival aleph: it entered the first warp
braking (measured ~13 u/s immediately before transit), exited Gaia with a weak
push, and was still inside 2175's transit volume when the 2 s re-arm lapsed
(~41 ticks later), so it re-crossed the very aleph it had just arrived through.
That is exactly what the native model avoids, and why a native drone never turns
back.

## 2. Native citations

- **Exit velocity** uses the ship's *current* speed: `speed = |v|`;
  `m_pShip->SetVelocity(backward * -(speed + c_fcidExitWarpSpeed))`
  — `FedSrv/fsship.cpp:93,126`. Low entry speed ⇒ weak exit push.
- **Re-arm is a flat ~2 s timer, no arrival-warp memory**:
  `m_warpState = warpWaiting`, `m_timeNextWarp = g.timeNow + 2.0f`
  — `FedSrv/fsship.cpp:1185-1191`.
- **Ships fly through a warp at speed**: `OT_warp` collision "Hit the front: go
  through" → `HitWarpEvent` — `src/Igc/shipIGC.cpp:629-636`.
- **GotoPlan aims at the next waypoint** (buoy / next warp) — `Waypoint::
  GetGotoPosition`, `Igc/common.cpp` (`FindPath`/cluster path), and only
  decelerates for the final waypoint.

So native relies on `GotoPlan` *maintaining cruise speed into the warp* so the
`+15` exit push clears the destination aleph inside the 2 s re-arm. The port's
`SteerToward` violated that by braking at the warp.

## 3. The fix

**`Ship.SteerToward`** (`Ship.cs`) gained `brakeOnArrival` (default `true`). When
`false`, the distance/decel ramp is bypassed and the throttle stays proportional to
nose alignment — the ship sustains speed through the goal. **`ApplyNpcAutopilot`**
marks `goalIsWarp` on every cross-cluster warp leg (goto / station / layer-build /
asteroid / layer-asteroid via `CrossClusterWarpGoal`) and passes
`brakeOnArrival: !goalIsWarp`; same-cluster final goals (station, asteroid, lay
point, combat standoff, pickup) still brake as before. This exactly parallels the
client `AutopilotSteering.Pursue` (P540b) which already drives through warp mouths
at full throttle; the server NPC autopilot is now consistent with it and with
native `GotoPlan`.

This fixes drones, **bots** (MCP combat/stage routing → server NPC autopilot) and
**pods** (P560 lifepod-home uses the same `GotoWaypoint → CrossClusterWarpGoal →
SteerToward` chain) with a single shared change.

## 4. P591b guard → non-native safety net (with an anti-trap correction)

- `Sector.WarpReEntryGuardEnabled` (default `true`) gates the guard (the test
  seam for the guard-off proof).
- `Ship.NumWarpReEntryBlocks` counts every time the guard actually blocks a
  re-crossing, and `ResolveWarpCollisions` logs it (`[warp] P591b re-entry guard
  BLOCKED …`). After the routing fix it must stay **0** for the pathological
  turn-back (the layers/pods the bug afflicted); a non-zero count is the
  diagnostic that routing regressed.
- **Anti-trap correction (live finding).** Running the 221 showcase with the
  guard as designed exposed a second defect: the guard blocked *every*
  re-crossing of the arrival aleph until the ship cleared the 2× volume, which
  **traps** any ship whose genuine route leads back through the aleph it just came
  through (e.g. a combat bot pursuing an enemy back across the border it entered
  by — its goal lies THROUGH the warp, so it can never clear and release the
  latch, and it froze at the mouth, generating a block every tick). P591c adds
  `Ship.IntendedNextWarpId` (the warp the autopilot is currently routing toward)
  so the guard is precise: it holds only the **unintentional** drift back through
  the arrival aleph (route goal is a DIFFERENT warp or none), and **permits an
  intentional re-crossing** (route goal IS the arrival aleph), matching native
  where a ship that chooses to go back may do so after the ~2 s re-arm. Verified
  live: combat bots now transit the border freely (no freeze). P591b's guard test
  was rewritten to this corrected anti-trap semantic.
- The P591b report gained an addendum correcting the "native holds until clear"
  claim (§`docs/reports/P591b_LAYER_PING_PONG.md`).

## 5. Failing-first (all measured on the real in-process server + `m2xspiderw`)

Today's code = guard-off / pre-fix brake-at-warp behavior. After = this fix.

| Test | Today's code | After |
|---|---|---|
| `Side1 …Organia…lays_with_guard_off` | **FAIL** — laid=False, **1453 crossings** (Organia⇄Gaia), sector 1062 | PASS — laid=True, **2 crossings**, blocks=0 |
| `Re_entry_guard…block_counter_stays_zero` | **FAIL** — guard had to fire **34×** (blocks=34) | PASS — blocks=0 |
| `After_crossing…plan_target_is_next_warp_not_arrival` | **FAIL** — next sector after Gaia = **1051 (Organia)** | PASS — next sector = **1052 (Alderan)** |
| `Side0 …Rigel…lays_with_guard_off` | PASS (lays immediately on arrival; regression guard) | PASS — 1 crossing, lays, blocks=0 |

The side-1 guard-off failure reproduces the exact live 8103 Organia⇄Gaia 2-cycle
(the measured **1453 crossings**); the guard-on blocks=34 shows the safety net
doing real work on today's code. After the fix, the guard-off routes make exactly
the route-length crossings (2 and 1) and lay, and the guard never fires.

## 6. Proof (suite-level) + suite numbers

`P591cDroneRoutingTests` (4 tests) + `P591bLayerPingPongTests` (3, guard test
rewritten to the corrected anti-trap semantic): 7/7.

- **Guard OFF**: S1 Organia→Alderan 2 crossings (route length) and lays;
  S0 Rigel→Alderan 1 crossing and lays. Minimal crossings, both layers.
- **Guard ON**: same routes, `NumWarpReEntryBlocks == 0` — the net never fires
  for the actual bug.
- **Anti-trap**: `Ship_that_arrives_and_intentionally_recrosses_its_arrival_warp_is_permitted_not_trapped`
  (P591b) — a ship whose route leads back through its arrival aleph re-crosses
  (is NOT frozen by the guard), matching native.
- Plan-target test: onward warp from Gaia is 2188 (Alderan), never 2175 (Organia);
  the drone's next crossing is to Alderan, not back.

Whole-solution build: Debug **0 warnings / 0 errors**, Release **0 warnings /
0 errors**.

### Live proof (local 221 server, final code, `consoleVerbose:true`)
`scripts/repro_p591b.sh` boots the 221 config (`m2xspiderw`, civs 25/3) and runs
the MCP `spawn_scenario … minefields{side:both}` showcase. The server log shows:

- The `.Caltrop` mine-layer reaches Alderan and lays with **0** guard blocks (the
  actual bug is gone; the layer route and lay are additionally proven by the
  guard-off tests above).
- Combat bots (MCP adv fighters) crossing the Omega↔Alderan border **transit
  freely** — the anti-trap correction works (before it, they were frozen at the
  arrival aleph's mouth, blocked every tick). The only remaining guard blocks in
  a chaotic combat showcase are *incidental* near-misses — a ship within its
  arrival aleph's transit volume while routing to a DIFFERENT warp (combat
  clusters ships around alephs) — which the guard correctly lets alone (the ship
  is not trying to cross it) and which never trap the ship. For the actual bug
  the block counter is 0.

Full suite — 2 consecutive runs via `scripts/run-tests.sh` (P471), ledger
`docs/TEST_FLAKES.md`:

| Run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire load-flakes, ledgered) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1683/1683 | **573/575** | **3848/3851** | ClientCore: `CrossSide_sector_stays_correct_across_warp_return` (line 404), `ShipDeleteGone…SameSide_Warper…` (fam.); Core: `CargoBayReload…Reload_swaps_cargo_launcher_ammo…` (line 53-54), `CargoBayReload…SwapPart_wire_out_of_range…` (fam.), `GameOverPlayers…Win_by_elimination…` (fam.) |
| 2 | 12/12 | 1683/1683 | **572/575** | **3847/3851** | ClientCore: `CrossSide_sector_stays…` (line 404), `FullSessionKill…ThreePeers…RealCrossSideKill` (fam.), `ShipDeleteGone…SameSide_Warper…` (fam.); Core: `AsteroidMinedLive…No_broadcast_fires_once_miner_hits_capacity` (line 184), `GameOverPlayers…Win_by_elimination…` (fam.), `LiveRipcord…Mid_countdown_anchor_side_change…` (fam.), `SideElimination…Eliminating_side2…` (line 39) |

Every failure is a **pre-existing live-wire load-flake** in a ledgered family
(`docs/TEST_FLAKES.md`); the failure sets differ run-to-run (only
`CrossSide_sector_stays…`, `ShipDeleteGone…SameSide_Warper…` and
`GameOverPlayers…Win_by_elimination…` overlap), confirming non-deterministic
load flakes, not this change. The **`P591cDroneRouting` and
`P591bLayerPingPong` tests passed in both runs (0 failures)**; no
routing/drone/layer/warp test failed in either run.

## Files
- `src/Allegiance.Core/Sim/Ship.cs` — `SteerToward` `brakeOnArrival`; `goalIsWarp`
  threading in `ApplyNpcAutopilot`; `NumWarpReEntryBlocks` counter;
  `IntendedNextWarpId` (anti-trap) stamped by `CrossClusterWarpGoal`.
- `src/Allegiance.Core/Model/Sector.cs` — `WarpReEntryGuardEnabled` seam; guard
  gated + block counter/log + intentional-re-cross permit; comments corrected to
  non-native safety net.
- `tests/Allegiance.Core.Tests/P591cDroneRoutingTests.cs` — 4 tests (2 guard-off
  failing-first routes, plan-target-after-crossing, guard-block-counter).
- `tests/Allegiance.Core.Tests/P591bLayerPingPongTests.cs` — guard test rewritten
  to the corrected anti-trap semantic (intentional re-cross permitted).
- `docs/reports/P591b_LAYER_PING_PONG.md` — addendum correcting the native claim.
- `docs/reports/P591c_DRONE_ROUTING_ARRIVAL_ALEPH.md` — this report.
