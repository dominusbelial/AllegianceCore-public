# P699 — lifepods dock at home instead of dying outside it

Working-tree implementation based on the supplied master snapshot `b43ebbfbda03b5dc8d6e9180b793c370cbde38e7`. No commits or pushes. Rig/221/200v200 verification belongs to the other lane and was not run here.

## Reproduction and cause

A real loopback server, real MCP `BotManager` combat bot, PCore001 Iron Coalition lifepod **444**, and the unmodified XXV_simple map reproduce the report. The pod starts near the outbound mouth in Arrokoth **1051**, crosses Borasisi **1052**, and reaches its friendly Garrison in Ceto **1053**. With the original cap-only geometry loader, oxygen expires at **300.30**, exactly its original deadline. The wire contains a true-death `KILL_SHIP` followed by the docked restart; the new regression rejects this as a successful home flight. See [baseline wire/server trace](P699_assets/bot-before.trx).

Two related defects explain the arrival failure:

1. Station reconstruction stopped after `capgarage` frames. Garrison models such as **ss24a** and **ss27** contain ordinary `garage1A…` / `garage2A…` frames. Native loads those too. Consequently the bot, human, NPC and server caches incorrectly treated real garrisons as lacking geometry and fell back to center-based behavior.
2. MCP's fallback called `CombatFlyTo`, whose general dodge feed includes the destination station and whose extra ship-avoidance pass includes launch traffic. The bot therefore evaded the very station it needed to enter. Its dock telemetry also counted a true-death restart as “pod reached home.”

Pod 444's actual capability mask is **0xA4**, including **0x80 fighter** and **0x04 lifepod**. Several old comments incorrectly described every pod as a non-fighter requiring capital landing. The capability calculation was already correct; the comments and missing ordinary bay data were not.

## Native rules checked

All paths in this section are beneath `~/Development/Allegiance/src/`; line numbers were checked against the local native source.

| Rule | Native source |
| --- | --- |
| `c_habmLifepod=0x04`, `c_habmFighter=0x80`; `c_sabmLand=0x20`, `c_sabmCapLand=0x1000` | `Igc/igc.h:728`, `Igc/igc.h:733`, `Igc/igc.h:749`, `Igc/igc.h:756` |
| Scale station frames by station radius / original model radius. Load the capital slots first, save their count, then append ordinary garage slots using the combined slot number, capped at four. | `Igc/stationtypeigc.cpp:46`, `Igc/stationtypeigc.cpp:71`, `Igc/stationtypeigc.cpp:106`, `Igc/stationtypeigc.cpp:112`, `Igc/igc.h:30` |
| Fighter hulls use all station slots; non-fighters use only the capital prefix. Docking requires the hull's minimum extreme to pass every plane of a slot in station orientation. Merely reaching the station center or a fixed radius is insufficient. | `Igc/stationIGC.cpp:389`, `Igc/stationIGC.cpp:405`, `Igc/stationIGC.cpp:417`, `Igc/stationIGC.cpp:423` |
| Friendly station collision requires the hull-class landing capability and `InGarage`; the resulting `DockWithStationEvent` requires warp readiness, announces `SDR_DOCKED`, and publishes docked status. | `Igc/shipIGC.cpp:727`, `Igc/shipIGC.cpp:734`, `Igc/shipIGC.cpp:843`, `FedSrv/FedSrv.CPP:13087`, `FedSrv/FedSrv.CPP:13093`, `FedSrv/FedSrv.CPP:13107` |
| `InGarage` is reached from a collision event. Native hit testing first bounds candidates by the sum of collision radii; an open entry half-space alone cannot dock a distant ship. | `_Utility/HitTest.cpp:130`, `Igc/clusterIGC.cpp:298`, `Igc/clusterIGC.cpp:315` |
| A lifepod can instead be rescued at a friendly rescue-capable station when the landing-capability branch is unavailable. | `Igc/shipIGC.cpp:861` |
| Native distinguishes **enter** (dock/warp) from **goto** (approach an offset and stop). A friendly station GOTO/NONE order becomes enter for an eligible pod; a fighter pod accepts Land or Rescue capability. | `Igc/igc.h:5204`, `Igc/shipIGC.cpp:4024`, `Igc/shipIGC.cpp:4036` |
| The station autopilot selects the appropriate bay count, transforms plane-0 frames, stages on the approach line, pivots, then enters toward the bay center. With no bays it power-glides through the station and skips that target as an obstacle. | `Igc/common.cpp:1157`, `Igc/common.cpp:1287`, `Igc/common.cpp:1301`, `Igc/common.cpp:1505`, `Igc/common.cpp:1513`, `Igc/common.cpp:1517`, `Igc/common.cpp:1528`, `Igc/common.cpp:1540` |
| Oxygen is armed on a hull swap to a pod using the core's endurance constant; the native default is **300 seconds**. Expiry calls `KillShipEvent` with a null launcher and amount 1. A pod death is a true death, marked by `bDeathCredit`. | `Igc/shipIGC.cpp:1407`, `Igc/missionigc.cpp:2724`, `Igc/missionigc.cpp:2775`, `Igc/shipIGC.cpp:361`, `FedSrv/FedSrv.CPP:11776`, `FedSrv/FedSrv.CPP:11779`, `FedSrv/FedSrv.CPP:11798` |

