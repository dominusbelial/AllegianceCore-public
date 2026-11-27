# P1094 late-match update spikes

Base `28debb3c`. Elapsed times are approximate and overlap where one run supplies several proofs.

## Brief items

| Item | Status | Elapsed | Evidence |
| --- | --- | --- | --- |
| 1. Log attribution | not done | ~3 min | b118/VPS attribution complete. The three b117 matches omit all requested bucket rows |
| 2. Scaling work | done | ~4 min | Instrumented 400-peer contact-count sweep below |
| 3. Local reproduction | done | ~3 min | Release 1/64-contact phases, exact contact and population guards |
| 4. Equivalent optimization | done | ~5 min | Differential mission-state and wire replay passed. Native effects and validation inputs below |
| 5. Regression proofs | not done | ~10 min | New proofs, deliberate breaks, wire goldens and budgets pass. The pre-existing supplemental P928 golden fails on both revisions |
| 6. Workstation measurements | done | ~3 min across the work interval | Three paired Release runs with the stock profiler and 16 transport workers. Additional timing probes in a separate process |
| 7. Report | done | ~32 min elapsed alongside the gates | Attribution, measured change, exactness proofs, limits and flake receipts below |
| Light gate | not done | ~21 min including isolation | Both solution builds and all tool builds passed. Fast returned four ledgered test failures |

## What the rig actually measured

Match age alone does not explain the rise. In match 1 the update mean peaks early and finishes below 4 ms. In match 2 the research finale arms at 05:14:40 UTC (age 30m45s). Between minutes 25–30 and 40–45 the update rises 6.7 → 14.6 ms and collision processing rises 0.8 → 10.9 ms. Within collision processing the ship event callback rises from about 0.1 on active windows to 5.5 ms, and ship/station detection from 0.3 to 2.5 ms. Model updates fall 2.5 → 1.6 ms. The cost shifts from sector 3 (4.46 → 0.69 ms) to sector 0 (0.86 → 8.57 ms), with a smaller rise in sector 2 (0.56 → 3.17 ms). Mining rises only 0.30 → 0.38 ms, while the combat and economy buckets both fall. The capture ends shortly after the third match starts. The first window after rotation can contain ticks from both matches.

The b118 match starts (UTC) were 03:43:55, 04:43:55 and 05:29:40. The b117 starts were 01:53:55, 02:53:56 and 03:40:21. Each cell below gives the mean of reported five-second window means, the nearest-rank 95th percentile of the reported window p95s, and the largest reported maximum, in milliseconds. Individual tick samples are unavailable, so a pooled tick p95 cannot be recovered. Age is measured from the matchup timestamp to the window end. Conditional buckets average over windows in which they were reported, and the profiler averages those buckets over due ticks. Parent and child times overlap. The accompanying CSV contains every observed simulation, sector, collision, visibility and combat bucket, its window count and these three statistics. The b117 capture has zero stage or child-bucket rows across all 1,287 windows. It permits a whole-tick comparison only. [All five-minute bucket statistics](LATE_MATCH_UPDATE_SPIKES_P1094.csv)

