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