The port retains its documented spherical hull-extreme approximation, absent-art radius fallback, and existing warp/launch cooldown gates. This change does not increase oxygen, loosen the P653 purchase gates, or bypass docking with a respawn/rescue call.

## Changes

- Reconstruct both capital and ordinary station bays, retain the capital-prefix count, and apply that count consistently in server `InGarage`, MCP, human and NPC approach selection. The server can now recognize a real garrison's actual landing bays. Cache by the actual type/model/radius inputs so runtime overrides cannot inherit another model's geometry.
- Require station collision range before testing real bay half-spaces, preventing a distant aligned ship from docking through an unbounded entry half-space. A new sector-level negative/positive regression covers this prerequisite.
- Keep the existing shared bay staging/entry maneuver. Fix MCP's absent-bay fallback to full-throttle pursuit through its destination, without general combat station avoidance or arrival braking, matching native's power glide.
- Count true-death docked restarts separately from pods that reached home. The safe restart still uses the existing purchase/relaunch lifecycle, but logs `RESTARTED` and contributes to `PodsRestarted`, rather than `PodsDocked`.

Normal arrival is collision-driven: the flight enters a valid bay and `DockWithStationEvent` accepts it (`Igc/shipIGC.cpp:843`, `FedSrv/FedSrv.CPP:13087`). Native station GOTO/NONE selects the **enter** objective rather than an offset-and-stop goto when docking is allowed (`Igc/shipIGC.cpp:4024`, `Igc/shipIGC.cpp:4036`; `Igc/igc.h:5206`). The existing controls / native pose stream supplies that maneuver here. A center-point goto plus general obstacle avoidance does not supply the required bay entry.

## Human classification and local flight evidence

The ported human client's original fallback **does dock** on the same two-hop route: its destination is already excluded from dodge obstacles. The paired headless human test invokes the actual `Game1` eject target chooser and autopilot control method, flies `OwnShipSimulator`, and sends native `SHIP_UPDATE` poses over a real session, rather than MCP controls. Before the geometry correction it docked after **233.35 simulation seconds**, with zero oxygen deaths. See [human baseline](P699_assets/human-before.trx).

After correction, the human session also uses the real ordinary bays and docked after **233.40 seconds** in the initial check. The MCP initial fixed flight docked after **234.20 seconds**. Both traverse **1051 → 1052 → 1053**, use the unchanged **300-second** timer, receive a normal dock, and assert **zero true-death events**. See [human corrected trace](P699_assets/human-after.trx) and [MCP corrected trace](P699_assets/bot-after-initial.trx).