| Match | Minutes from | b118 update | b118 collision | b118 ship event | b118 models | b118 wall | b117 wall |
|---:|---:|---|---|---|---|---|---|
| 1 | 0 | 4.3/8.9/21.9 | 0.5/0.9/5.9 | 0.1/0.3/4.0 | 1.9/4.1/11.0 | 14.3/25.5/60.3 | 18.1/34.8/42.5 |
| 1 | 5 | 7.4/13.8/24.3 | 1.0/1.6/7.2 | 0.1/0.3/0.4 | 2.9/6.0/12.7 | 21.1/34.2/48.0 | 29.1/45.7/63.4 |
| 1 | 10 | 6.7/14.2/32.8 | 1.0/1.9/8.7 | 0.1/0.3/0.4 | 2.6/5.5/16.0 | 22.4/36.2/55.5 | 36.9/55.0/68.2 |
| 1 | 15 | 4.1/9.4/17.8 | 0.4/0.5/5.8 | 0.1/0.3/0.3 | 1.9/5.0/10.2 | 15.4/26.7/41.6 | 17.7/29.7/38.8 |
| 1 | 20 | 4.1/7.6/13.2 | 0.4/0.5/5.6 | 0.1/0.1/0.2 | 1.9/3.8/8.5 | 15.3/23.2/30.6 | 15.3/22.6/34.0 |
| 1 | 25 | 4.0/8.2/16.6 | 0.4/0.6/6.0 | 0.1/0.2/0.2 | 1.7/3.9/11.6 | 14.0/22.7/30.2 | 17.0/27.5/41.4 |
| 1 | 30 | 4.0/7.1/18.6 | 0.4/0.7/9.6 | 0.1/0.2/0.3 | 1.8/3.4/11.6 | 14.3/24.2/30.5 | 15.6/23.7/31.2 |
| 1 | 35 | 3.5/6.6/17.0 | 0.5/0.7/9.0 | 0.1/0.3/0.3 | 1.5/2.8/10.8 | 14.3/23.6/32.5 | 14.7/23.1/32.4 |
| 1 | 40 | 4.5/8.4/15.9 | 0.5/0.7/9.6 | 0.1/0.2/0.2 | 2.0/4.4/12.3 | 14.9/26.8/33.5 | 15.1/24.4/41.4 |
| 1 | 45 | 3.9/7.4/18.3 | 0.5/0.7/9.6 | 0.1/0.3/0.4 | 1.7/3.7/15.7 | 14.5/24.0/32.5 | 15.4/22.9/35.6 |
| 1 | 50 | 3.9/7.9/16.2 | 0.5/0.7/8.8 | 0.1/0.3/0.3 | 1.6/3.3/12.7 | 14.4/25.1/36.1 | 14.7/22.1/34.0 |
| 1 | 55 | 3.8/8.1/17.3 | 0.5/0.7/8.8 | 0.1/0.2/0.4 | 1.7/3.5/11.9 | 14.6/23.7/31.9 | 15.3/24.9/36.6 |
| 2 | 0 | 6.5/17.9/30.1 | 0.6/1.0/19.2 | 0.2/0.6/2.3 | 3.4/12.1/23.6 | 22.0/46.8/133.7 | 31.5/62.9/97.4 |
| 2 | 5 | 8.9/18.9/32.9 | 1.0/1.9/13.0 | 0.1/0.3/0.9 | 3.6/9.3/20.8 | 26.1/44.0/57.5 | 26.4/39.0/55.9 |
| 2 | 10 | 8.0/12.4/24.1 | 1.2/1.8/12.6 | 0.1/0.3/0.4 | 2.8/4.9/13.8 | 21.4/32.4/44.1 | 25.7/37.9/61.2 |
| 2 | 15 | 8.5/14.5/26.2 | 0.9/1.4/10.9 | 0.1/0.2/8.8 | 3.2/5.6/16.2 | 21.7/33.5/46.0 | 22.4/32.9/52.5 |
| 2 | 20 | 7.7/13.4/26.3 | 0.8/1.2/10.3 | 0.1/0.2/0.2 | 3.0/5.4/19.1 | 19.4/31.2/43.5 | 22.9/35.7/53.7 |
| 2 | 25 | 6.7/12.8/22.6 | 0.8/1.1/9.3 | 0.1/0.2/0.3 | 2.5/4.5/14.2 | 16.9/27.5/41.0 | 19.4/29.5/37.4 |
| 2 | 30 | 12.0/29.2/45.7 | 7.9/22.4/31.7 | 3.9/13.8/24.3 | 1.7/3.0/15.8 | 23.4/49.6/124.9 | 25.6/120.4/145.1 |
| 2 | 35 | 13.0/27.2/39.0 | 9.5/21.2/29.3 | 4.7/12.4/22.3 | 1.5/2.7/23.5 | 25.3/64.0/138.8 | 32.3/107.7/140.4 |
| 2 | 40 | 14.6/27.5/37.4 | 10.9/20.3/30.7 | 5.5/13.1/21.0 | 1.6/2.6/9.0 | 27.0/43.0/130.4 | 32.6/121.4/145.5 |
| 2 | 45 | 14.9/25.6/33.0 | 10.5/18.2/21.8 | 5.4/10.4/15.7 | 1.8/4.3/8.9 | 27.2/41.4/50.5 | 31.4/77.0/136.0 |
| 3 | 0 | 0.6/21.2/31.9 | 12.7/18.5/18.5 | 6.7/12.6/12.6 | 1.9/3.0/3.0 | 5.5/40.0/131.0 | 5.8/28.2/115.1 |

