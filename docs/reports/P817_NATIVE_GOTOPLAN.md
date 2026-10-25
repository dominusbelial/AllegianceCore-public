# P817 — experimental native NPC GotoPlan

Implemented on `codex/p817`; changes remain in the working tree. No commits,
pushes, remote hosts, rig changes, or new media. The feature is **off by default**.

`sim.nativeGotoPlan: true` enables native NPC travel. The environment variable
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN` overrides JSON (`1` enables; `0`, `false`, or
`off`, case-insensitive, disables). Docker Compose forwards it with default `0`.
Each mission prints the resolved setting at boot. Rollback is a config/env
change followed by restarting the server. Player autopilot and MCP pilots use
their existing controllers.

## Implementation and native scope

`src/Allegiance.Core/Sim/NativeGotoPlan.cs` is a pure value-input planner. Its
caller supplies target/bay/obstacle snapshots, visible graph edges and friendly
cluster flags. It produces controls, maneuver buttons, masks and completion;
waypoint state and graph workspaces are explicit. It does not mutate the world.
`Ship.NativeGotoPlan.cs` adapts the production mission to those inputs and
consumes completion in `ApplyNpcAutopilot`. The legacy path still runs when the
flag is off. Existing native mining and busy-building controls retain priority.

Native pilot values are in `Igc/igc.h:686-692`: miner (0), wingman (2), layer (5),
builder (6), carrier (9), player (10), cheat player (11). In native,
`shipIGC.cpp:2795-2833` runs GotoPlan after special mining/building/combat blocks;
`shipIGC.cpp:2353-2358` identifies wingman/cheat-player combat separately. The
port enables the component for **NPC miners, builders, layers, carriers and
NPC wingmen/cheat pilots during travel**. Their existing combat controller owns
engagement with an attack target. Human and MCP ships are excluded by `IsNpc`.
Explicit NPC Goto orders also retain their target type and ID in `GotoOrder`;
`ResolveDroneGoto` supplies these without changing the wire format. The adapter
re-reads ship position/sector and static object radius, uses Enter for explicit
warp/station/carrier goals, and completes an entered warp after the transit.
Station docking clears the matching experimental Goto order
(`Igc/shipIGC.h:459-478`). Legacy/player consumers continue to read the original
point fields. Builders enter asteroid strips; ordinary mining/layer rock goals use Goto;
station entry retains the native capability gate
(`shipIGC.cpp:3990-4053`). Pod pickup uses the native moving intercept. Visible point buoys retain the
native radius 20 (default constructor visibility); internal wingman investigation
buoys use radius 2. Native layers lay only on GotoPlan completion, at timeStart
before movement; the default-off lane retains its legacy reach-radius action.
These are pinned by a mine-creation/time/position regression, in addition to the
control-only tests.

The commander parity row remains `partial`: the opt-in implementation exists,
but ordinary/default sessions still use legacy steering. SIM-0475–SIM-0483 (renumbered at merge: SIM-0474 was taken by P813c)
track the executable experimental rules without claiming default rollout.

## Native behavior classification

All cites below are relative to the native `src/` tree.

| Behavior | Classification | Native source | Port / executable evidence |
|---|---|---|---|
| Reverse-thrust drag rest position, speed cutoff .1 | design | `Igc/common.cpp:1936-1983` | `DockApproach.StoppingDistance`; `Reverse_drag_stopping_distance` numeric cases |
| Collision sphere, rest sphere, pivot / kill throttle / finished | design | `Igc/common.cpp:1343-1379` | `GetGotoPosition`; `Goto_masks_preserve_current_and_rest_position_tests` |
| Bay/axis approach strip, least error, coast/pivot/charge, experimental angular threshold | design | `Igc/common.cpp:1157-1320,1385-1429,1505-1545` | `Approach`; builder/station mask tests; both P769 radius sweeps |
| Ship-ID center offsets use integer division and are always zero | bug, preserved | `Igc/common.cpp:1392-1398` | both asteroid strip centers remain zero; builder mask test |
| Fast warp/no-bay/probe entry | design | `Igc/common.cpp:1448-1454,1500-1524,1547-1559` | warp full-throttle and station no-bay tests |
| Moving pod intercept | design | `Igc/common.cpp:1430-1446` | `Pickup_predicts_intercept_and_flies_through_target` |
| Waypoint masks and cluster/target-cluster invalidation, warp-enter completion | design | `Igc/igc.h:5285-5362`; `Igc/common.cpp:1845-1933` | `Waypoints`; cluster-change test |
| Initial exit distance order, subsequent breadth-first expansion, coward/brave retry | design | `Igc/common.cpp:894-1011,1904-1907` | `FindPath`; visibility/initial-distance/coward test |
| Side-seen or personal-scanner visibility (signature GA, rear quarter range, flag carriers, asteroid LOS); unavailable target waits without Dodge | design | `Igc/shipIGC.h:341-389`; `Igc/igc.h:4909-4913`; `Igc/common.cpp:1873-1874` | adapter scanner snapshots; `Unavailable_target_waits_without_completing_or_steering` |
| Friendly-cluster territory and weighted visible ships | design | `Igc/common.cpp:388-433` | existing `Sector.IsFriendlyCluster`, with reusable territory workspace |
| Enemy ships ignored by drones; lower-ID allied drone right of way; rescue/carrier pairs and allied mines ignored | design | `Igc/common.cpp:1642-1690` | `FillNativeObstacles`, applying actual alliance groups and mining/ripcord latches |
| Earliest collision, acceleration horizon, orthogonal escape, mine speed >50 | design | `Igc/common.cpp:1693-1790` | `Dodge`; dodge and mine gate tests |
| Dodge before movement, waypoint skip / ships-only / NoDodge masks; face waypoint while thrusting sideways | design | `Igc/common.cpp:2000-2016` | `Execute`; directional-dodge test |
| RCS dominant-axis selection including ties | design | `Igc/common.cpp:1575-1636,2309` | `Direction`; wide-angle/dodge tests |
| Stationary obstacle tangent diversion | design | `Igc/common.cpp:2136-2226` | `Divert_uses_tangent_path_around_stationary_blockers` |
| Minefield throttle cap -.5 | design | `Igc/common.cpp:2248-2304` | minefield slowdown test |
| Rearward turn escape, angular stopping drift and normalized controls | design | `Igc/common.cpp:152-254` | shared `DockApproach.Face`; wide-angle turn test |
| Entry roll with angular stopping drift | design | `Igc/common.cpp:2090-2150` | `Roll`; station entry masks exercise roll branch |
| Ship speed matching when coasting / approaching | design | `Igc/common.cpp:2043-2063,2244-2262` | `Execute` ship-target branches |
| Boost request, strict <2000 cutoff against original target | design | `Igc/common.cpp:1813-1821` | `WantBoost`; 1999/2000/2001 boundary tests; adapter keeps original target during warp leg |
| Clear maneuver/coast/fire bits before applying goto | design | `Igc/common.cpp:1800-1825` | adapter maneuver mask |
| Builder completion latches drilling, roll 1, throttle 0 | design | `Igc/shipIGC.cpp:2810-2822` | completion adapter and inherited P769 actual collision/construction tests |
| Wingman retreat/resupply afterburner at full throttle | design | `Igc/shipIGC.cpp:2752-2754,2830-2833` | retreat/resupply latches applied after native movement; five-case boost/carrier theory |
| Typed command targets, live ship tracking, explicit warp entry and docking completion | design | `Igc/shipIGC.cpp:3990-4151`; `Igc/common.cpp:1850-1862`; `Igc/shipIGC.h:459-478` | `GotoOrder` metadata; production resolver / explicit-warp / moving-ship / dock-completion tests |
| Visible / hidden buoy radii and layer completion action at timeStart before movement | design | `Igc/igc.h:1921`; `Igc/buoyigc.cpp:44-48`; `Igc/shipIGC.cpp:2435,2730,2795-2828` | `Native_layer_waits_for_visible_buoy_stop_then_lays_before_movement_legacy_keeps_reach_radius` |
| Boost cutoff compares raw target coordinates even across clusters | bug, preserved | `Igc/common.cpp:1817-1821` | original-goal boost assertions in explicit-warp test |
| C++ intrusive lists/temporary waypoint arrays | era constraint, adapted storage | `Igc/common.cpp:894-1011,1385-1429,1525-1532` | retained per-ship arrays/lists and stack bay/axis spans; no planner tick allocation |
| Active equipment signature sums used by personal scanners | design; arithmetic preserved | `Igc/shipIGC.h:353`; `Igc/weaponIGC.h:109,122`; `Igc/afterburnerIGC.h:78,89`; `Igc/shieldIGC.h:72,76`; `Igc/magazineigc.h:74,78`; `Igc/dispenserigc.h:74,79` | `Ship.EffectiveSignature` replaces LINQ with loops and retains double accumulation / float sum boundaries |

## Compatibility boundaries

The existing parked-builder fallback remains for synthetic rocks without a
usable spin axis. Real native rocks require a unit axis
(`common.cpp:1178`, `:1399-1400`). For an axis-less fixture, the adapter uses Goto
so it can stop in the existing reservation envelope instead of flying endlessly
through a rock that cannot supply a valid entry strip. This is an explicit
fixture compatibility adaptation, not a claimed native zero-axis rule.

The existing ServerHost dock lifecycle still resets its plan/accepted slots
outside repair retreats (the documented P386 reduction); P817 adds matching
typed Goto completion without rewriting that older lifecycle.

The existing upstream ripcord selection/unsafe deferral remains unchanged. It
still runs before cross-cluster travel and retains its documented P172
simplification (unsafe pending ripcord holds rather than flying while pending).
Player/bot autopilot, drone economic decisions and combat laws are outside P817.

Obstacle snapshots follow the port's stable ships → rocks → stations → warps →
other runtime model categories. Native's intrusive pickable-list insertion order
is not stored by this world model; exact equal-time collision ties follow this
managed order. Pickable travel blockers include mines and probes; combat
projectiles/chaff and treasure pickup remain in their existing systems. The
native diversion fallback to a random direction for a degenerate zero cross
product (`common.cpp:2205-2208`) is replaced by deterministic +X. A coincident
blocking center is normally preempted by Dodge before diversion. These edge
adaptations, and the inherited upstream simplifications, are reasons to retain
experimental/default-off status pending the rig comparison.

## Tests and reference trace

The six existing economy/construction test classes now have inherited native-mode
variants, so their original assertions run with both flags:
`MinerDecisionCycleLiveTests`, `P652EconomyDroneAiTests`,
`P528bMinerSellRegressionLiveTests`, `BuildingConstructionLiveTests`,
`NpcBuilderDefaultOrderLiveTests`, and `P769BuilderCollisionBuildTests`.
The P528b fixture explicitly seeds a known warp map and a scouted mining field:
it has no scout clients. Its assertions, native rates/capacities and tick budget
are unchanged. Before this change that fixture succeeded because legacy steering
ignored warp discovery; native routing correctly waits for known exits/targets.

`tools/p817-gotoplan-proof/reference.cpp` independently evaluates the cited native
rest/mask/throttle and 1-D velocity formulas. Its checked-in trace is embedded in
`P817NativeGotoPlanTests`; the managed planner plus production `ShipKinematics`
must match controls, mask, velocity and position at **all 1,031 50-ms frames** of
mine approach → loaded warp travel → station sell approach (position/velocity
absolute tolerance .005). This is an independent **formula reference**, not a
capture of a running native game binary. Other unit tests cover 3-D turning,
Dodge, diversion, station masks, pod intercept, boost boundaries, flags and
allocations.

On the starting tree, the 80 existing economy/builder cases passed (zero skips).
No pre-existing failure in that baseline was classified as task-induced. New
planner tests require APIs absent on master; they are new coverage, not a claim
that those test binaries run unmodified against master.

| Check | Result |
|---|---|
| Solution build | 0 warnings / 0 errors |
| Tools build | all 77 projects built |
| NativeAOT | self-contained linux-x64 publish; no IL2026/IL3050; config-on/env-off boot checks passed |
| Exact-method repetitions | 127 methods; 405 invocations; 660 cases passed, zero failures/skips; each new/affected method at least 3× |
| Independent C++ trace | regenerated byte-for-byte; all 1,031 managed trajectory frames matched |
| Compose / glossary / parity | config valid; all glossary links valid; both CSV matrices validate with LF |
| Fast tier (one invocation) | 10,458 passed / 6 failed / 3 skipped; all six failures were environment-context cases, audited below |

The three skipped Fast cases require the optional private Meshy pack; none is
P817 coverage. [Per-method counts](P817_assets/isolation.json),
[Fast summary](P817_assets/fast-summary.json), and
[verification metadata](P817_assets/verification.json) preserve the results.

The Fast failures were existing `PlayerBundlePathsTests` cases (five failing
parameter cases across four methods) and
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`.
Keeping TMPDIR under the worktree made the bundle fixtures inherit the outer
repository's local.env; the profile test hit the sandbox's read-only user config.
All affected methods passed 3× under the intended fixture context, with unchanged
assertions and no resolver/profile source changes: scratch bytes stayed under
this worktree, a proc directory-handle alias supplied the non-repository lexical
temp context, and XDG_CONFIG_HOME pointed to writable scratch. The profile test
also passed 3× with writable XDG config alone. These are environment limitations,
not a clean broad-run claim. See [context audit](P817_assets/context-audit.json)
and the P817 entry in `docs/TEST_FLAKES.md`.