The final MCP regression additionally launches twelve physical ships through the production undock helper when the pod enters its home sector, covering local relaunch traffic. The final repeated flights are preserved with hashes and extracted logs in [flight-evidence.json](P699_assets/flight-evidence.json):

| Path | Three isolated runs | Oxygen deaths | Pose corrections |
| --- | --- | --- | --- |
| MCP, twelve relaunching ships | 250.00 / 247.80 / 246.00 simulation seconds | 0 / 0 / 0 | Controls-only MCP path |
| Accelerated human session | 240.30 / 240.70 / 240.30 simulation seconds | 0 / 0 / 0 | 2 / 2 / 1, applied by the client |
| Real-time human, ping clock | 4895 / 4899 / 4899 ticks, about 245 simulation seconds | 0 / 0 / 0 | **0 / 0 / 0** rejects and resets |

Friendly station collision already excludes friendly docking candidates; no evidence required a new hull bounce or jiggle rule. Longer routes can still legitimately exceed native oxygen, but this reproduction has enough travel allowance; increasing it would conceal the arrival defect.

## Fixture compatibility

Recognizing real ordinary bays intentionally removes the old center-point docking shortcut. Tests of docking transitions, restocking, ore sale, visibility and teleport now place incoming ships in actual bay interiors via `DockTestPlacement`; their behavioral assertions remain intact. Hangar-only economy setups explicitly start docked where appropriate. Tiny synthetic capture/conquest bases explicitly use absent artwork rather than accidentally inheriting a scaled real garrison model. The dodge-goal isolation fixture explicitly selects absent bay data, retaining its original straight-approach assertion; real bay staging legitimately deviates from a line to the station center.

The constructor fixture now issues an undock only if still docked: native's station-type completion starts and immediately undocks a constructor (`FedSrv/FedSrv.CPP:11354`, `FedSrv/FedSrv.CPP:11356`), and a center spawn is no longer incidentally docked by a radius shortcut. No production constructor, transport, receipt deadline or quarantine trait changed. The hostile-dock precondition now proves actual bay membership before asserting side refusal.

## Verification

The initial unfiltered diagnostic suites reported Core **4522 pass / 41 fail**, ClientCore **1008 pass / 11 fail**, and MonoGame **2197 pass / 3 fail / 3 existing optional Meshy-pack skips**. These results are retained in [suites-results.json](P699_assets/suites-results.json). Most failures exposed the center-point fixture assumptions described above; no failure was silently treated as a load flake.

The affected cohort contains **80 exact methods**, invoked three times each in separate test processes. The primary [affected ledger](P699_assets/affected-results.json) has **234 passing invocations / 3 failing invocations**; the three failures are the new accelerated human harness's extra zero-pose-correction assertion, discussed below. Every other method—including the MCP flight, the real-time human flight, every corrected fixture, and both unchanged receipt failures—passed **3/3**. The corrected accelerated harness's final triplet is retained separately in [human-complete-results.json](P699_assets/human-complete-results.json). The explicit `get_perf_stats` JSON now exposes aggregate `podsRestarted` and per-bot `podRestarts`; the outcome theory and existing API check passed three invocations each after that final change, recorded in [telemetry-results.json](P699_assets/telemetry-results.json). Together the final method triplets comprise **240 passing invocations / 264 passing data-case executions**.

The accelerated harness initially asserted zero pose corrections against an exact synthetic server clock. That exposed velocity-envelope rejection during decaying launch/warp momentum, despite a reported position within 2 cm of the server; it was a velocity/timing issue rather than failure to reach the station. Cookie, latency, frame-order, side-attribute and cadence trials are retained in the `human-*-results.json` ledgers; they did not establish a reliable zero-correction result and are **not classified as load flakes**. The accelerated docking/oxygen regression now applies native `SHIP_RESET` and its new cookie just as `Game1.UpdateOwnShipSimulation` does, uses side-adjusted prediction inputs and the production send cadence, and reports accepted/rejected counts. No server acceptance gate changed. The independent **real-time P540b test still requires zero pose rejects and zero resets** and passed **3/3** with the actual Game1 autopilot and real ping-derived clock.