In b118's 51-tick window ending 05:21:25, wall mean/p95/max were 42.16/129.53/138.81 ms. Update was 13.68/27.17/32.81, collision 10.06/15.52/29.29 and sector 0 8.95/14.07/27.60. Snapshot publication reached 108.39 ms (p95 107.28). Even the maximum update leaves at least 106 ms of the worst tick outside update. The 05:19:15 and 05:26:20 windows also show >100 ms publication maxima and update maxima below 40 ms. The time-dilation receipt reaches 0.90081 at 05:21:24.459. Gen0/1/2 counts in the 51-tick window were 15/7/1, total GC pause 112 ms across the window and worst reported pause 10.87 ms. At age 40–45 both builds reach their late spike with zero gen2 collections across that bin. The median reported allocation per window rose from 418.4 MB in b118 match 2 at age 25–30 to 814.7 MB at 40–45. Only medians are used for the rig allocation estimates because some windows have negative or implausibly large values.

The single slowest wall tick by match, with GC from the same five-second window, is shown below. Both match 3 maxima fall in the first window after rotation.

| Build | Match | Window end UTC | Ticks | Wall max ms | Collections 0/1/2 | Total pause ms | Worst reported pause ms |
|---|---:|---|---:|---:|---|---:|---:|
| b117 | 1 | 02:08:05 | 86 | 68.16 | 7/5/0 | 62.5 | 14.72 |
| b117 | 2 | 03:34:10 | 85 | 145.51 | 15/6/0 | 64.2 | 5.66 |
| b117 | 3 | 03:40:25 | 99 | 115.08 | 11/5/0 | 48.1 | 5.77 |
| b118 | 1 | 03:46:45 | 100 | 60.31 | 12/5/0 | 70.3 | 7.50 |
| b118 | 2 | 05:21:25 | 51 | 138.81 | 15/7/1 | 112.0 | 10.87 |
| b118 | 3 | 05:29:45 | 99 | 131.00 | 6/3/0 | 42.4 | 14.41 |

The b117 match 2 trough at 03:27:45 had 54 ticks, a 135.61 ms maximum, 16/6/0 collections and a 5.38 ms worst pause. Both b117 extrema come after its research finale arms.

The b118 rotation logs report 411 peers at each completed match end. The match 1 finale credits 10/0/4 station kills and 1,370/320/952 kills by side. Match 2 finishes on kills (678/709/960) with one station kill per side. The b117 research finale also arms at age 30m45s and finishes with 724/684/870 kills and 1/1/0 station kills. There are no per-window collision counts, peer locations, model censuses, construction records or individual kill records in the supplied rig excerpts. These end totals cannot be assigned to a particular slow tick.

## VPS at 18:00 UTC

The window ending 18:00:04 was 13m35s into the match. Of 85 ticks, the worst took 494.53 ms. Update peaked at 477.04, sector 2 at 476.39 and models at 466.98 ms, while model p95 was just 1.33 ms. The same window had 52.93 ms drain, 33.18 ms collision and 28.63 ms ship broadcast maxima. GC paused for 12.3 ms total with no gen2. The preceding three wall maxima were 8.21, 7.48 and 30.22 ms. In the following window only 76 ticks completed, multiple sectors reached 47–87 ms, and the thread-pool probe reached 110.66 ms. This favors a period of general scheduling or CPU contention on the one-vCPU host. The measurements time elapsed wall, include the fleet clash and lack per-model CPU time and hypervisor steal time. They cannot distinguish a VM pause from a model doing real work for that one tick.

Only one of the six b118 match 3 windows contains collision samples. It straddles rotation. No late phase of match 3 is present in either recording.

The server also logged four mining events, one rescue and one undock around that five-second window. There is a 593 ms gap in log timestamps ending at the 494.55 ms overrun receipt at 18:00:03.394. Seven ping lines immediately follow within 54 microseconds of that receipt, consistent with buffered work being drained. The next window’s worker probe and broad bucket slowdown support contention, although output buffering limits what the timestamps alone can establish.

## Contact count, audience construction and equivalence

The measured update encloses real mission updates in `Server.Broadcast.cs:148–159`. `src/Allegiance.Core/Model/Sector.cs:3246–3259,3305,3343–3346` counts each resolved contact and synchronously signals the site before applying damage. `ServerHost.cs:598–600` and `Server.Lifecycle.cs:915` route the call into the server. The two effect payloads implement native `Igc/shipIGC.cpp:1189–1195`. All server paths in this section are under `src/Allegiance.Server/`. The source inspection and rig recordings identify a collision callback, not the precise split of its time on the rig.

