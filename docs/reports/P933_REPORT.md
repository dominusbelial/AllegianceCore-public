# P933 — arena patrol points and M6/M10 default orders

The three scoped rows are implemented: **12/12 points, 3/3 rows**. The patrol
policy now uses native mixed pickable insertion order, all eligible body kinds,
and the launcher identity exclusion. Coincident centers match native **retail**
behavior safely. No other matrix rows or native rule descriptions were changed.

Changes remain in the working tree; no commits, pushes, or remote-host actions.
Dispatch base: `0f6c98926fe3095f5ad23e8e47553a45bb3681cb`.

## Row audit

| Row | Verdict | Native | Production port | Regression / live evidence |
|---|---|---|---|---|
| TRN-0238 | implemented, 4/4 | `training/mission6.cpp:230-277`; `training/mission10.cpp:226-273`; list and attribute sources below | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:80`; `src/Allegiance.Core/Model/Sector.Pickables.cs:8`; `src/Allegiance.Core/Model/MissionMap.cs:83`; `src/Allegiance.Core/Model/Mission.Registry.cs:216` | `P933TrainingPatrolTests`: mixed insertion/reentry, body membership, pointer exclusion, static file order, station/He3/random ordering, seeded replay, buoy lifetime and coincident centers |
| TRN-0134 | implemented, 4/4 | `training/mission6.cpp:82-287` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs:17`; `src/Allegiance.Core/Sim/Ship.cs:7797` | `P933TrainingPatrolTests.Live_arena_tick_picks_through_complete_candidates_for_each_lesson(false)` and existing `P911TrainingArenaTests` |
| TRN-0143 | implemented, 4/4 | `training/mission10.cpp:78-283` | same shared production handler, bound by `src/Allegiance.SinglePlayer/Training/TrainingHost.cs` | same live test with `true`, plus existing M10 arena tests |

The regression file is
[`P933TrainingPatrolTests.cs`](../../tests/Allegiance.ClientCore.Tests/P933TrainingPatrolTests.cs).
Each real-tick case observes the original production callback while
`TrainingHost.StepLight` drives `Mission.Update` and `Ship.DecideWingmanDefaultOrder`.
It checks the waypoint after a treasure overrides a probe, with a later own
missile excluded. Expected geometry is sampled at callback time because actual
simulation movement/collisions can move the choosing ship before the decision.
No orders, damage, deaths or target choices are injected by that observer.
Existing combat-wave tests retain miner, repair, escort, scout, bomber and carrier
recipes; P933 changes only the common local patrol branch they depend on.

## Native rules and implementation

- `Igc/clusterIGC.cpp:500-518,539-551` appends to `m_modelsPickable` on cluster
  entry and deletes on exit. The general `m_models` list inserts at the front;
  it cannot substitute for the pickable list. `Igc/clusterIGC.h:90-247` funnels
  typed treasure/warp/station/ship/mine/missile/probe/asteroid additions through
  AddModel. `Igc/igc.h:5130-5137` also appends typed lists.
- `Igc/missionigc.cpp:3269-3294` imports records in file order. MissionMap now
  retains that cross-type sequence; after existing typed assembly passes, only
  the new pickable view is ordered by it. Later additions, removals, station
  retyping and reentry maintain their actual mixed entry order. The other
  simulation consumers retain their lists and iteration order.
  Retyping's detach/reentry is `Igc/stationIGC.cpp:229-230,267-271`;
  `Igc/missionigc.cpp:4018-4023` explicitly accounts for its move to the end.
- Pickable attributes come from `Igc/warpIGC.cpp:33-35`,
  `Igc/asteroidigc.cpp:32-36`, `Igc/probeigc.cpp:112-115`,
  `Igc/missileigc.cpp:50-67`, `Igc/treasureIGC.cpp:159-177`, and
  `Igc/buoyigc.cpp:39-71`. Treasure radius is 5 (`treasureIGC.cpp:193`);
  visible/hidden buoy radii are 20/2 (`buoyigc.cpp:45-48`). Hidden buoys are
  pickable even when no visual is drawn. Navigation buoys are eagerly observed
  when training starts so later lazy materialization cannot reorder them.
