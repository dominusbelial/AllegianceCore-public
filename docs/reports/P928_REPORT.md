# P928 — simulation allocation cuts, byte identity

The three alternating NativeAOT bench pairs reduce allocation from **808.7 to
65.5 KiB/tick (91.9%)**, with the 400-ship population sustained. Their median
tick times are 0.95–0.97 ms before and 0.81 ms after. No feature flag,
simulation rule, broadcast policy, commit, push, or remote-host action was added.

Base: `5ca9f37ac1e9799482122ac8dd8417df27d9375b`, branch `codex/p928`.
The assigned worktree already had `local.env`; artwork was available. The final
Fast sweep has three optional-pack skips; task tests have zero.
Detailed gate outcomes and remaining baseline failures are recorded below.
Raw evidence is kept in the task's writable [scratch directory](../../.scratch/p928/),
including the saved base/final native executables, publish logs, raw profiler
windows, trace files and TRX results. Small portable summaries are under
`P928_assets/`. No private artwork is included.

## Identity proof

The original [500-tick fixture](../../tests/Allegiance.Core.Tests/Fixtures/P928Golden.csv)
was generated and replayed **before any production cut**. Its generation pins
`PCore001.igc` SHA-256
`F73AA7BBCE7414140F6691366F8829EA58829727660A8A86C114B29A83A55DE8`.
The fixed-time, seed-928 scenario uses 240 flying ships, 20 turret gunners,
12 native GotoPlan travellers, duplicate fixed and turret mount IDs, sustained
weapon fire and seeded salvos, 8 sensor probes, 12 mines, 12 asteroids and
2 stations, all in one sector. It completes 500 × 0.053 s with **254 physical
collision callbacks, 1,673 ship-damage callbacks, 18 kills, 18,164 weapon-created
projectiles and 48,268 counted RNG draws**.

Each row records independent SHA-256 hashes of binary authoritative state,
site callbacks in their actual dispatch order, and real encoded
`SINGLE_SHIP_UPDATE` payloads, plus RNG/event counters. State uses the original
float bits, including all nine ship/probe collision-basis components; it covers
model order, IDs, clocks, position/velocity, hull/shield/energy/ammo/fuel, weapon
firing state, visibility/eye bits, live static-target health and retired ships.
The RNG counter includes scenario selection and every weapon-dispersion draw.
Sensors' random constructor attitudes are reset before ticking; their two
constructor draws per probe are outside the counted run, and they never fire.
Other unseeded RNG paths (treasure, complaints and chaff) are deliberately absent.

The [supplemental fixture](../../tests/Allegiance.Core.Tests/Fixtures/P928SupplementalGolden.csv) adds explicit projectile headings/lifetimes, full
control and turn-rate floats, ore/cloaking/parent/dock state, static geometry,
weapon burst/mount state and additional station/expendable callbacks. It is
sourced from the unchanged base implementation, with the original fixture kept
unchanged. The [provenance record](P928_assets/fixture-provenance.json) records the exact
base source and fixture hashes. Its 500 wire hashes and RNG/event counters also
match the unchanged original fixture; it only adds observation coverage. Both
replays pass after restoring the cuts.

The wire hash is the real codec over every living ship, in deterministic order;
it does **not** claim to capture per-viewer server scheduling, filtering or
transport framing. Existing broadcast identity tests remain in the Fast gate.
The replay itself runs on .NET's test runtime; the performance comparison below
uses NativeAOT. This is a local same-machine proof, not a cross-architecture
floating-point portability claim.

Negative control: temporarily reverse the detector's sorted collision stream,
then run the original golden test. It **fails on tick 1**, changing state,
callback-order and wire hashes while retaining the same event counts. The
perturbation was reverted before the cuts. Raw evidence:
[negative-control.log](../../.scratch/p928/negative-control.log).
Each cut (a), (b), (c), (d) subsequently passed the original 500-tick replay.

[The proof tool](../../tools/p928-allocation-proof/README.md) shares its scenario
source with the test. Additional tests exercise changing detector sizes,
repeated/unique geometry, serial/parallel detection, complete body reset,
reference release, nested leases, separate parallel owners, removal/addition
and exception-safe model snapshots, stable duplicate weapon mounts, loadout
changes during a firing callback, last-shot energy priority and retained
orientation values.

## Cuts and ordering

Line numbers in the “before” column refer to the base revision above; “after”
refers to this working tree. Stage measurements use the same NativeAOT/server-GC
400/240/60 bench, after 60 warmup ticks. Savings are **incremental stage totals**,
not a fabricated split between sites changed together.

