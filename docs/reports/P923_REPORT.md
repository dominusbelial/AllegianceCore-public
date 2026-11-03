# P923 — server routing through discovered alephs

Implemented against dispatch base `21747c7b`, with changes left in the working tree. No commits, pushes, stashes or remote-host operations were performed. The setting remains off by default.

**Native rule and an important correction to the brief.** Native `FindPath` checks `pship->CanSee(pwarp)` on both its initial exits and every later expansion (`Igc/common.cpp:920,987`), and returns NULL when its frontier is empty (`:960-968`). `CshipIGC::CanSee` accepts ownership, side visibility or the ship's own scanner (`Igc/shipIGC.h:380-389`). Alephs have no owner, leaving the last two alternatives. Personal scanning requires the same cluster and an unboarded ship, includes both radii and signature-scaled scanner range, multiplies rearward displacement by four, and checks asteroid occlusion (`Igc/shipIGC.h:341-376`). The existing native predicate is reused rather than approximated with distance alone.

The current checkout has **two** server travel planners. The P324 legacy caller uses the omniscient `Sector.FindPathToward` BFS described in the brief. Production-default native GotoPlan already gates warp visibility through `Ship.NativeGotoPlan.cs:FindNativeWarp`; this finding is also recorded in existing SIM-0476. Docker Compose separately defaults its native-GotoPlan environment override to zero. Neither steering default nor that deployment override was changed.

**Setting and behavior.** Set `"liveSettings": { "seenWarpRouting": true }` in the server JSON and restart. `LiveSettingsConfig.SeenWarpRouting` defaults to `false`, is logged at assembly, and binds `Mission.SeenWarpRoutingEnabled` for booted, rotated and provisioned worlds. The source-generated server configuration handles the new property. There is no new environment variable, service or port. The safe Docker example explicitly retains `false`; the guide, Docker README and glossary describe it.

| Planner | Flag off | Flag on |
|---|---|---|
| Legacy BFS | Existing full graph, ordering and cached hits/misses | Each edge must satisfy this ship's CanSee; discovery/view changes refresh the cache |
| Native GotoPlan | Existing visibility gate, frontier ordering, coward/brave policy and retry behavior | Same search policy; cached hits/misses refresh when the ship's visibility view changes |

The legacy helper retains its unweighted FIFO BFS, first-warp propagation and dangling-edge skip. Its shipless overload behavior remains full-graph traversal. The flag-off regression test pins the unseen shortcut and its cached result before and after discovery. No client or MCP pathfinder changed, and no cowardly filter or distance sorting was added to the legacy helper.

**Cache invalidation.** Each mission keeps an atomic, per-side discovery revision. A warp's false-to-true visibility transition advances only that side's revision; both `SetSideVisibility` and direct `RevealToSide` writes notify it. This includes the existing partner-reveal sweep, which implements native's paired-aleph reveal (`FedSrv/FedSrv.CPP:10782-10799`). Repeated revelations and losing the current eye do not advance the revision. Cross-sector simulation can publish discoveries concurrently, so revision increments use `ConcurrentDictionary.AddOrUpdate`.

Each ship compares mission identity, sector identity, side identity, that discovery revision, and its exact local exit eligibility set. Personal scanner geometry can change before the periodic side sweep, so comparing the local set is necessary to recover a cached miss immediately or discard a personally visible exit after leaving range. Work is bounded by local exits and their personal-scanner geometry; it does not search the graph every tick. No graph-wide visibility scan, per-tick allocation or per-tick BFS was added to cache hits. Hits and misses in both planners are reused until their navigation context changes. Target changes retain each planner's existing invalidation behavior.

Alephs **do not lose learned visibility** in normal play. They are predictable (`Igc/warpIGC.cpp:35`), and the native/ported visibility latch retains `SeenBySide` after scanners lose their eye (`FedSrv/FedSrv.CPP:11124-11126`). Partner revelation latches too. Mission replacement starts new revisions and invalidates through mission identity. Dynamic topology mutation is outside the static-graph cache contract.