On base, `Server.Combat.cs:247–258,305–317` built a new union for each contact. For **each** party, `Server.Broadcast.cs:2589–2615` scanned the peer map, built per-enemy-side connection lists and sector sets, and yielded all members of each qualifying side. For C two-ship contacts and P seated peers, that is up to 2C group builds, 4CP peer visits across the two audience passes and O(CP) cumulative temporary storage per tick. Sending to R actual recipients per collision still requires O(CR) deliveries. For 64 contacts and 400 seated peers the base visits up to 102,400 peer records in those passes and queues 25,600 ordered reliable deliveries. This scales with contacts, seats, exports and sector occupancy. No accumulation of dead models is necessary. There is no rig counter from which to infer the 64-contact count.

The secondary detector at `src/Allegiance.Core/Model/Sector.cs:3483–3507` registers flying ships and positioned stations. Base `src/Allegiance.Core/Collision/BroadPhase.cs:47–131` sorts intervals and visits active candidates, or streams all unordered pairs when dense. For S ships and B stations, sorting is O((S+B) log(S+B)), and dense pair visits can reach (S+B)(S+B−1)/2 before filtering to ship/station pairs. The rig recorded elapsed detection time but neither S/B nor candidate counts, so it cannot identify which operation inside the detector contributed the additional 2.2 ms. The independent model, mining, docking, combat and other conditional totals are in the CSV. There is no subclass timer for native NPC steering in the historical log.

A separate Release process on base split the callback with two temporary scopes under the existing profiler. At one contact/tick, audience selection and queueing averaged 0.026 and 0.009 ms. At 64 they averaged 2.151 and 0.634 ms (whole callback 2.970 ms). The fixture counted exactly 100 and 6,400 resolved contacts in 100 ticks. All six final timing processes use only the stock profiler buckets.

The changed `Server.CollisionRoster.cs:20–69` constructs groups in the same peer iteration order, including first-encounter side order within each mission and connection order within each group. Before reuse, it compares the ordered connection, mission reference, current ship side and seated sector for every peer. Count, removal/reinsertion, sector moves and in-place side changes are covered. A different ship with precisely the same grouping inputs can reuse the groups because the owner and teammate scans still read the live ship and current export set. Each party reads its current sector, cloak and seen-by-side bits during the callback. Sector presence admits the entire enemy side in that mission, including members elsewhere.

`Server.Combat.cs:247–273,320–332` keeps the union's original insertion order and sends the original packed bytes with the original reliable send loop. The second party can add nothing once every seated connection belongs to the union. The code loans the cleared set to one call at a time and returns it in `finally`. A nested send can use a separate set. `Server.BroadcastSnapshot.cs:171` releases the roster when projection ends. There is no saved roster outside an open snapshot. With a stable roster the grouping and set backing storage are O(P) per snapshot. Each actual grouping change may require one more rebuild. Validation and emission remain linear per contact. Collision detection, RNG, state transitions and packet encoding follow the existing code.

## Release reproduction

Intel Core Ultra 9 285HX, 24 logical CPUs, .NET SDK 10.0.403. `P1094CollisionFleetTests` uses the production server host because P835Bench's null site cannot exercise collision fan-out. The fixture boots a generated SingleRing with seed 1094, PCore001, 400 bare living fighters, two sides and established teammate exports. It stages disjoint approaching pairs far from terrain before each 50 ms simulation step. All runs checked 400 survivors, the exact contact count and 64 reliable frames per recipient on a final untimed tick. Immutable snapshot, encode-once, the built-in P863 budget, native ship updates, native GotoPlan and pooled transport use the baseline configuration. Parallel build is enabled with a maximum of 15 workers and the transport pool has 16 workers, matching the rig header. The publisher consumes the real snapshot and is flushed after each update. Peers are seeded in memory, so the fixture does not measure network saturation, publication backlog or time dilation.

Each phase warms 20 ticks then measures one unpaced 100-tick window. The update and child timers exclude pair placement, sealing and flushing. Process-wide allocation and collection deltas include that upkeep. `KiB/tick` uses the precise total allocation counter. The one-contact control shows whether the improvement relies on high contact density.