| Cut / P838 sites | Before → after | KiB/tick after | Incremental saved | Identity constraint |
|---|---|---:|---:|---|
| (a) detector/collider graph, #2 + supports from #6 | `Model/Sector.cs:2268,2859,3153,3410` → `:2271,2863,3158,3416`; `Collision/Collider.cs:26,65` → `:26,37,77` | 548.9 | 259.8 | One private sector owner; reset every constructor/step/shape field; same registration order. Cached immutable hull supports are referenced directly. Frame disposal clears model/hull references; nested callbacks receive independent storage. |
| (b) geometry maps and result arrays, #1 + remaining #6; sweep delegates, #4 | `Collision/CollisionDetector.cs:75,76,93,133,148` → `:15,142,165`; `Collision/BroadPhase.cs:105` → `:105` | 281.2 | 267.7 | Clear and pre-size the dictionary, never remove/iterate it; group IDs still come from the first collider encounter. Reset direct-cache tested bits and overwrite used parallel results. Compact the active list stably using the negation of the original removal predicate, including its NaN behavior. |
| (c) weapon LINQ, #3; selected navigation closures, #7 | `Sim/Ship.cs:9429,9431` → `:873,9518`; navigation `:7485,7852,7973,8003,8555,8560,8579,8854,8898` → nearby explicit loops/helpers | 81.1 | 200.1 | First fixed match in list order; stable turret insertion by captured mount keys; snapshot collected after fixed-gun callbacks and before turret callbacks. New guns wait for the next turret snapshot. No floating-point/RNG expression changed. |
| (d) three model snapshots, #5 | `Model/Sector.cs:1021,1240,3809` → `:1022,1242,3816`; new `Model/Sector.Scratch.cs:13` | 65.5 | 15.6 | Capture fresh membership separately at each original call site; keep insertion order, duplicate membership and existing live-membership checks. Clear references at disposal. Nested resolvers use a distinct rented array. |

Navigation locations in `Sim/Ship.cs` (base → final): cross-sector lookup
7485 → 7542; builder claim checks 7852 → 7911 and 8854 → 8934; repair-station
membership 7973 → 8032; nearby repair-mine threat 8003 → 8062 (helper 8079);
teleport-unload station/side lookup 8555/8560 → 8629/8636; active builder check
8579 → 8657; alliance lookup 8898 → 8986. These share stage (c)'s aggregate
measurement; no separate per-helper saving is claimed.

The other detectors named in the brief **are hot**: ship/asteroid, ship/ship
and ship/station passes build them on populated sectors every tick. All four
ordinary passes now share a sector's sequential frame store. The experimental
`Sector.TickOrder.cs` collision queue remains untouched: both native phase/queue
flags are off in the required winning baseline, and its contact recalculation
has different lifetimes.

`Mission.Update` walks its sectors serially (`Model/Mission.cs:1061`);
`broadcast.parallelBuild` uses later broadcast snapshots, not these private
scratch stores. Different sectors own different detector/array graphs. The
supported detector parallel path joins all narrow-phase workers before it
expands results or releases the frame. Reference clearing also prevents a peak
pool from retaining dead models/core hulls. Review tightened the failure path:
a borrowed slot is counted before resetting it, so a hull-support exception
still releases its just-assigned model/hull references. A regression exercises
that failure, the next frame's clean state, and successful reuse. The per-frame
geometric diagnostic is also reset. Capacity remains at the sector/ship's
high-water mark until its owner is collected; that retained scratch capacity is
the memory tradeoff to check in the rig A/B.

## NativeAOT bench: paired runs

All timing/allocation comparisons use `.NET SDK 10.0.403`, linux-x64 NativeAOT,
server GC, 24 available processors, real `PCore001`/`m2xspiderw.igc`,
`nativeGotoPlan=true`, and the unchanged default-off native ship-phase/collision
queue experiments. Every run ends with **400 ships and 240 projectiles**.
The bench has no network/broadcast/transport stage; those winning settings are
exercised in the real-server capture below. The bench spawns bare unarmed ships;
its large weapon-loop saving removes overhead that was paid even without guns.
The crowded golden scenario separately exercises firing/turrets and duplicate
mounts. The retained gunner-copy cost is not represented by this bench.

Build and run (source `local.env` and use a worktree-local temporary/cache directory):

```sh
dotnet publish tools/P835Bench -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/p928/bench-before
# Keep this binary from the unmodified base; build the final tree to bench-after.
.scratch/p928/bench-before/P835Bench 400 240 60
.scratch/p928/bench-after/P835Bench 400 240 60
# Repeat before/after in that order three times, serially.
```

