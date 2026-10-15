# P694 — station death and next-match mass launch

Date: 2026-10-15. Worktree: `codex/p694`, during the `integ9` merge.

## Result

Both controlled local reproductions meet the **under 100 ms** target with the
parallel flag off and on. Three isolated invocations produced **126 timed
updates**, worst **50.324 ms** across two sectors; the final full Core run's
42 corresponding samples peaked at **27.522 ms**. Native launch placement is
ported, and P667's byte-identical determinism gate remains green.

Changes are left in the working tree. No commits, pushes, staging, or other Git
writes were performed. Both conflicted source files have been edited to remove
all conflict markers; the index intentionally remains unmerged for the dispatcher.

## Reproduction

`P694MassEventTickTests` boots a real server with `PCore001.igc`, real CVH collision
hulls, and `XXV_simple.igc`. Each measurement updates **two crowded sectors** in
sequence for 50 ms of simulation, including their synchronous server-site
callbacks. Crowding hull drain is disabled, matching the supplied live run.

The mass-launch case has 195 same-side fighters at each garrison's first launch
point, either exactly coincident or with independently seeded +/-5 coordinate
jitter. Ships face their launch direction and travel at 50 units/s. A separate
200-per-side case calls the new native station-launch routine, rotating through
`ss24a`'s two actual launch slots, with its rapid-launch offset and jitter. These
are 390 and 400 simultaneously launched ships, respectively.

The station-death cases use the same crowds, plus **12 opposing bombers per
sector**, 150 or 195 projectiles and the same number of missiles per sector, and
a separate lethal projectile aimed at the station. The native-slot variant uses
200 of each ordnance type per sector. The enemies fly against the launch velocity;
ordnance is concentrated at the first launch point. Projectiles use a loaded type
with power 1, zero blast power and ten-second lifespan; loaded missiles have
zero blast power and a ten-second readiness delay/lifespan. The station-killing
projectile has explicit synthetic power `1e9` and no blast. Both real station
runtimes must die through collision dispatch. This does **not** invent a damaging
station-death explosion. P689's existing lethal splash/secondary-explosion/loot and
two-real-TCP-observer case remains part of validation.

The baseline initially explored 24 attackers per sector. That larger overload
reproduced approximately 1.1–1.3 second Debug updates with the old launch-point
orientation and filtering, dominated by `combat.detect` and physical detection /
separation. The final density matrix uses 12 attackers, not 24; the 24-attacker
exploration is not silently counted as an under-100-ms success. This is a synthetic
reproduction of the supplied geometry and density, not a replay of clash46b's
unavailable individual trajectories, loadouts, timing, or 392 network peers.

The matched **algorithm ablation** temporarily restores P667's ordinary per-pair
solves / concurrent result bag, the original broad-phase global tuple sort and
queue sort, and the old separation/no-op work. It retains P689's filters and
instrumentation and uses the same final 12-attacker geometry. Native-slot cases
use native slot geometry on both sides of this comparison; this comparison does
not separately quantify the launch-rule correction. The baseline's timing ceiling
was temporarily 5 seconds so the slow samples could complete; the final test is
restored to 100 ms. Source restoration ran in `finally`, followed by a full build.
Commands are in [matched-before-commands.json](P694_assets/matched-before-commands.json).

The final matched baseline and fixed measurements both use Release and the
**default tiered JIT**, the same prepared fixture, geometry, and timed boundary.
Earlier exploratory measurements with `DOTNET_TieredCompilation=0` are tagged as
exploratory in the data, rather than used for this comparison. Hardware/runtime
details are in [environment.json](P694_assets/environment.json): Intel Core Ultra
9 285HX, 24 available CPUs, .NET 10. Parallel measurements use four workers. Final fixtures configure the flag and worker count through `ServerConfig`
and assert the resulting values after boot. Earlier profiles used an explicit
post-boot override, so their boot-label flag is stale; their per-sample
`P694 ... parallel=...` field records the actual mode.