```bash
export NUGET_PACKAGES="$PWD/.scratch/packages"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/http"
export XDG_CONFIG_HOME="$PWD/.scratch/config"
set -a
source local.env
set +a
dotnet build tests/Allegiance.Core.Tests -c Release
for run in 1 2 3; do
    DOTNET_gcServer=1 DOTNET_TieredCompilation=0 dotnet test tests/Allegiance.Core.Tests \
        -c Release --no-build --filter FullyQualifiedName~Crowded_fleet_update_profile \
        --logger 'console;verbosity=detailed'
done
```

For base, use the same fixture with production files from `28debb3c`. For changed, use the working tree. Timings below give mean / p95 / max in ms. Each row has 100 measured ticks. The profiler test itself is in the Perf tier. Each test process took 2.57–2.91 seconds, including server boot, both warm-ups and the untimed guard. Runs were serial and paired in base/change, change/base, base/change order after the other gates finished.

| Contacts/tick | Revision | Run | Update | Collision | Ship event | KiB/tick | GC 0/1/2 |
|---:|---|---:|---|---|---|---:|---|
| 1 | base | 1 | 1.000/2.593/4.158 | 0.330/0.370/0.662 | 0.051/0.063/0.372 | 151.61 | 0/0/0 |
| 1 | base | 2 | 0.993/2.564/4.058 | 0.336/0.366/0.486 | 0.049/0.060/0.084 | 152.94 | 0/0/0 |
| 1 | base | 3 | 0.982/2.586/4.035 | 0.322/0.358/0.369 | 0.045/0.059/0.065 | 151.61 | 0/0/0 |
| 1 | change | 1 | 0.980/2.582/4.228 | 0.331/0.359/0.390 | 0.044/0.054/0.066 | 160.59 | 0/0/0 |
| 1 | change | 2 | 1.098/2.591/11.389 | 0.331/0.371/0.455 | 0.047/0.067/0.115 | 160.82 | 2/1/1 |
| 1 | change | 3 | 1.039/2.645/4.119 | 0.351/0.373/0.381 | 0.058/0.070/0.074 | 160.55 | 0/0/0 |
| 64 | base | 1 | 3.764/4.613/20.479 | 3.098/3.557/19.855 | 2.588/3.030/19.180 | 1423.93 | 2/1/1 |
| 64 | base | 2 | 4.624/5.800/22.318 | 3.906/4.102/21.738 | 3.331/3.532/21.059 | 1424.32 | 3/2/1 |
| 64 | base | 3 | 4.195/5.326/19.379 | 3.501/3.490/18.784 | 2.949/2.960/18.153 | 1424.33 | 2/1/1 |
| 64 | change | 1 | 2.523/3.574/18.723 | 1.709/1.788/2.396 | 1.207/1.260/1.886 | 291.89 | 2/1/1 |
| 64 | change | 2 | 2.357/3.635/4.335 | 1.700/1.761/2.117 | 1.197/1.240/1.616 | 290.55 | 0/0/0 |
| 64 | change | 3 | 2.605/3.705/17.090 | 1.925/1.808/16.538 | 1.360/1.274/15.954 | 291.86 | 2/1/1 |

At 64 contacts the across-run median update mean falls from 4.195 to 2.523 ms (40%), and the median ship-event mean from 2.949 to 1.207 ms (59%). The median update p95 falls from 5.326 to 3.635 ms. Allocation falls from 1,424.32 to 291.86 KiB/tick (80%). At one contact the update means are 0.993 and 1.039 ms, and the changed roster costs 9.0 additional KiB/tick on the median run. The local results do not establish that the 139 ms rig tail will disappear.

On the next rig A/B, align match 2 windows by research finale and compare the 30–45 minute `collision.ship.event`, `sim.collision`, `step:update`, ticks per five seconds and whole-tick tails. Count resolved contacts, seated peers and reliable recipients to distinguish cheaper work from fewer fights. Inspect `collision.station.detect` and `broadcast.snapshotPublish` separately. This change targets neither the station detector nor the publisher backlog. The VPS scheduling question needs per-thread CPU or host scheduling evidence from a future run.

## Regression and deliberate-break proofs

The two Fast proofs in `tests/Allegiance.Core.Tests/P1094CollisionRosterTests.cs` and the Perf fixture each passed three separate exact-method Release runs with artwork. The differential runs two seeded 32-ship worlds through eight full mission updates with 12 real collisions per tick. A test-only oracle reconstructs the original owner/teammate and grouped cross-side union and sends the original encoded event through the existing publisher. During the same tick the fixture reorders seats, relocates the only local enemy seat, changes a ship's side and mission, toggles cloak and current visibility, and raises a single-party event. The ordinary projection suffix runs too.