The final build succeeded with **0 warnings / 0 errors**. Core, ClientCore and MonoGame then ran **unfiltered and sequentially**, with the local 15v15 P660 showcase explicitly enabled; Lobby also ran. [validation-summary.json](P699_assets/validation-summary.json) checks the final TRX start/finish times against the last build and each preceding suite and links compressed portable TRX evidence.

| Final suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4562 | 2 | 0 |
| ClientCore | 1019 | 0 | 0 |
| MonoGame | 2200 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

The two final Core failures were unchanged receipt tests: ripcord cancellation and loadout-change broadcast. After **all four final suites** completed, both passed **3/3 exact-method isolated runs**. The raw transport-peer timeout from the earlier attempt also passed **3/3** in that final isolation phase. All nine invocations passed; see [post-final-results.json](P699_assets/post-final-results.json). These satisfy the brief's load-flake rule and are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). No assertions, deadlines, quarantine traits or transport behavior changed for them. The three MonoGame skips are the existing unavailable private Meshy-pack checks; the local P660 showcase passed.

An earlier validation controller was interrupted to finish the explicit MCP telemetry JSON projection and rebuild, but its child runner continued in the background. That additional attempt is retained in [early-results.json](P699_assets/early-results.json) and compressed TRX archives: Core 4563 pass / 1 raw transport-peer join timeout, ClientCore 1019 pass, MonoGame 2200 pass / 3 existing skips. The later sequence is the final run; its TRX timestamps are checked against the last build and against each other. The final follow-up isolated triplets ran after both attempts finished.

The reproducible runners are [validate.py](P699_assets/validate.py) and [prepare_affected.py](P699_assets/prepare_affected.py); the JSON ledgers retain the recorded invocations and failures, with raw output and TRX paths under `/tmp/p699-validation/`.

Initial fixture diagnostics were corrected before final validation: one hand-guessed bay interior point lay outside ss24a's real bay, and the first human setup selected a different outbound mouth from the actual home route. The corrected point comes from a real entry frame, and the human setup starts at the router's proper mouth. These were test setup errors, not load flakes. A first build needed a writable NuGet HTTP cache (`NUGET_HTTP_CACHE_PATH=/tmp/p699-nuget`) because the sandbox makes the default user cache read-only; subsequent builds succeeded without weakening compiler checks.

The initial oxygen-countdown receipt failure also passed 3/3 unchanged in the affected cohort, and passed the final full Core run. Its initial symptom and isolated results are recorded alongside the other observations in [TEST_FLAKES.md](../TEST_FLAKES.md).

## P699b

Working-tree follow-up on `codex/p699b`, base `8ab81983`, 2026-10-15. No commits or pushes. The local workload uses the real `m2xspiderw.igc` Alderan (1052), Ganymede (1050), Organia (1051), PCore001 pod hulls and garrisons. Live rig/221/200v200 work remains in the Claude lane.

### Findings and changes

**Four landing slots are geometry, not four occupied berths.** Native `InGarage` evaluates each approaching ship independently, without reservations, capacity, a queue, or another ship's occupancy. Entering an eligible slot triggers docking immediately, subject to the existing warp-readiness gate. A new real-server regression puts **100 physical pods inside one real bay and docks all 100 in one tick**. The port already had no occupancy queue; no capacity bypass was needed.

The remaining local flight failures occur **before entering station collision range**, including pods that already reached their home sector. Hop-count routing ignores the distance between an arrival aleph and the next departure mouth, and the final home-sector crossing. An alternative first aleph can save enough flight time to fit the oxygen budget. MCP also reused its generic combat-route machinery for return trips.