Parking audit: the closing five-run series passed **5/5** (flag off ×3,
flag on ×2), reaching 24/25/24/24/24 out of 30; its longest
stopped-with-target interval was 3.911 s. The initial five observations
were 4 pass / 1 arrival-count failure (flag off), with the stopped-with-target
condition passing in all five. The unchanged starting revision
`041e23a0189c83cc29e35d13008f0e32a72af9bf` passed its three isolated observations
(reached 24/24/25 out of 30; longest stopped-with-target 1.613/1.819/1.518 s).
The initial miss reached 21/30 and had a longest stopped interval of 2.105 s;
it was not a stopped-with-target >5 s failure. The arrival miss passed its three
closing flag-off repetitions; it was not
reproduced in the three baseline runs. The closing series used the build before
the final NPC retreat/resupply boost correction and pure boost consolidation;
those final changes passed their focused 3× checks, including player exclusion
and zero-allocation coverage. Full summaries are in
[parking evidence](P817_assets/parking.json).


## Local comparison

| Metric | Flag off | Flag on |
|---|---:|---:|
| Observed simulated seconds | 2400.0 | 2400.0 |
| He3 delivered | 540.00 | 697.50 |
| He3 / simulated minute | 13.5000 | 17.4375 |
| Completed loaded trips | 7 | 9 |
| Mean loaded trip (s) | 245.01 | 210.11 |
| Mean outbound trip (s) | 205.99 | 200.59 |
| Miner deaths | 0 | 0 |
| Constructor arrivals | 2 | 2 |
| Mean tick (µs) | 288.72 | 361.94 |
| Tick p95 (µs) | 568.90 | 549.30 |
| Mean whole-tick allocation (bytes) | 185343.57 | 180287.34 |

