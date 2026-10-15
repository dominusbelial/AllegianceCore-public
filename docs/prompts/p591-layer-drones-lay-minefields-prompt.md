# P591 — layer drones must actually LAY: port native layer-drone deployment (build plan → fly to buoy → LayExpendable), and make the showcase buy/order a MINEFIELD layer so real minefields appear at the alephs

## Why this task exists

Showcase minefields never appear. **P560** (`2fd61013`,
`docs/reports/P560_BOT_PODS_AND_SHOWCASE_MINEFIELDS.md:57-115`) fixed the
invest (it completes) and the drone spawn: "drone type 441, pilotType 5,
hull 'Tower Layer Drone', laysExpendable 87, at home". But "the layer drone
is ORDERED to build (orderSent=true) but does NOT actually deploy …
documented P161 gap ('Actual deployment is P161's known gap',
`DroneBucketSpawnsDroneLiveTests`); a layer drone's default order parks it at
a rock." So P452/P574 mine damage and the per-hit pops never show in
showcases. Also check: **is bucket `.TowerAC` / expendable 87 a TOWER rather
than a minefield?** The showcase may be buying the wrong layer.

**Native** (`Igc/shipIGC.cpp`):
- `c_ptLayer` drones: when the accepted/plan command is `c_cidBuild`
  (`:1750-1756`), they fly to the plan target; at it, `c_cmdPlan ==
  c_cidBuild` with `m_pilotType == c_ptLayer` asserts the target is an
  **`OT_buoy`** and calls `GetMyMission()->GetIgcSite()->LayExpendable(
  timeStart, (IexpendableTypeIGC*)m_pbaseData, this)` (`:2810-2828`).
- With no buoy, `:4050-4110` builds one: `DataBuoyIGC db` (for a mine type it
  uses `DataMineTypeIGC` data, `:4064-4066`), `db.type = c_buoyWaypoint`,
  `CreateObject(OT_buoy, …)`, then "change the plan to build at the newly
  created buoy" `SetCommand(c_cmdPlan, b, c_cidBuild)` (`:4102-4110`).
- Server side: `LayExpendable` in `FedSrv` creates the mine/probe/tower
  (find it; cite), and the drone is consumed (check native).
- The pilot type assignment: `:112` (`c_ptLayer`), and `:2324` drone
  behaviour per type.

## Scope

1. Port the layer-drone build flow 1:1 (plan → buoy → arrive → `LayExpendable`
   → expendable created → drone consumed/returned per native), for mines AND
   towers (whatever the expendable is), server-side, using the existing
   P161/P452 mine model and the P574 per-hit explosion path.
2. Ordering: commanders/MCP can order a layer to build at a position/sector
   (native `c_cidBuild` targeting a buoy/position); default behaviour per
   native if no order.
3. Showcase: make `spawn_scenario … minefields` buy/order a **minefield**
   layer (not a tower layer, unless native's naming says `.TowerAC` IS the
   mine layer: verify with the core's expendable 87 type) and place the
   field at the rendezvous aleph(s).
4. Keep bots/MCP and the client unchanged except what's needed to show it.

## Proof

- Loopback/in-process test: a layer with a build order lays its expendable at
  the buoy (failing-first on today's code).
- Live: a showcase 15v15 on the 221 config → server log lines for each laid
  minefield (position near the aleph), ships taking P452 mine damage, P574
  per-hit pops in the real window (screenshot/frame strip).
- Report `docs/reports/P591_LAYER_DRONES_LAY.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