- `PodReturnNavigation` routes over actual paired aleph mouths, minimizing the sum of physical flight distances, including the final exit-to-station leg. A short three-hop flight can beat a two-hop route with a long sector crossing. This is an explicit port routing improvement, **not a claim that native computes this complete distance metric**: native sorts its initial frontier by distance to the ship. MCP and human pod returns share the new selector. MCP caches the chosen mouth per sector/export topology and clears it on the next life, avoiding a graph search at every control tick.
- The shared docking planner now has the native drag-based stopping-distance calculation. MCP, human and NPC station approaches use it with the available hull/side values. This replaces the constant-deceleration estimate, including its missing backward-thrust multiplier. Equal-cost bay choices use a stable per-pod tie order; a genuinely nearer bay still wins. There are no bay reservations or forced detours to a more distant slot.
- Station rescue now respects native's **else-if**: a station offering the pod's landing class requires normal bay entry; the rescue-capability branch applies when that landing-class capability is absent. This prevents a rescue radius shortcut past an available garage. Existing rescue-only station paths remain covered.

### Native references

Every path below is relative to `~/Development/Allegiance/src/`, checked in the local native tree.

| Rule | Native file:line |
| --- | --- |
| Four-slot limit is a model-frame limit; capital prefix followed by ordinary garages | `Igc/igc.h:30`, `Igc/stationtypeigc.cpp:71`, `Igc/stationtypeigc.cpp:106`, `Igc/stationtypeigc.cpp:112` |
| Independent per-ship slot/plane test; fighters use all slots, other hulls the capital prefix; no occupancy check | `Igc/stationIGC.cpp:386`, `Igc/stationIGC.cpp:390`, `Igc/stationIGC.cpp:413`, `Igc/stationIGC.cpp:423`, `Igc/stationIGC.cpp:434` |
| Landing-class capability branch, bay entry, then dock callback; rescue is its else-if | `Igc/shipIGC.cpp:727`, `Igc/shipIGC.cpp:843`, `Igc/shipIGC.cpp:861` |
| Dock readiness followed by docked exit/status, without a berth allocation | `FedSrv/FedSrv.CPP:13087`, `FedSrv/FedSrv.CPP:13093`, `FedSrv/FedSrv.CPP:13107`, `FedSrv/FedSrv.CPP:13110` |
| Bay selection minimizes approach error; staging/pivot/entry law and class selection | `Igc/common.cpp:1157`, `Igc/common.cpp:1239`, `Igc/common.cpp:1287`, `Igc/common.cpp:1301`, `Igc/common.cpp:1513` |
| Backward multiplier, effective maximum speed/thrust, mass and logarithmic drag stopping-distance solve | `Igc/common.cpp:1936`, `Igc/common.cpp:1940`, `Igc/common.cpp:1943`, `Igc/common.cpp:1971`, `Igc/common.cpp:1982` |
| Native path search sorts the initial mouths by squared distance, then propagates the first warp through its frontier | `Igc/common.cpp:917`, `Igc/common.cpp:932`, `Igc/common.cpp:948`, `Igc/common.cpp:974`, `Igc/common.cpp:1001` |
| Transit relocates the ship into the destination cluster; readiness remains a separate gate | `FedSrv/fsship.cpp:126`, `FedSrv/fsship.cpp:134`, `FedSrv/fsship.cpp:1185` |
| Oxygen deadline is armed from the core constant on pod hull swap; native default is 300 seconds; expiry kills at the actual position with NULL launcher | `Igc/shipIGC.cpp:1407`, `Igc/missionigc.cpp:2724`, `Igc/shipIGC.cpp:361`, `Igc/shipIGC.cpp:364` |
| Friendly lifepod/rescue-capable ship collision triggers pickup; native sends PLAYER_RESCUED for a non-null rescuer, then finds a friendly restart station | `Igc/shipIGC.cpp:1027`, `Igc/shipIGC.cpp:1033`, `Igc/shipIGC.cpp:1040`, `FedSrv/FedSrv.CPP:13033`, `FedSrv/FedSrv.CPP:13052`, `FedSrv/FedSrv.CPP:13065`, `FedSrv/FedSrv.CPP:13076` |
| Native crowding counts pods separately and applies a per-sector cost | `Igc/clusterIGC.cpp:164`, `Igc/clusterIGC.cpp:185`, `Igc/clusterIGC.cpp:196`, `Igc/missionigc.cpp:2728` |