Warmup repeatedly constructs and updates fresh worlds for at least one second per
case, allowing the default tiered JIT to promote numerical helpers before measuring
three more fresh worlds. Boot, core parsing, warmup, profiler CSV output, and socket
setup are outside the stopwatch. Unmeasured boot/warmup console output is silenced.
Each measured world receives a full collection **before** timing to remove the
unreachable core/map/server garbage produced by importing earlier fixture worlds
and age its live objects, matching an already-running server. Measured callback
logging remains enabled. All in-tick allocation/GC remains inside the wall clock;
allocation, collection-count and GC-pause deltas are captured immediately after the
stopwatch stops, before profiler formatting or test output. Time includes both `Sector.Update` calls, physics,
combat, callbacks, loot, and ordinary simulation passes. It excludes the rest of
`SimPass`, broadcast fanout to 392 peers, and the rig itself. The new test retains a
strict **100 ms measured-update assertion**; all gameplay checks also run on every
warmup world.

Exploratory trials before this preparation had intermittent 100–161 ms outliers.
Repeated warmup imports had also queued tens of megabytes of test output. The
first unprepared three-invocation matrix series contained three overruns among
122 completed timed updates (maximum 142.581 ms); one nominally identical run spent 91.4 ms in treasure
callbacks versus about 0.2–1.3 ms in adjacent samples. Another had an approximately
80 ms gap outside the instrumented sub-steps. These observations do not distinguish
GC, output backpressure and scheduler delays. Both unprepared and prepared data are
retained. This fixture correction changes no measured gameplay work, assertions,
timeouts or the 100 ms ceiling. It does not claim to remove production GC pauses.

## Measured result

All **126 prepared timed updates** (14 cases, three fresh samples, three isolated
invocations) passed the strict 100 ms guard. The maximum was **50.324 ms** across
both sector updates. Their recorded in-tick collection counts and GC pause deltas
were all zero. Tables show milliseconds; baseline medians use three samples per
case and fixed medians/maxima use nine.

| Relaunch scenario | Before, off median | Fixed, off median / max | Before, on median | Fixed, on median / max |
| --- | ---: | ---: | ---: | ---: |
| 195/side, one point, +/-5 jitter | 73.391 | 0.989 / 1.039 | 53.777 | 0.965 / 1.070 |
| 195/side, exactly coincident | 74.152 | 1.042 / 1.143 | 74.745 | 0.688 / 0.722 |
| 200/side, actual native slots | 38.589 | 16.897 / 17.106 | 38.877 | 19.995 / 26.106 |

| Station death, plus 12 enemies and N of each ordnance/sector | Before, off median | Fixed, off median / max | Before, on median | Fixed, on median / max |
| --- | ---: | ---: | ---: | ---: |
| N=150, one point, jitter | 198.325 | 36.798 / 42.584 | 82.215 | 15.863 / 16.717 |
| N=195, one point, jitter | 315.993 | 25.473 / 25.761 | 149.110 | 18.914 / 22.282 |
| N=195, exactly coincident | 518.952 | 33.050 / 50.324 | 201.864 | 14.315 / 14.721 |
| N=200, actual native slots | 172.537 | 30.870 / 35.160 | 69.611 | 12.083 / 12.408 |

For the 195/side jittered station-death case, median **serial** `combat.detect`
fell from **256.364 to 8.218 ms**; ship detection from **24.460 to 10.473 ms**;
ship response from **31.839 to 4.228 ms**. Combat dispatch itself stayed about
2 ms. This reproduces and removes expensive geometric work, rather than treating
station death as an unproven damaging explosion cascade.

Representative median process-wide allocation deltas during the two updates:

| Station-death geometry | Off: before → fixed | On: before → fixed |
| --- | ---: | ---: |
| 195/side, jitter | 33.98 → 22.88 MiB | 46.81 → 27.99 MiB |
| 195/side, coincident | 40.12 → 35.22 MiB | 64.50 → 39.76 MiB |
| 200/side, native slots | 16.98 → 13.83 MiB | 23.65 → 16.61 MiB |

These remaining allocations and live network/GC/scheduling tails still matter to
rig acceptance. The result is the stated local two-sector reproduction, not an
end-to-end 392-peer latency guarantee. Parallel mode is not always faster for
small/no-op work; serial is independently within budget.

Raw per-sample buckets, allocation/GC deltas and exploratory data are in
[profiles.json](P694_assets/profiles.json), with compact
[timings-summary.json](P694_assets/timings-summary.json).

## Implementation

The merge resolution combines P667's parallel switch with P689's pair predicate
in one detector API. Combat, ship/asteroid and ship/station passes use the same
immutable type filters in both flag states. P689's nested profiling is retained;
new `collision.{ship,asteroid,station}.{detect,dispatch}` buckets separate expensive
geometric detection from physical response. Broad-phase construction remains
serial; narrow-phase work and swept-bound setup remain parallel when enabled.