After each tick it compares every published packet byte, recipient, ordering key and guaranteed bit in global delivery order. It also compares a SHA-256 digest of the complete logical mission graph, including private fields, raw floating-point values, ordered collection contents, shared references and the seeded RNG's private state. Host callbacks/services and the two immutable input assets are opaque. The boot wall timestamp and freshly generated research epoch are excluded from both runs. These are independent per-tick checks. The growth test observes the same group object across 128 further contacts, a replacement on in-place side change, and no cached roster after sealing, in three successive snapshots.

Each deliberate change was compiled and run against a separate copy of the production server. All three failed the intended assertion.

| Proof | Deliberate change | Observed failure |
|---|---|---|
| Repeated contacts | Start every contact with an empty roster cache | The second contact returned a different grouping object. |
| Per-tick state and packets | Accept the old grouping after a seated-sector change | State still matched. The ordered wire transcript diverged at byte 19,747. |
| 400-seat benchmark | Skip the first recipient in the send loop | One recipient received 0 of its 64 expected reliable collision frames on the untimed validation tick. |

Both the oracle comparison and the benchmark passed against a separately compiled copy of the unmodified base server, including the benchmark’s per-peer delivery guard. No deliberate change is in the production working tree.

## Gate

Debug and Release solution builds each finished with zero warnings and zero errors. Both used local packages under the worktree’s scratch directory, an empty local restore feed, a scratch HTTP cache and scratch XDG configuration. An initial concurrent MSBuild invocation lost a worker node. The completed builds used `-m:4 -nr:false`. The full tools build then passed for all 157 tool projects. Fast uses `--no-build` against the Debug output. The glossary link check passed for all 1,188 paths.

| Unchanged focused coverage | Cases per run | Release results |
|---|---:|---|
| FedPacket wire goldens and collision codec | 4 + 2 | Each class passed 3/3 |
| Ship/ship, ship/station and ship/asteroid collision | 8 + 5 + 6 | Each class passed 3/3 |
| P863 clash and P863b surge budgets | 12 + 6 | Each class passed 3/3 |
| P928 ordinary and supplemental 500-tick goldens | 1 + 1 | Ordinary passed 3/3. Supplemental failed 3/3 on base and change |

The supplemental golden's first mismatch is the event digest at tick 33. Both revisions produced the empty event digest and 3,091 RNG draws, with matching state and continuous hashes. This reproduces the ledgered discrepancy without changing the golden or the station damage rule. [Earlier base comparison](RESCUE_RANK_SPEEDUPS_P1087.md)

### Fast sweep and isolation

`tools/run-tests.sh --tier fast --no-build --parallel 1` completed all seven projects in 1,063.1 seconds. Nine infrastructure checks passed. Of 16,201 discovered Fast cases, 16,192 passed, five skipped and four failed. The calibrated limit was 10.0 seconds. Only the failed station-destruction case exceeded it. Both new Fast proofs passed within the sweep. Every flagged method ran three times in fresh Debug test hosts afterward.

| Method | Sweep observation | Method-only results |
|---|---|---|
| `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim` | Skirmish/port sustained aim drift | Pass / pass / pass, all four theory rows each time |
| `P1051ResumeAuthenticationTests.Valid_WS_resume_retires_both_prelogon_deadlines_and_restores_human_classification` | Expected ship 8096, observed null | Pass / pass / pass |
| `StationCombatLiveTests.Lethal_hit_destroys_station_ejects_docked_ship_and_broadcasts_destroy_to_whole_mission` | The cross-side shot did not destroy the station, 22.569 seconds | Pass / pass / pass |
| `LivePositionRequestTests.Empty_side_request_auto_accepts` | Missing new-side leader receipt at line 248 | Fail / fail / pass. The two retries reached line 251 with one `SideGone` deletion for side −1 |

All four methods and sweep signatures already appear in the flake ledger. The two isolated side-selection failures did not invoke the collision broadcaster (zero collision lines in transcripts with boot and side-selection logging). A Debug server rebuilt from `28debb3c` passed the first 13 isolated attempts and produced the identical line-251 `SideGone` receipt on attempt 14. The additional signature is recorded in the ledger. [Flake ledger](../TEST_FLAKES.md)