Oxygen remains the loaded **300 seconds**. Geometry, native slot limits, warp/launch cooldowns, damage, purchase gates and the normal dock/relaunch lifecycle retain their existing enforcement. Station rescue now applies the native landing-before-rescue precedence described above.

### Trip instrumentation

`PodTripTelemetry` begins at the server's actual ejection helper and terminates **before true-death relocation**, or at a normal dock/rescue. Ship rescues and station rescues have distinct `rescued` / `station-rescued` outcomes, captured before relocation. Each terminal `[pod-trip]` JSON includes ship/side, ejection sector/time, timed sector visits, nominal friendly home station/sector, first home-sector arrival, station collision-radius arrival, real garage entry, approach-envelope arrival, terminal time/sector/position, elapsed seconds and oxygen allowance. True-death restarts cannot become successful home arrivals. Removed ships and retired missions are discarded from active tracking.

Every 60 simulation seconds, `[pod-trip] window` reports docked/rescued/oxygen/other counts, oxygen deaths after home arrival, active trips, median/p95 completed trip time, oxygen allowance, and p95 time near the bay. `WaitedAtBay` accumulates time inside **station radius + pod radius + 50 units**, a diagnostic envelope including the staging ring just outside collision range. The 50-unit margin changes no flight, collision or docking rule. `RadiusAt` retains the actual collision radius and `BayAt` requires actual garage geometry. The reported home is a diagnostic nearest reachable friendly restart station; it does not issue orders or guarantee that a human selected that destination.

### Load reproduction and limits

The scale regression drives **100 pods per side**, four 50-pod ejection waves 40 seconds apart, around both relevant Alderan return mouths, just outside transit range. It retains native crowding and the real 300-second allowance. It uses the production ejection helper, flight integration, warp transits, reconstructed station bays, collision docking, and continuous outbound ships launched through the production undock helper. The direct-dock cohort uses an unmodified native miner hull without rescue capability, so every pod must physically enter a bay. A second cohort uses native rescue-capable fighter launches, testing real automatic pickups with separate outcome counts. Launch traffic routes toward Alderan. Controls are driven synchronously to isolate flight/physics from socket scheduling; this is **200 physical pod trips, not 200 network client sessions**. The separate MCP and human loopback regressions exercise the actual networked control and native pose paths.

The old sector-ID BFS / constant-rest flight law, selected only inside this regression via `P699B_BASELINE=1`, produced **100 normal docks / 100 oxygen deaths / zero other deaths**. This is a failing control-law reproduction, not a load flake or an exact replay of clash55. See [baseline ledger](P699b_assets/baseline-results.json) and its compressed TRX.

Both final cohorts passed three isolated runs each, with native oxygen unchanged:

| Cohort | Direct bay docks | Ship rescues | Oxygen / other deaths |
| --- | ---: | ---: | ---: |
| Non-rescue native launch hull, 3 × 200 pods | 600 | 0 | 0 / 0 |
| Rescue-capable native fighter launches, 3 × 200 pods | 487 | 113 | 0 / 0 |