1. **Reuse identical geometric solves.** Each detection call groups colliders by
   bit-identical position, velocity, hull reference, scale, working shape,
   effective radius and orientation basis. Signed zero is distinguished. Each
   ordered geometry pair is solved once. Results are expanded onto every original
   collider identity and ordinal, including misses and exact time ties. Gameplay
   state, side, launcher, damage, and mutable dispatch gates are not cached or
   filtered. The serial path streams candidates without a full pair array or
   expansion map. Parallel workers write disjoint result slots, then the sim
   thread expands results in original pair order. The cache dies with the call.
2. **Reduce sorting work.** Sweep-and-prune still yields the exact brute-force
   swept-AABB candidate set. It buckets second registration indices under their
   first index, avoiding a large tuple sort. Detection's one-event-per-pair stream
   is already in ordinal order; time buckets preserve that order for ties. The
   public general-purpose collision queue keeps its full original comparator.
3. **Skip physical no-ops.** At dispatch, identical *current* velocities make the
   existing relative-normal approach gate return for every possible separating
   normal. That check now precedes expensive separation. If the entire flying
   fleet has one velocity, no collision in that pass can change that invariant,
   so detection itself is unnecessary. Equal initial velocities are **not** a
   pair filter in a mixed fleet: an earlier impact can change a later response.
   This rule is independent of side and is an optimization of the port's existing
   approach gate, not a new friendly-ship immunity.
4. **Reuse separation support offsets.** The 27 native probe directions have pure
   support offsets for a fixed hull/scale/relative-orientation/sub-shape pair.
   A per-pass cache saves those offsets while retaining the original arithmetic
   order, probe order, position-dependent scores, initial normal and coincident
   random perturbation. It has at most 256 entries; unfamiliar pairs after that
   use the original solver. No normals, displacements or gameplay responses are
   approximated or carried across ticks.
5. **Port station launches.** The former radial `DockRadius + 50` placement sent
   same-position ships down one direction, ignored model slots, preserved their
   old attitude, and could place them inside the station. Stations now own their
   native slot counter and last-launch time. Shared undock / NPC / station-ripcord
   repositioning delegates to this launch routine. Station geometry reconstructs
   up to six launch frames as well as the existing landing planes. The landing
   cache's no-landing-bay contract remains intact for launch-only geometry.

No GJK iteration limit, damage formula, explosion scheduling, collision tie order,
ship collision immunity, launch queue, artificial stagger, or ship-separation cap
was added. Actual launch positions, attitudes and turn rates change where the old
port differed from native. The existing eight-second re-dock latch is retained;
it is not ship/ship collision immunity.

## Native rules and evidence

Paths are relative to `~/Development/Allegiance/src/`; line numbers were read from
that checkout, rather than copied from older reports.

- **Player launch goes straight to station launch:**
  `FedSrv/FedSrv.CPP:6282-6291` clears the docked station after a successful launch
  buy. `FedSrv/FedSrv.CPP:13184-13190` repairs/refuels and calls the server ship's
  launch in the undock callback. `FedSrv/fsship.cpp:175-183` calls
  `pstation->Launch(m_pShip)` immediately. There is no added launch queue here.
- **Model slots and scale:** `Igc/stationtypeigc.cpp:40-66` loads `launch1`,
  `launch2`, etc., scales positions by station radius / original model radius,
  and keeps frame directions. `Igc/igc.h:29` supplies the six-slot cap.
  `Igc/stationIGC.cpp:254` initializes the rotating launch counter. The frame
  coordinate flips are `_Utility/HitTest.cpp:796-801` (position X, forward Y/Z).
- **Jitter, slot rotation and rapid-launch offset:**
  `Igc/stationIGC.cpp:299-307` adds independent +/-5 coordinate jitter and reads
  `c_fcidExitStationSpeed`. `:350-366` transforms the selected slot and direction
  by station orientation, uses `shipRadius + speed*0.85 + 5` if the last launch
  was within **0.1 seconds inclusive**, otherwise `shipRadius + speed*0.5 + 5`,
  then advances the slot modulo its count. This is an offset adjustment, not a
  delay and not guaranteed non-overlap of 195 simultaneous ships.