- Projectiles, chaff and construction effects carry `c_mtNotPickable`
  (`Igc/projectileIGC.cpp:53-66`, `Igc/chaffigc.cpp:42-46`,
  `Igc/buildingeffectigc.h:93-100`). Ships and mines are pickable but explicitly
  excluded from arena corrections (`mission6.cpp:252-253`, `mission10.cpp:248-249`).
- `Igc/missileigc.cpp:87-99` sets a missile's NoHit to its launcher's HitTest.
  `ReferenceEquals(missile.Launcher, ship)` represents that pointer exclusion;
  a different ship with an equal numeric ID remains an obstacle. Other native
  NoHit setters apply to non-pickable projectiles/effects. No new collision
  behavior or broadly writable NoHit surrogate was introduced.
- The native correction intentionally uses **ship minus body**, tests strict
  distance `< radius + shipRadius + 10`, and overwrites the random point for
  each eligible overlap (`mission6.cpp:254-257`, `mission10.cpp:250-253`). It
  does not iteratively move the point clear of obstacles. Live identities keep
  current missile/treasure positions available without geometry snapshots.
- Station candidates precede He3 rocks, then exactly two random points; only
  afterward is one index drawn. An internal Random source permits deterministic
  test replay; ordinary sessions retain Random.Shared. This certifies candidate
  order and repeatability, without claiming the .NET generator reproduces a
  native C-runtime RNG stream.

There is no remaining scoped gameplay gap. Coincident centers need a correction
to P911's prior characterization: `zlib/vector.h:97-108` calls `ZError` then
returns the original zero vector. `zlib/zassert.h:54` enables its debug assertion;
`:67` compiles it out in retail. `zlib/Win32app.cpp:105-126` can break/crash/abort
on that assertion. The safe port uses its existing zero-return Normalize,
therefore sets the point to the body's center, exactly as native retail does.
It omits the native debug assertion in both port configurations. The regression
covers an earlier correction being overwritten and an otherwise random point.

## Evidence and scorecard

Before any production edits, the initial nine regressions ran against the
original code: **8 failed / 1 passed / 0 skipped**. Missing membership and mixed
ordering produced wrong waypoints; the seeded cases also lacked the replay hook.
The final development run passes **16/16, 0 skipped**. Restoring the old patrol
policy while retaining only the deterministic Random hook causes **9/16 failures**,
including both real M6/M10 ticks and coincident centers. Separately removing
NoHit exclusion fails its regression, and reversing pickable traversal fails both
mixed-order cases. All three controlled mutations were restored before gates;
logs/TRX are under `.scratch/p933/mutations/`. These controls test actual behavior,
not just the presence of the new replay hook.

| Metric | Before | After |
|---|---:|---:|
| Training weighted production credit | 807/901 (89.57%) | 819/901 (90.90%) |
| Training completed eligible rows | 206/235 (87.66%) | 209/235 (88.94%) |
| P933 scoped credit | 0/12 | 12/12 |
| P933 completed rows | 0/3 | 3/3 |

Re-measured by Claude after merging onto master ee78f26a (which adds P932): training **816/901 (90.57%), 208/235** before, **828/901 (91.90%), 211/235 (89.79%)** after — the same +12 points and +3 rows.

`python3 scripts/parity-scorecard.py` validates before and after. All other
scorecard totals remain unchanged; denominators and weights are unchanged.
Small public receipts:
[before](P933_assets/scorecard-before.txt), [after](P933_assets/scorecard-after.txt),
and [verification receipts](P933_assets/verification.json).
Raw local test/build receipts are retained in ignored `.scratch/p933/` because
the writable sandbox excludes the external evidence store. The inherited window
harness writes a fixed P931 path; its receipts now live under
`.scratch/p933/inherited-p931/`, with a local symlink preserving logged paths.
The temporary base source/build checkout and published executable were removed
after controls completed; assembly/publish hashes and raw receipts remain. Base
window captures are retained in `.scratch/p933/base-window-controls/raw-windows/`.
No media was added to the public report assets.