`P835Bench` now explicitly enables server GC/native GotoPlan and prints median
and p99 in addition to its existing metrics. The same instrumentation was
applied before either native binary was built. It does not change scene inputs.
The original pre-cut baseline captures and first paired comparison are retained
separately. This table is the fresh three-pair comparison after the final
exception-cleanup guard; allocation is unchanged from the first comparison.

| Arm/run | MiB allocated / 500 ticks | KiB/tick | gen0/1/2 | pause ms/tick | pause % wall | mean / median / p95 / p99 ms |
|---|---:|---:|---|---:|---:|---|
| before 1 | 394.9 | 808.7 | 22/8/5 | 0.097 | 8.0 | 1.23 / 0.96 / 2.59 / 5.11 |
| after 1 | 32.0 | 65.5 | 2/0/0 | 0.001 | 0.2 | 0.96 / 0.81 / 2.31 / 2.42 |
| before 2 | 394.9 | 808.7 | 17/4/4 | 0.109 | 8.8 | 1.24 / 0.97 / 2.62 / 5.37 |
| after 2 | 32.0 | 65.5 | 2/0/0 | 0.001 | 0.2 | 0.96 / 0.81 / 2.30 / 2.46 |
| before 3 | 394.9 | 808.7 | 19/4/4 | 0.100 | 8.3 | 1.21 / 0.95 / 2.55 / 5.21 |
| after 3 | 32.0 | 65.5 | 2/0/0 | 0.002 | 0.2 | 0.95 / 0.81 / 2.29 / 2.45 |

Tool labels “MB/KB” divide by 1,048,576/1,024; this report labels them MiB/KiB.
Allocation falls by 743.2 KiB/tick, comfortably exceeding the 50% target.
The mean tick-time average improves by about 22%; these local timings are
shared-host observations, not a projected rig guarantee. The raw profiler logs
retain every bucket. `step:update` accounts for 391.4–391.5 MiB before versus
28.6–28.7 MiB after per 500-tick run; its amortised timing is 1.20–1.23 ms
before and 0.94–0.95 ms after.

## Real local showcase and GC windows

Each arm launches the saved default NativeAOT server and the ordinary MCP host,
requests the showcase preset with 60 bots per side, and records 150 s after a
successful 120/120 spawn. Runs alternate before/after three times. The server
explicitly uses immutableSnapshot, encodeOnce, parallelBuild, pool transport,
native ship updates and native GotoPlan; all rejected/default-off experiments
remain off. The existing per-IP admission limit is raised only in the scratch
configuration so the 120 local bots can connect. Two initial failed setup
attempts (missing side names; then the default 64-per-IP cap) are excluded.

For each run the table uses the **last 20 complete five-second windows** (about
2,020 ticks), excluding startup. This is a real, nondeterministic scout/march/
combat showcase, not a replay of identical bot decisions. Window and run spread
are both shown; no rig result is inferred.

| Arm/run | KiB/tick mean (window min–max) | gen0/1/2 totals | pause ms/tick | pause % tick-wall | tick-wall mean ms |
|---|---:|---|---:|---:|---:|
| before 1 | 1289.7 (1237.4–1487.1) | 505/6/0 | 0.690 | 21.40 | 3.224 |
| after 1 | 722.2 (685.3–749.9) | 231/4/1 | 0.332 | 12.40 | 2.679 |
| before 2 | 1264.3 (1231.7–1359.2) | 526/3/0 | 0.706 | 21.78 | 3.244 |
| after 2 | 741.5 (698.6–796.8) | 242/5/1 | 0.367 | 12.86 | 2.853 |
| before 3 | 1255.8 (1233.0–1283.5) | 496/3/0 | 0.691 | 21.12 | 3.272 |
| after 3 | 734.8 (704.0–773.5) | 244/3/0 | 0.359 | 12.72 | 2.824 |

| `step:` | Before mean ms (run range) | After mean ms (run range) | Before MiB/window | After MiB/window |
|---|---:|---:|---:|---:|
| `step:update` | 1.238 (1.216–1.270) | 0.840 (0.801–0.875) | 53.46 | 9.37 |
| `step:broadcastShipsUpdate` | 0.754 (0.695–0.790) | 0.742 (0.720–0.758) | 7.93 | 7.05 |
| `step:broadcastShipStates` | 0.464 (0.456–0.472) | 0.448 (0.442–0.454) | 17.04 | 17.70 |
| `step:drain` | 0.384 (0.374–0.390) | 0.365 (0.334–0.395) | 13.21 | 13.29 |