- **No-slot fallback:** `Igc/stationIGC.cpp:311-346` gives NPCs at ripcord-capable
  stations a random direction; other ships rotate right/up/-right/-up with the
  native case-4 reset/fallthrough. That reset repeats right at the wrap; the port
  preserves it. Fallback distance is station radius + ship radius + speed*0.5 + 5.
- **Launch attitude and velocity:** `Igc/stationIGC.cpp:369-382` aligns the ship
  to the launch direction with station forward as its up reference, sets outward
  launch velocity, clears all three turn rates, moves sector membership, and
  records ship/station launch times. Ship radius is half hull length
  (`Igc/shipIGC.cpp:1419`). Random fallback direction follows the uniform sphere
  construction in `zlib/vector.cpp:37-47`. The port stores time in its simulation
  clock; membership and networking remain in the existing callers.
- **Station ripcord shares this rule:** `FedSrv/FedSrv.CPP:13440-13447` calls
  station `Launch` directly. The existing live ripcord test now checks native
  slot geometry and jitter, replacing its old assertion of an arbitrary
  250-unit radial offset.
- **No post-launch ship/ship immunity found:** station launch (`stationIGC.cpp:
  299-382`) sets no collision-exclusion state. Ship collision dispatch in
  `Igc/shipIGC.cpp:1023-1198` contains friendly rescue/carrier handling and physical
  response, not a last-launch-time gate. `GetLastTimeLaunched` is exposed in
  `Igc/shipIGC.h:1376-1382`; searching its native call sites found no collision
  consumer. `SetNoHit` instead belongs to launcher/projectile/missile exclusions
  (`Igc/projectileIGC.h:81-92`, `Igc/missileigc.cpp:99`) and building effects,
  rather than a general ship launch grace period. No immunity was invented.
- **Physical separation:** `Igc/shipIGC.cpp:479-616` contains the 27 probes,
  coincident-position random perturbation, convex supports and +0.5 clearance.
  The cache preserves that computation. The zero-approach debounce optimized
  here was already in the port; this task does not claim to add it from native.
- **Station death is not a damaging blast:** `Igc/stationIGC.cpp:189-194` calls
  `KillStationEvent`; `FedSrv/FedSrv.CPP:12237-12392` vacates and terminates the
  station without a damage-radius explosion. Its visible explosion is client
  `clintlib/appmsg.cpp:520-528`. Existing P689 blast instrumentation and tests are
  retained for a weapon's own splash and missile secondaries.

## Validation and artifacts

`P694CollisionGeometryTests` compares filtered detector output with an independent
brute-force candidate stream and uncached `NarrowPhase.TestPair`, including real
multi-hulls, sphere ordnance, working-shape changes, mutable positions/velocities/
attitudes, signed zero, original identities and ties. It checks flag off and
thread counts 1/2/4/8. Another test compares **all nine float bit patterns** in
separation results against the uncached solver for 1,500 cases, including repeated
keys, sphere/ellipse/point/hull shapes and cache saturation.

`P694StationLaunchTests` checks all six slots and wrap, first/rapid/isolated launch
offsets, deterministic jitter bounds, attitude, velocity, turn reset, timestamp,
real station-frame scaling, and native no-slot direction cycling.
`P689CollisionPairTests` now also checks the parallel filter at 1/2/4/8 threads.
The existing P667 140-tick whole-world digest gate remains enabled.
Synthetic-station helpers in the live ripcord/teleport/membership/timer fixtures
now register both records and runtimes with `Mission.PlaceStation`. A flight test
now projects commanded displacement onto the native launch-forward direction
rather than assuming global -Z; its five-unit distance checks are retained.

Final commands, counters and nonpassing details are recorded in
[P694_assets/verification.json](P694_assets/verification.json). The complete attempt
history is retained, including corrected fixture failures and load-flake audits.
[coverage.json](P694_assets/coverage.json) verifies no selected method is missing
three successful isolated runs; exact lists are
[Core](P694_assets/isolated-methods.json) and
[ClientCore](P694_assets/isolated-client-methods.json).
Raw logs and TRX files are under `/tmp/p694-verification-final/` on this machine.
Rig / 221 / 200v200 acceptance remains the dispatcher's lane.

## Verification results

The final functional build passed with **zero warnings and zero errors**; see
[build.log](P694_assets/build.log). The merged P667 benchmark project also builds
cleanly: [p667bench-build.log](P694_assets/p667bench-build.log). NuGet HTTP cache writes were directed to
`/tmp/p694-nuget-http`, preserving the sandbox's read-only home cache policy.