The direct-dock runs each launched **136** physical traffic ships and require non-null collision-radius and **actual garage-entry** milestones for every pod. Median trip times were **215.70 / 215.60 / 215.58 seconds**, nearest-rank p95 **281.35 / 281.45 / 281.40 seconds**, and maxima **287.20 / 286.30 / 283.60 seconds**. P95 time within the diagnostic bay envelope was **8.45 / 8.05 / 7.95 seconds**. Both real bays receive entries. Rescue-traffic runs launched **112 / 114 / 114** ships and record pickups before relocation, without manufacturing home or bay milestones. See [flight-summary.json](P699b_assets/flight-summary.json) for every cohort, route count, metric, hash and portable trace. The test console summary uses the next order statistic for p95; the JSON uses nearest-rank p95.

In the baseline, all **100 oxygen deaths** had reached home, but none reached the approach envelope or collision radius. Both sides always chose one sector-ID-favored return route. For example, pod **20000** spent **193.40 seconds** reaching Rigel, reached Ganymede at **263.20**, and exhausted oxygen outside the station at **300.00**, at approximately **(27.39, 3010.41, 76.41)**. The corrected runs use all four two-jump return chains, 50 pods per chain. This directly distinguishes travel-budget loss from occupied bays.

There is **no universal guarantee for every pod merely reaching its home sector**. An exploratory central-sector grid produced **151 docked/recovered pods / 49 oxygen deaths**, with no other deaths: some initial positions plus intermediate and final crossings exceed native endurance. A nearest-first/shortest-hop exploratory run likewise recorded home-sector oxygen deaths before collision radius. Those observations remain in [central-grid diagnostics](P699b_assets/diagnostic-central-grid.trx.gz) and [nearest-mouth diagnostics](P699b_assets/diagnostic-nearest-mouth.trx.gz); they were not reclassified as flakes. The bounded scale target is zero added losses for routes with sufficient allowance. Rig logs from the new milestones can distinguish an intrinsically long return from a station-approach delay in clash55's actual fleet. This lane has not proven near-zero losses across all possible Alderan ejection positions.

Raw-trace review also found that early scale runs counted native ship rescues as ordinary docks. Those early rows are diagnostic evidence, not evidence of 200 physical garage entries; the final logger classifies rescues before relocation, and the direct-dock cohort requires non-null radius and real-bay milestones for every pod. The final validation sequence restarts after that correction.

The first harness attempted 200 simultaneous pods in Alderan. With the loaded core's crowding threshold, they died from environmental damage at about **14.3 seconds**, rather than oxygen. [The diagnostic trace](P699b_assets/diagnostic-simultaneous.trx.gz) is retained. Staggering waves and sending launches outward corrected that workload error; no crowding constant was changed. A later observer-side home-arrival counter missed pods that docked during the same tick; the final regression uses authoritative trip milestones and outcomes, not that observer counter or a post-death station latch.

### Verification

The final solution build passed with **0 warnings / 0 errors**. The [source manifest](P699b_assets/source-manifest.json) records the original 15 changed source files before the production build and the two subsequently corrected MonoGame fixture files before their test-only build; [corpus.json](P699b_assets/corpus.json) identifies the artwork and native sources read by this work.

The affected cohort contains **96 actual methods**, each with three passing exact-method isolated invocations: **288 passing invocations / 408 passing data-case executions**. This includes both 200-pod cohorts, the 100-pods-in-one-bay test, routing/drag/tie/telemetry discriminators, real docking and rescue tests, MCP and human flights, and affected NPC docking, launch, construction and economy behavior. See [final-focused-results.json](P699b_assets/final-focused-results.json), [isolate-additional-results.json](P699b_assets/isolate-additional-results.json), and [isolate-fixture-fixed-results.json](P699b_assets/isolate-fixture-fixed-results.json).