The whole-server mean falls 42.3% (1269.9 → 732.8 KiB/tick);
`step:update` allocation falls about 82.5%. Other steps still dominate a
substantial part of the final server total. Per-step global allocation can
include overlapping network-worker activity; these are the existing profiler's
counters, not exact per-method allocation isolation. All windows and bucket
summaries are preserved in [measurements.json](P928_assets/measurements.json).
Reproduction driver: [showcase.py](../../tools/p928-allocation-proof/showcase.py).
[Spawn/end receipts](P928_assets/showcase-receipts.json) confirm 120/120 spawns
in all six runs, and 120 active bots at every available final stats receipt.
Before runs 2/3 ended with 122/79 shots and zero recorded hits; after runs
1/2/3 recorded 115/168/102 hits and 2/2/1 kills. Before run 1's final stats query
used an unavailable tool name; its successful spawn receipt and server windows
are retained, but there is no final combat count for that run. Thus the 42.3%
whole-server difference is **illustrative**, with different combat activity,
not a controlled combat-throughput or causal whole-server A/B result. The
seeded bench supplies the controlled allocation comparison; match combat
activity carefully in the rig A/B.


## Allocation recapture

An ordinary NativeAOT publish defaults `EventSourceSupport=false`; attempting to
attach the trace tool to that binary cannot establish the diagnostic session.
A **separate diagnostic-only** NativeAOT publish with
`-p:EventSourceSupport=true` successfully recaptured 423 allocation samples,
47,398,880 sampled bytes, with `dotnet-trace --profile gc-verbose`. All allocation
type names are `NULL` and no application frames resolve in this runtime/tool
combination. Those samples cannot honestly supply a native top-ten site table.
The default AOT binaries, not this diagnostic binary, supply all comparison
numbers above.

The companion server-GC JIT capture of the same final code/400-ship scene gives
449 samples and 49,972,296 sampled bytes, including core load, scene setup,
60 warmup and 500 measured ticks. Grouped by type plus first application frame,
the new top ten are:

| Rank | Sampled MiB | Allocated type | First application frame |
|---|---:|---|---|
| 1 | 19.035 | `Func<Asteroid,bool>` | `Sector.ResolveMining` |
| 2 | 6.503 | `VisibilityParticipant[]` | `Sector.ResolveCrossSideVisibility / Add` |
| 3 | 3.037 | participant-index `Entry[]` | `Sector.BuildParticipantsIndex` |
| 4 | 1.626 | `List<int>[]` | `BroadPhase.OverlappingPairs / MoveNext` |
| 5 | 1.524 | visibility `Dictionary` | `SideVisibilityState` constructor |
| 6 | 1.423 | `Projectile` | `ClashBench.TopUpProjectiles` |
| 7 | 1.021 | `byte[]` | `TechTreeBitMask.FromBytes` |
| 8 | 1.015 | `Bucket[]` | `Side.Update` |
| 9 | 0.709 | `Ship` | `ClashBench` constructor (startup) |
| 10 | 0.618 | `byte[]` | `IgcReader.ParseFile` (startup) |

Full data: [JIT ranking](P928_assets/allocations-jit.tsv), [AOT capture summary](P928_assets/allocations-aot.tsv).


The reporter prints all groups, so the full ranking can be inspected. Sampling
is coarse at the tail and whole-capture startup entries are explicitly retained;
these are not exact per-site object counts or default-AOT byte estimates.
The old geometry dictionary/collider rebuild/weapon-loop/sweep-delegate sites no
longer dominate. The largest residual is a mining predicate outside this brief's
ranked cuts; visibility scratch is now also prominent.

## Deliberately retained work

- **#8 gunner `Orientation.Copy()`** remains at `Sim/Ship.cs:913`. Native
  `Igc/shipIGC.cpp:336–338` copies the nine-component value before advancing and
  publishing it. Managed `SetOrientation` publishes a mutable class reference;
  recycling the same object (even with two buffers) can mutate a previously
  retained orientation. The new retained-value test guards that contract.
  Changing the global class to a value type would be a much broader refactor.
- **#9 visibility scratch** is retained. Its participant/scanner/side/spotter
  structures and nested callback lifetimes need a separate ownership change;
  it is not a cheap extension of the model snapshot buffer. The trace quantifies
  its new rank without changing discovery, caches or parallel behavior.
- **#10 projectiles** retain their normal identity/registry lifecycle. Model
  pooling and broader algorithm/cache changes are outside this task.
- Navigation changes remove the listed captured predicates and preserve first
  matches/short-circuiting. They do not claim to remove every iterator/delegate
  in ship AI. The bench's stage-(c) delta combines weapon and navigation changes;
  it does not invent a separate NPC-byte saving for a non-NPC scene.