**166 directly affected Core methods** and **five ClientCore methods** each have
at least three successful fresh method-filtered invocations (theory filters include
their rows). This includes all new tests, the collision/blast/physical response
checks, real TCP station-death observers, launch/dock/ripcord/NPC paths, P667's
140-tick digest at 1/2/4/8 threads, P689's filter oracle, and the two-match /
three-match relaunch proofs in both ship-update models with pipeline off/on.
The real 15v15 showcase passed three isolated runs. The P682b lifecycle unit cases
also passed three runs each.

The unfiltered suites, run sequentially after the build, produced:

| Suite | Passed | Suite failures | Existing skips |
| --- | ---: | ---: | ---: |
| Core, final prepared fixture | 4,582 | 2 load flakes | 0 |
| ClientCore | 1,008 | 2 load flakes | 0 |
| MonoGame | 2,195 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

**Each of the four failed methods passed 3/3 fresh isolated reruns, unchanged.**
Core's failures were a raw-peer join callback timeout and an immediate
healthy-peer-update assertion in the stalled-console test. ClientCore's failures
were the already-known session-loss callback race and a bounded two-hop approach
loop ending one sector short. The prior unfiltered Core run with the same
production changes passed **4,584/4,584**. The lane's load-flake rule was applied;
no transport, flight-law, timeout, or quarantine change was used to hide failures.
Details are in [TEST_FLAKES.md](../TEST_FLAKES.md) and the full attempt ledger.
The three MonoGame skips are the existing optional private `MeshyPackTests` asset
pack, which is absent.

The deterministic synthetic-anchor missing-runtime failure and the old flight
test's global -Z assumption were corrected, rather than classified as flakes.
The final prepared benchmark kept the strict 100 ms guard and passed all cases
3/3, including the previously intermittent rows.

All five additional local opt-in drivers completed with their flags enabled and
one passing test each:

| Driver | Observed wall duration |
| --- | ---: |
| Normal-map capture attempt | 301.456 s |
| Debug-map capture attempt | 301.189 s |
| 20 Hz log baseline | 241.887 s |
| 10 Hz log baseline | 242.586 s |
| Side-dependent acquisition/transit diagnostic | 122.235 s |

The last three observational windows overlapped in independent local server/test
processes; acceptance timing and the required suites had already completed and
ran serially. The 20/10 Hz drivers recorded zero shots/hits, respectively 11/8
collisions, and 0/3 ejections and pod docks. They are diagnostic observations,
not an asserted combat-success gate; P667/P682's separate real-kill assertions
passed. The acquisition driver's complete state/transit trace is archived as
[acquisition-transit.txt](P694_assets/acquisition-transit.txt).

Local opt-in drivers are executed separately from acceptance timing and the
unfiltered suites. Their observational outcomes are archived in
[proof-outcomes.json](P694_assets/proof-outcomes.json). Both 300-second capture
runs returned their real wire outcomes: station reached and full-shield refusal
confirmed, with `captured=false` and no shield-down event. A successful capture
is **not** claimed from those observational drivers. Rig / 221 / 200v200 opt-in
drivers remain gated as instructed.

To rerun the main local proof:

```bash
set -a
. ./local.env
set +a
NUGET_HTTP_CACHE_PATH=/tmp/p694-nuget-http dotnet build -c Release -m:8
NUGET_HTTP_CACHE_PATH=/tmp/p694-nuget-http dotnet test tests/Allegiance.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~P694' --logger 'console;verbosity=detailed'
```

## P694b — loaded-machine cost and a repeatable performance guard

The corrected-fixture Release cohorts have a **47.780 ms worst measured
update across both sectors**, including fresh-process invocations of the exact
150/195 serial review rows. The median guard is **100 ms in Release / 200 ms in
Debug**, with fixed preparation for the already-running-server scenario.

Date: 2026-10-15. All changes are left in the working tree. No commits, pushes or
staging were performed. This section supersedes the earlier claim that each
individual sample must meet the same 100 ms ceiling in every build configuration.

### Diagnosis and fixes