Final comparison: [raw small JSON](P817_assets/comparison.json). Delivered He3
increased 29.2%; the mean loaded trip was about 14.2% shorter in this window.
Timing was noisy on the shared machine: another local pair reversed the
mean-tick ranking. These numbers do **not** establish a stable throughput win
or replace the rig acceptance run. Whole-tick allocation totals are not the
planner-only allocation measurement.

The reproducible comparison command is:

```sh
~/.dotnet/dotnet run --project tools/p817-gotoplan-proof -c Release -- \
  --seconds 2400 --output result.json
```

Unset `ALLEGIANCE_SIM_NATIVE_GOTOPLAN` for this config-driven A/B command; the
harness rejects an environment override that defeats either half. It boots the
real server and `m2xspiderw.igc`, seed 817, with 13 NPC wingmen + one initial miner
+ one constructor per side (30 total), known warps/field, no connected clients,
native economy rates, and time dilation explicitly off. He3/min uses observed
mission simulation time rather than wall time or a nominal tick count. Home clusters have no buildable rocks, so one
rotating radius-200 construction fixture per side measures actual constructor
arrivals. It observes 2,400 simulated seconds per setting, excludes the first
10 seconds from timing/allocation statistics, and records loaded and outbound
trip durations separately. Ore is counted at real dock-sale transitions. This
is a local NPC travel/economy run, not a 200v200 MCP rig battle.