## Validation

The final production source passed the whole-solution Debug build with zero
warnings and errors. All 16 new cases and all 16 existing arena cases passed
three separate filtered runs, with zero skips. `local.env` was loaded and
artwork-backed cases executed. The row audit and matrix were completed before
qualification; production source did not change during the gates.

The broad artwork-backed training run recorded a failure in the existing
`P912TrainingWindowTests.Assigned_lesson_completes_in_its_own_fresh_window(mission: 4, lesson: 5)`:
its real-input driver timed out after 20 minutes with `M4 stalled at 5.7`, during
Mars warp traversal and repeated trainee death/restoration. The original failure
is retained in `.scratch/p933/m4-prefix5-sweep-failure.txt` and the full training
log. This is a failed broad-gate observation; successful scoped regressions do
not erase it. A second case in the same method, `(mission: 4, lesson: 10)`, also timed out
after 20 minutes at `M4 stalled at 8.7`, while awaiting command acceptance.
The existing M3 prefix case `(mission: 3, lesson: 9)` likewise timed out at
`M3 stalled at 9.7`. The Modern early-Nanite compatibility case also failed at M8 goal 3.1:
`early mounting and conserved repair energy did not exercise miner survival`.
No lesson controller, assertion or timeout was changed.

The requested `tools/run-training-tests.sh --no-build --repeat 1` finished at
09:06:56 UTC: **367 passed / 4 failed / 2 skipped (373 total)**. ClientCore is
118/118, zero skips; MonoGame is 249 passed / 4 failed / 2 skipped. Both skips
are the dispatch base's opt-in M4 hunt cases (`ALLEGIANCE_TRAINING_BRANCH_PROOFS`),
not missing artwork. All ten full-course window cases, both sacrifice branches
and Classic early-Nanite completed. The broad gate remains **FAIL**.

Completed qualification receipts:

| Check | Result |
|---|---|
| Debug whole solution | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 116 projects |
| New focused cases, three isolated runs | PASS, 16/16 per run, 48 passes / 0 skips |
| Existing P911 arena cases, three isolated runs | PASS, 16/16 per run, 48 passes / 0 skips |
| Additional server-GC-pinned P933 and P911 repeats | PASS, each class 16/16 × 3; 96 passes / 0 skips |
| Full training gate, one repeat with artwork | FAIL, 367 passed / 4 failed / 2 opt-in skips; 0 artwork skips |
| Ordinary showcase never-parks, five serial runs | PASS 5/5, 0 skips |
| Server-GC baseline parking, five alternating pairs | Both lanes 4/5 full-test passes; one arrival-coverage failure each; no five-second parking violation |
| ONE Fast-tier sweep | FAIL, 13156 passed / 12 failed / 3 existing optional skips; 2 timing offenders; infrastructure PASS |
| Release linux-x64 server NativeAOT publish | PASS, no IL2026 / IL3050 diagnostics |

The five original working-tree parking controls used the existing test runner's
default GC configuration. They all reached 24/30 objectives; longest observed
pauses with a target were 2.109 / 2.003 / 1.914 / 2.405 / 2.006 seconds. These
are functional controls, separate from the explicitly pinned baseline below.