The original test reproduced the review failure in **Debug**: the serial jittered
station-death case took **132.249 ms at 150 ships/side** and **121.358 ms at 195**,
with zero in-tick collections or GC pause. At 150 the ship-detection / combat-
detection / ship-dispatch buckets were 86.611 / 20.789 / 17.573 ms; at 195 they
were 53.036 / 31.699 / 24.811 ms. These are substantial numerical-work costs,
not evidence that the review's three consistent failures were scheduler flakes.
The earlier report's approximately 50 ms result was **Release**. The initial
original-source Debug and Release matrices overlapped on the busy host, so they
are configuration diagnosis, not a matched speedup claim.
[Baseline output](P694b_assets/baseline.json) and
[environment](P694b_assets/environment.json) preserve the evidence.

The remaining algorithmic changes are:

- Stream dense broad-phase candidates directly in registration order instead of
  storing and sorting every second-index bucket. Coverage by at least three
  quarters of the intervals at a sweep coordinate selects this algorithm; sparse
  populations retain sweep-and-prune. Both apply the same strict three-axis
  overlap predicate and original-order pure pair filter.
- Use a directly indexed serial geometry-result table for at most 256 groups
  with at least two colliders/group on average. Its maximum is 65,536 cells;
  tested misses are separate from untested cells. Larger or weakly repeated
  sets retain the sparse dictionary; unique geometry needs no result cache.
  Ordered A/B keys, original identities and per-call cache lifetime are retained.
- Leave already time-ordered event streams in place, avoiding the extra time-
  bucket copies. Unordered streams retain the existing deterministic sort.
- Load support-walk direction components once and read immutable coordinate
  arrays, keeping float arithmetic order, adjacency order, strict improvement
  and tied-vertex selection. Native's strict `dot > dotMax` and walk are at
  `~/Development/Allegiance/src/_Utility/HitTest.cpp:526-546`; its float dot
  arithmetic is `~/Development/Allegiance/src/zlib/vector.h:164-167`.

No gameplay rule was introduced. Shapes, hull-solving convergence criteria,
collision times, damage, launch geometry, population density and dispatch gates
remain unchanged. Temporary fine-grained profiling scopes were removed.

### Fixture correction and assertion

A final check matching the review's **single theory row in a fresh process**
exposed a second problem: the initial one-second preparation still left the
Release workload slow, despite the whole matrix appearing warm. One 195-ship
run had a **108.741 ms median**, and 150-ship medians were 86–93 ms. This
intermediate failure was investigated and fixed, rather than classified as a
load flake. The one-second interval included cold core/map/server imports and
was insufficient preparation for the long-running-server scenario.

Preparation now always runs fresh worlds for **at least five seconds and at
least ten worlds** before measuring. This is a fixed time/count policy, never a
"warm until fast" condition or a failure-triggered retry. Default tiered JIT/PGO
settings are retained. Every warmup world still runs the gameplay assertions.

Each row measures **five more fresh worlds** and asserts their **median**.
All samples, allocations, GC deltas and profiler buckets are logged. The
stopwatch still includes **both sector updates together**, synchronous callbacks
and in-tick GC. Fixture import and the existing pre-measurement collection remain
outside it. No samples are discarded or retried.

The regression bound is **100 ms in Release** and **200 ms in Debug**. Debug's
unoptimized numerical code has materially higher cost; its guard detects sustained
large regressions. Release is the production performance acceptance configuration
and must be run explicitly with `-c Release`. Five-sample medians tolerate up to
two isolated scheduling/GC tails while still rejecting sustained overruns. This
does not claim to make Debug run below 60 ms or to bound a cold server's first tick.

### Final measurements

Both review rows passed **three individual fresh-process invocations per row,
per configuration**, with the corrected preparation. Each table row has 15 timed
worlds. Times are milliseconds:

| Build | Ships/side, serial station death | Range of invocation medians | Worst sample |
| --- | ---: | ---: | ---: |
| Release | 150 | 25.897–38.678 | 47.780 |
| Release | 195 | 23.717–24.136 | 28.476 |
| Debug | 150 | 104.539–107.125 | 108.902 |
| Debug | 195 | 95.447–97.885 | 99.816 |

The complete 14-scenario Release matrix also passed **three fresh exact-method
invocations**, totaling **210 timed two-sector updates**. It retains flag off/on,
150/195/200 populations, jitter, coincidence and actual native launch slots.
Serial station-death rows have these results across 15 worlds each:

| Scenario | Median | Worst sample |
| --- | ---: | ---: |
| 150/side, jittered launch point | 26.884 | 31.616 |
| 195/side, jittered launch point | 24.912 | 29.287 |
| 195/side, coincident | 13.255 | 14.073 |
| 200/side, native slots | 19.634 | 20.149 |

The worst individual Release sample across the corrected-fixture single-row and
matrix cohorts was **47.780 ms**. The requested serial production cases
therefore meet the comfortably-under-100-ms target on this loaded machine.
Invocation loads, sample timings and sub-step profiles are retained in
[verification.json](P694b_assets/verification.json), with separate single-row,
matrix and full-suite cohorts in [timings-summary.json](P694b_assets/timings-summary.json).
Earlier one-second-preparation results remain explicitly tagged as superseded.

### Correctness and checks

The six new theory cases compare dense/sparse filtered candidates against the
brute-force stream, direct/sparse geometry caches against uncached narrow-phase
events with flag off and 1/2/4/8 workers, and common/mixed-time queue ordering
against the full comparator, including signed zero, NaN and infinity. Existing
P689 filters, P694 geometry/separation checks and P667's 140-tick whole-world
byte digest remain enabled.

A separate harness compiles the original committed Collider/NarrowPhase/Separate
under different type names and compares against the working-tree Core binary.
**Three Release and three Debug runs each passed 168,000 support comparisons,
12,000 narrow-phase results and 2,501 deterministic separations**, checking every
result float bit. Coincident random perturbations are excluded from that
comparison; the real coincident mass-event cases still run, and the perturbation
code is unchanged. Reproduce after both builds with
`python3 docs/reports/P694b_assets/solver-oracle.py`;
[results and baseline commit](P694b_assets/solver-oracle.json) and
[harness](P694b_assets/solver-oracle.cs) are retained.

Both solution builds passed with **zero warnings and zero errors**:
[Release](P694b_assets/build-release.log), [Debug](P694b_assets/build-debug.log).
After the final Core-only fixture correction, both Core test builds also passed
cleanly: [Release](P694b_assets/build-fixture-release.log),
[Debug](P694b_assets/build-fixture-debug.log). Commands use
`NUGET_HTTP_CACHE_PATH=/tmp/p694b-nuget-http` and `-p:UseSharedCompilation=false`.

**169 Core methods and 5 ClientCore methods each passed three
exact-method isolated invocations**. The changed mass-event method's current
fixture was rechecked three times as a full Release matrix, and both critical
rows were individually checked three times in both builds. The local 15v15
showcase and real MCP two-match/three-match combat checks passed three times each.
[Coverage](P694b_assets/coverage.json), [invocation ledger](P694b_assets/verification.json)
and [source fingerprints](P694b_assets/source-hashes.json) make the selected runs
reviewable.

The final unfiltered suite results are:

| Suite | Passed | Failed in full suite | Existing skips |
| --- | ---: | ---: | ---: |
| Core | 4590 | 0 | 0 |
| ClientCore | 1010 | 0 | 0 |
| MonoGame | 2195 | 0 | 3 |

The Core suite was rerun after the fixture correction. ClientCore and MonoGame
results remain applicable: only the Core test preparation changed, and the shared
Core DLL and those test assemblies remained byte-identical, verified by
[SHA-256 fingerprints](P694b_assets/unchanged-product-binaries.json). Both full
four-minute P539 combat recordings and the opt-in P660 showcase were enabled.
The three MonoGame skips require the existing unavailable private built Meshy
pack. Rig / 221 / 200v200 acceptance remains in Claude's lane and was not run here.

The first MonoGame attempt exited with code 1 after 2,175 logged passes and three
skips, without a TRX, terminal summary or reported failing assertion. Its cause
was not established. The complete unfiltered Debug retry with crash/hang
diagnostics passed all 2,195 runnable cases, including the two-warp pod flight
with zero rejected updates and zero resets. Both attempts remain in the ledger;
the table uses the completed retry, not the partial count.

Every method that failed during an unfiltered suite passed three fresh isolated
Debug reruns after the final suite completed:

| Full-suite failure | Symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Assert.Equal() Failure: Values differ; Expected: 11; Actual:   26 | 3/3 pass |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 pass |

These satisfy the brief's load-flake rule and are recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md). No transport/mining behavior, receipt deadlines,
quarantine traits or assertions were changed for those observations. Raw logs and
TRX files are under `/tmp/p694b/verification/`. No commits or pushes were made.