The requested immediate improvement of a cached longer route is an opt-in cache policy extension, including for native GotoPlan. Native itself retains a selected warp until the ship/target cluster context changes, and retries a miss because it lacks a warp waypoint (`Igc/common.cpp:1844-1850,1884-1916`). The visibility predicate is native parity; discovery-triggered cached-hit replanning is the brief's explicit requirement, not a claim that native implements that trigger.

**No-route NPC behavior.** Native retains the target, clears the warp waypoint, optionally dodges, and returns false (`Igc/common.cpp:1915-1923`). The port's native wrapper waits with neutral controls when there are no obstacles; avoidance can still act. Legacy `CrossClusterWarpGoal` returns null, logs the stand-by once, and leaves the pending order in place. Its caller returns without a new steering goal and retains the previous controls; it does not add a brake or guarantee an immediate physical stop. Existing ripcord attempts remain ahead of warp routing. No unknown-edge escape, alternate destination or new retry timer was invented.

The shared legacy callers were checked: mining and selling, station travel, layer asteroid/build orders, cluster/point goto, retreat to a repair carrier/station, repair orders, and carrier resupply. Builders also reach this machinery through their assigned build travel. Native GotoPlan handles the corresponding native NPC travel. Tests exercise miners, builders, layers, wingmen and carriers with no route, preserving their pending orders and existing control behavior.

**Stranded-miner measurement.** `tools/p923-routing-proof` runs a local six-sector/twelve-directed-aleph SingleRing, seeds 923/924/925, 600 simulation seconds per arm, with 15 NPCs per side: one real initial miner plus fourteen wingmen. Settings use server GC, immutable snapshot/encode-once, parallel broadcast, pooled transport, native ship updates and native GotoPlan. Explicit legacy diagnostic arms pin only native GotoPlan off. Each arm samples miner orders once per simulation second. No remote machine was used.

| Scenario, three seeds per arm | Native off / on | Legacy off / on |
|---|---|---|
| Ordinary resources | 0/6 miners with a no-seen-route order in either arm | 0/6 in either arm |
| Home He3 exhausted; neutral rocks reported | 0/6 in either arm; cross-sector mining observed | 0/6 in either arm; cross-sector mining observed |
| Deliberately assigned isolated known rock | 6/6 encounter no seen route in either arm; zero ore sold | 6/6 encounter no seen route in either arm; enabled arm sells zero ore |

The isolated-target arm selects an actual rock behind an unscouted edge and reveals only the contact before assigning the mining plan. This is a controlled adverse state, **not its natural incidence rate**. With native travel, all 3,540 active miner samples per flag arm lacked a seen route, and the longest measured continuous wait was 589 seconds. Legacy flag-on also reached a 589-second wait in every seed; existing decision/territory changes can replace a plan, so its blocked sample counts are lower. Legacy flag-off can fly the undiscovered route and discover edges during flight: sampled initial no-route knowledge does not mean it was physically stranded. It sold 58.2, 58.2 and 0 ore across those three seeds, versus zero in all enabled arms. The ordinary sample establishes only the observed zero incidence in these small, ten-minute games; it does not establish impossibility. No replacement mining policy was added.

**Per-call cost.** Local Release JIT, .NET SDK 10.0.400, server GC; tiered compilation disabled for repeatability. Seven alternating arms of 20,000 measured calls each, with 2,000 warmup calls per arm. Same all-known six-sector/twelve-edge graph and three-hop query in both arms; returned warp asserted identical. All server settings remain the baseline above. This is local diagnostic evidence, not a rig acceptance measurement.

| `FindPathToward` | Median ns/call | Range ns/call | Allocated bytes/call |
|---|---:|---:|---:|
| Flag off | 522.6 | 410.4–787.6 | 696 |
| Flag on | 622.1 | 555.4–788.3 | 696 |