The MCP loopback returned in **242.10 / 241.50 / 241.90 simulation seconds**, with twelve physical relaunches each. Accelerated human flights returned in **239.25 / 240.15 / 239.65 seconds**, with **0 / 1 / 2** pose rejections handled by the existing native-reset application path. The independent real-time human flight passed **3/3**, with **zero pose rejections and zero SHIP_RESETs in every run**; each traversed both warps and docked after 4883 test ticks. No pose-acceptance gate or assertion was loosened. All nine network flights assert zero oxygen/true-death restarts. See [network-flights.json](P699b_assets/network-flights.json) and its linked raw traces.

Three focused filter invocations initially used the filename `P699PodOutcomeTests` for a partial class actually declared as `BotFixRegressionTests` and matched no tests. They are excluded from passing invocation/data-case totals. The correct declared-class method then passed **3/3**, two data cases each, at the start of the additional ledger. The runner now derives the declared class and treats empty test results as failure. Partial early validation attempts were interrupted during the rescue-count correction; they are excluded from the final evidence and totals.

The initial sequential unfiltered suites reported Core **4583 pass / 0 fail / 0 skip**, ClientCore **1060 pass / 0 fail / 0 skip**, MonoGame **2206 pass / 2 fail / 3 existing private Meshy-pack skips**, and Lobby **19 pass / 0 fail / 0 skip**. The local P660 showcase was enabled. See [initial suite ledger](P699b_assets/final-suites-results.json).

Both MonoGame failures reproduced **3/3 exact-method isolated runs**, so neither is classified as a load flake. `P698StaleShipsLoopbackTests` parked its pod 20 units from the station center; `P540EjectLiveProofTests` relocated its pod to a synthetic Land+Rescue station's center. Both had relied on the old rescue branch bypassing real garage entry. Their fixtures now use the existing `DockTestPlacement.Inside` helper, preserving all wire, retirement, cookie, hull, station and timing assertions. Both methods then passed **3/3**, including all three P698 scenarios: **12 passing data cases**. [The failing triplets](P699b_assets/isolate-failures-results.json) and [corrected triplets](P699b_assets/isolate-fixture-fixed-results.json) retain the evidence.

The fixture-only MonoGame test rebuild passed with **0 warnings / 0 errors**. All **22 production/Core/ClientCore DLLs remained byte-for-byte identical**, verified by [before](P699b_assets/binaries-before-fixture.json) and [after](P699b_assets/binaries-after-fixture.json) hashes. Core, ClientCore and Lobby results therefore remain applicable. The entire unfiltered MonoGame suite then passed with the corrected test assembly, including its ten-minute real-time P695b soak. The final results are:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4583 | 0 | 0 |
| ClientCore | 1060 | 0 | 0 |
| MonoGame, corrected fixtures | 2208 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

The three skips are the unchanged unavailable private Meshy-pack cases. No full-suite failure was dismissed as a load flake: both actual failures were reproduced, corrected as fixture preconditions, and verified in isolated triplets and the complete MonoGame rerun. [validation-summary.json](P699b_assets/validation-summary.json) checks source/binary identity, all 96 method triplets, initial suite ordering, failing-before/passing-after fixture evidence, relevant build timestamps, unchanged skip sets and test counts, and hashes every final portable TRX. The [corrected MonoGame ledger](P699b_assets/corrected-monogame-results.json) preserves its full result.

Reproduction and auditing use [validate.py](P699b_assets/validate.py), [summarize.py](P699b_assets/summarize.py), and [verify.py](P699b_assets/verify.py). The two builds and the recorded validation runner used `NUGET_HTTP_CACHE_PATH=/tmp/p699b-nuget`; raw logs/TRX remain under `/tmp/p699b-validation/`. All product changes and the report remain in the working tree, with no commits or pushes.

The first fixture rebuild encountered `CS2012` / read-only access to the existing generated MonoGame test object DLL. Backing up and regenerating that test project's object DLL/PDB allowed the clean rebuild; no production source, assertion, timeout, quarantine trait or oxygen allowance was changed for that build issue.