Whole-tick allocations include pre-existing mission/visibility/economy work and
route-changing events. Pure control/route loops and warmed production adapters
(including repeated unreachable-route retries) separately assert zero allocated
bytes. Per-ship buffers may allocate on first use or capacity growth; no LINQ
iterators, obstacle generators, per-tick bay lists or directional-sort arrays
were introduced. The scalar signature sum, indexed ripcord target lookup and
reusable friendly-cluster workspace remove allocations on those paths.

## Claude-only 221 rig recipe

The lane rules reserve remote hosts and the two 200v200 matches for Claude. No
remote host was contacted. Use the usual rig launcher and identical scenario,
map seed 817, core, civs, rates, duration, transport options and build for both
matches; change only `ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0` versus `1` (or the JSON
flag). Restart between matches and retain each boot banner and profiler CSV.
Use a scouted/known field in both matches or keep the same scout schedule.

Concrete server settings (merge into the same rig config for each run):

```json
{
  "benchmarkSeed": 817,
  "sim": { "nativeGotoPlan": false },
  "profiler": { "enabled": true, "reportIntervalSeconds": 5, "csvPath": "p817-ticks.csv" }
}
```

Run that same published binary locally on the rig with
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0 ./Allegiance.Server --config server.json`,
then repeat after restart with `=1`, keeping separate logs/CSV files. Preserve
native ship-update mode, existing time dilation policy, fog and the same scout
schedule. On Claude's bot host, submit this to the existing MCP driver's command
file after it has initialized:

```json
{"tool":"spawn_scenario","args":{"preset":"showcase","count_a":200,"count_b":200,"target_sector":"Alderan","mode":"march","staging":true,"finale_after_minutes":30},"timeout":1800}
```

Use `count_a`/`count_b` for exact 200v200; `size_scale:13` is only 195v195.
Let each match reach game over; use the same next-match/research configuration
for both. No part of this recipe was executed on a remote host by Codex.

Record miner deaths, He3 actually sold per simulated minute, outbound/loaded
miner trip distributions, constructor launch/arrival/completion counts and
elapsed times, and server tick p50/p95/p99. Include enough duration for at least
two loaded returns by each surviving initial miner. Normalize against simulated
time, record sim time dilation and the number of miners alive, and compare
p95 only over matched load/phase windows. Reproduce any missed arrival or
persistent obstacle dodge using its ship ID, route and waypoint mask. Keep the
feature default off until that acceptance comparison is reviewed.

## Evidence and repository hygiene

Raw logs/TRX and verification scripts are in [`.scratch/p817/`](../../.scratch/p817/) in this worktree,
which is already gitignored. The sandbox permits writes only to this worktree
and scratch, so raw evidence could not be moved to the lane's external
`~/allegiance-evidence/P817/` location. Small public summaries are under
`docs/reports/P817_assets/` (well below 2 MB); no screenshots/media were added.