Median increment: 99.5 ns/call, about 19%. These are uncached searches, including the existing BFS allocations; cached navigation avoids that cost. Cache view checks and full-fleet throughput are separate from this microbenchmark. Rig 200v200 A/B and the default-setting decision remain with the dispatcher.

**Validation and evidence.**

- Solution build: zero warnings, zero errors.
- `tools/build-tools.sh`: all 114 tool projects built with zero warnings/errors; the final proof revision was also built and run directly.
- Linux x64 self-contained server AOT publish: passed, no IL2026/IL3050 or other warnings.
- New and affected tests: 63 passed, zero failed, zero skipped, three filtered repetitions. This includes 18 new graph/control cases and two artwork-backed loopback cases.
- Loopback: real server and logged-on TCP peer; a miner targets a side-seen rock in a controlled graph. Legacy steering is explicitly pinned because native steering already enforces discovery. Flag off selects and transits the direct unseen shortcut; flag on selects and transits the first leg of the known three-hop detour. The graph tests verify the entire route's admissibility; this live assertion concerns the first actual transit rather than later mining decisions.
- One Fast sweep: **12,998 passed, 13 failed, three optional Meshy-pack skips**, plus six calibrated timing offenders. Eleven failures pass 3/3 after clearing inherited baseline overrides that defeat their explicit old-path settings. The timer boundary passes/pass/fails in isolation; the wingman failure fails 3/3 both here and on unchanged dispatch-base sources. Four timing cases fall below the limit in all three isolations; the existing P777 timing case remains borderline above it. The Fast gate remains **failed**, with details in [the flake ledger](../TEST_FLAKES.md).
- Showcase never-parks: **unchanged dispatch base 5/5 passed, changed flag-off 5/5 passed** (observed failure rates both 0/5). The separate enabled run also passed, with longest sampled hold 0.802 s. P700 assertions were unchanged; every run skipped zero tests. These local samples do not replace the dispatcher's rig A/B.
- `docker compose config`: passed. `git diff --check`: passed. Glossary link check: 529 paths passed.

The workspace's `local.env` was present and loaded. New artwork tests skipped **zero** cases. The Fast sweep skips exactly three optional private Meshy-pack tests; artwork-backed cases ran. All unrelated failure/timing isolation receipts are included below.

SIM-0476 was updated with the legacy gate, cache design, opt-in policy distinction and executable evidence. No additional production parity credit is claimed. Before/after scorecards retain the sim headline **98.51% (2112/2144)**, **790/805** completed eligible rows; general matrix remains **100% (1881.500/1881.500)**. All matrices validate.

Repeat with the configured artwork environment and rig-baseline overrides: `dotnet run --project tools/p923-routing-proof -c Release` (add `-- --cost-only` for just the microbenchmark), and `python3 tools/p923-local-proof.py --base --repeat 5`, `--repeat 5`, or `--seen`. The showcase helper snapshots tracked HEAD sources for the base arm and compiles the unchanged P700 assertions in an isolated test project. Small public receipts are in [P923_assets/verification.json](P923_assets/verification.json); raw builds, TRX and proof logs stay under [the task scratch directory](../../.scratch/p923/), excluded from version control.

**Remaining gaps.** The rig default decision, 200v200 performance acceptance, cowardly legacy filtering, distance-ordered legacy exits, client/MCP routing and the existing debug-topology home-visibility issue remain out of scope. There is no new hot-reload mechanism; this live setting follows the existing assembly-time pattern. The ordinary and exhausted-home experiments are bounded samples, and the isolated-target experiment demonstrates consequences rather than natural frequency.

**Native-intent footer.** Knowledge-limited routing is a fog-of-war **design** rule. Omniscient legacy BFS and an indefinitely cached discovery miss are port **bugs** relative to that rule. Native's linked-list search and repeated miss attempts are its implementation **era constraints**, rather than gameplay requirements. A modern implementation uses versioned side knowledge and explicit personal-scanner eligibility to retain cache reuse while reacting to discovery; a future shared per-side route cache could reduce repeated work further, provided it keeps each ship's scanner exceptions and the chosen route-order policy.