Fast failure and timing follow-ups are complete: twelve methods received three
separate filtered invocations each. Ballot, roster time, missile destruction,
gun-development damage and the warp timing case pass 3/3. P660 is mixed
(3/4 cases, then 4/4, then 4/4); missile-lock, NPC-defend and the five failing
bundle-path cases persist in this cohort. Exact symptoms and classifications
are in [TEST_FLAKES.md](../TEST_FLAKES.md#p933-fast-qualification--2026-11-06).
The original sweep remains failed; no second Fast sweep was run.

The four failed training cases each passed **3/3 isolated repetitions**, zero
skips. Each repetition selected only those four cases and ran them serially;
no other task tests ran alongside them. M4 lesson 5 took 109.5–110.3 seconds,
M4 lesson 10 took 457.5–617.5 seconds, M3 lesson 9 took 118.4–156.3 seconds,
and Modern early-Nanite took 268.5–292.5 seconds. The unchanged dispatch base
also passed one control per case. Those four base controls ran concurrently
in separate processes, so they are not a matched-load comparison. Binary hashes
and absence of both new code markers establish that these were base binaries.
The outcomes qualify the broad failures as intermittent window-proof failures;
they do not establish host load or P933 as the cause. Details and exact symptoms
are ledgered in [TEST_FLAKES.md](../TEST_FLAKES.md#p933-training-window-qualification--2026-11-06).

## Parking comparison on baseline settings

Five alternating serial base/working-tree pairs used `DOTNET_gcServer=1`.
Every receipt confirms immutable snapshots, encode-once, parallel build,
pooled transport, native ship updates and native GotoPlan. The test and its
assertions were unchanged. Both lanes passed **4/5 full tests, zero skips**:

| Lane | Observation seconds, runs 1–5 | Longest idle with target, seconds, runs 1–5 |
|---|---|---|
| Dispatch base | 480.0 (fail), 241.8, 302.5, 193.1, 183.8 | 1.913, 1.804, 1.612, 1.904, 1.911 |
| Working tree | 221.6, 382.1, 187.8, 480.1 (fail), 266.2 | 2.204, 1.403, 1.910, 0.802, 1.212 |

Both failures were `Most showcase ships must reach their objectives`: base
reached 19/30 and working tree 17/30 before the eight-minute limit. All other
runs reached 24/30. No observation violated the five-second parking assertion.
The equal observed pass rates in this small cohort do not establish a causal
performance result. Base's failed arrival check was followed by four passing
serial base repetitions. The working-tree failure was followed by **3/3 passing
serial repetitions** (runs 5–7): 266.2 / 216.1 / 209.1 observation seconds,
each 24/30 arrivals, longest idle 1.212 / 2.003 / 0.000 seconds. Runs 6–7 are
follow-ups and are excluded from the original five-pair pass-rate comparison.
Details are in
[TEST_FLAKES.md](../TEST_FLAKES.md#p933-baseline-parking-qualification--2026-11-06).

## Bounded local performance control

The existing `tools/p911-arena-proof` ran unchanged against dispatch base and
working tree in three alternating processes per lane. Each process warmed up
for 200 ticks, then measured five 1,000-tick windows with 30 ordinary NPCs.
The last three windows per process supply nine steady samples per lane.
Both lanes explicitly used immutable snapshots, encode-once, parallel build,
pooled transport, native ship updates, native GotoPlan and server GC; interest
tiers and native broadcast ordering remained off. All six processes reported
`serverGC=True` and shut down with zero overruns. No synthetic load was generated.

| Steady measurement | Dispatch base | Working tree |
|---|---:|---:|
| Median elapsed per 1,000 ticks | 173.94 ms | 181.24 ms (+4.20%) |
| Observed elapsed range | 134.19–321.83 ms | 139.53–196.38 ms |
| Median allocated bytes per tick | 88,686.05 | 89,516.11 (+0.94%) |

Host load averages at the three pair starts were 1.69/0.82/0.65,
1.71/0.84/0.66 and 1.66/0.84/0.66. This is a small local simulation control,
with no connected peers and no active training arena. It does not establish
a speedup, zero overhead, arena throughput or a rig-scale result. Raw samples,
settings and the exact workload are retained in the verification receipt;
the measured median increase is reported without a causal performance claim.

## Native-intent footer

The ordered candidate population, pointer exclusions and temporary sector
aggression are **design**. Pointer-based HitTest identity, native RNG coupling
and the debug/release assertion split are **era constraints**. Normalizing a
zero vector and correcting from ship position instead of waypoint position are
native **bugs**. P933 preserves retail behavior and safely omits the debug
assertion. A modern design would be more robust if it sampled collision-free destinations
from an explicit seeded stream and returned a fallback for degenerate geometry; that would
be a deliberate behavior change and is not silently substituted here.