## Verification

- **Final solution build:** zero warnings / zero errors; all **116** tool projects
  build clean. These checks were repeated after restoring the optimized sources
  from the base controls.
- **Affected cohort:** **126/126 × 3**, zero skips. It includes both 500-tick
  replays and the relevant collision, broad-phase, weapon, turret, friendly-fire,
  native GotoPlan and nanite-repair tests.
- **Final task tests:** **10/10 × 3 alone**, zero skips, including the later
  failure-path cleanup regression. Both fixture hashes remain unchanged.
  The cleanup regression was added after the single Fast sweep; it was built and
  qualified separately, and the full Fast tier was not rerun.
- **ONE Fast sweep:** **13,060 passed / 10 failed / 3 skipped**, 13,073 tests,
  882.0 s, all nine infrastructure checks passed. The three skips are the
  optional private Meshy pack, not artwork. The sweep remains **FAIL**. The
  missile-tip receipt case also exceeded the 10 s guard (38.498 s).
- **Normal showcase never-parks:** **5/5**; the additional swapped-home scenario
  is **4/5**. Every one of the ten executions holds the actual five-second
  parking bound (worst 2.006 s). The failed swapped run reached 17/30 objectives
  by 480 s; this exact composite arrival failure was already ledgered on merged
  master. The final three serial swapped repetitions pass.
- **Failure qualification:** all failed Fast methods ran three filtered,
  serial repetitions on the optimized tree and three controls against the exact
  dispatch-base Core sources. Path fixtures, wingman target and join status fail
  on the base too. Missile tips are fail/fail/fail here and pass/fail/pass on the
  base; side selection passes 3/3 here and pass/fail/fail on the base. Mining
  receipts are pass/pass/fail here and pass/pass/pass in this small base sample;
  the failure family is already ledgered, but its cause is not established by
  these samples. No fixture/threshold or unrelated behavior was changed.
  Detailed outcomes remain open in [the flake ledger](../TEST_FLAKES.md#p928-validation--one-fast-sweep-serial-live-runs-and-base-controls-2026-11-04).
- **Server NativeAOT publish:** final linux-x64 self-contained publish is clean,
  including no IL2026/IL3050. Both benchmark arms and all six real-server
  captures use the required winning baseline; trace-enabled AOT is diagnostic only.
- **Parity/glossary/hygiene:** scorecard output is byte-identical before/after;
  no parity CSV changed. The glossary validator passes (550 paths). Working-tree
  content scan finds no secrets/personal paths/new media; `git diff --check` passes.

Small evidence: [verification.json](P928_assets/verification.json),
[Fast summary](P928_assets/fast-summary.json),
[all ten live receipts](P928_assets/never-parks.json). Raw test results are under
[results/](../../.scratch/p928/results/) and
[the single Fast run](../../.scratch/p928/fast-results/).


No new service, store, application port or environment variable was introduced;
Docker Compose was therefore unchanged. The glossary documents the new owner/
lease pattern. Parity CSV files were not changed; this task ports no new native
rule. All changes remain in the working tree, without commits or pushes.

## Rig A/B handoff

Use the existing winning configuration: immutableSnapshot + encodeOnce,
parallelBuild, pool transport, native ship updates, native GotoPlan, server GC,
NativeAOT and P863's tick budget. Keep the three rejected/default-off options
off. Compare the same clash population and match stages, not only overall run
averages: `[gcprof]` allocation/tick, generation counts, pause total/worst pause,
`step:update`, broadcast steps and tick p95/p99. Check retained heap after a
sector's peak population and after rotation, because pools retain capacity while
releasing model/hull references. Confirm no changed collision/damage/kill order
or wire receipts. The unarmed bench's 91.9% update-allocation reduction is not a
claim of 91.9% lower whole-server allocation at the rig; unchanged broadcast,
transport, mining, projectile and visibility work limits the real total.

## Native-intent footer

**Design:** preserve native's model traversal and ordered weapon/energy priority
(`Igc/clusterIGC.cpp:238–250`, `Igc/shipIGC.cpp:312–349`) and the gunner's copied
orientation value (`Igc/shipIGC.cpp:336–338`). These source lines were re-read
locally before citation. **Era constraint:** the C++ implementation's stable
object storage/value temporaries did not require rebuilding managed object
graphs each tick. **Bug:** no gameplay defect is reclassified or repaired here.
**Modern approach:** bounded owner-local scratch, complete reset, stable
snapshots and pre-change golden traces remove managed allocation while keeping
iteration, floating-point evaluation and RNG order intact. No native parity
rows or feature flags are appropriate for this refactor.
