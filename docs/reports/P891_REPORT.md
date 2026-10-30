# P891 — boarded source pose at docking, launch and flight

The native ship-update model now supplies a boarded child's source pose at launch and keeps it current outside the remote ranked budget. The docked P803 pose assertion was a port-mode artifact: the gunner uses the hangar, and a docked parent's `Latest` may correctly be absent. The P803 loopback test now uses the default native model, checks that docked state, launches through the real wire, and checks immediate launch state and 400-candidate flight freshness.

## Native truth, rechecked in the local native checkout

All citations below were read again for this task; retained numbered excerpts are `.scratch/p891/native-lines.txt`.

| Phase | Native state and messages | Screen |
|---|---|---|
| Docked | `Igc/shipIGC.h:421-459` removes the ship from its cluster when assigning its station. There is no cluster pose stream to manufacture for a docked carrier. The local IGC object and loadout/passenger metadata still exist. | Same-side docking selects `vmHangar` for a non-ghost (`WinTrek/trekigc.cpp:2853-2858`). The flight marker's source velocity (`WinTrek/indicator.cpp:130`) is not a prerequisite for this screen. |
| Launch | `CFSPlayer::SetCluster` exports the source ship's complete pose into `SET_CLUSTER` (`FedSrv/fsship.cpp:758-767`). It sends the identical guaranteed burst to each child before flushing the pilot's connection (`:918-929`). `clintlib/appmsg.cpp:1009-1010,1044-1049` applies that pose to the source and clears the child's station; `:1061-1065` changes the cookie only for the pilot. | The child leaves the hangar with its parent's launch pose already available. This is an event delivery, not a wait for the remote ranked quota. |
| Flight | Parentless players enter `rgShips`; boarded players enter the turret lists (`FedSrv/FedSrv.CPP:3763-3794`). This candidate distinction does **not** exclude children from the outbound player-recipient loop (`:4029-4047`). The child's client sends active/inactive turret input instead of a pilot pose (`clintlib/clintlib.cpp:1821-1823,1871-1910`). HEAVY position reference and fractions come from `GetSourceShip()` (`FedSrv/FedSrv.CPP:4044-4047,4128-4133`); `clintlib/appmsg.cpp:805-819` applies parentless heavy records and source fractions. | Flight HUD/camera rides the carrier; velocity marker reads `GetSourceShip()->GetVelocity()` (`WinTrek/indicator.cpp:130`). |

**Correction to the brief's hypothesis:** native does not reserve a special per-pass parent HEAVY slot. Its ranked assembly at `FedSrv/FedSrv.CPP:4148-4171` treats the parent as another eligible ship, except when it is the promoted command target. `CmpShipDeviation` (`:3614-3621`) has no parent term. Native guarantees the initial source pose through guaranteed `SET_CLUSTER` and advances its local IGC between ranked corrections. `SendShipUpdates` itself skips alternate calls (`:3664-3667`), and the native client update interval is 0.15 s (`Inc/Messages.h:226`). It would be inaccurate to cite native for an unconditional 20 Hz parent-pose transport guarantee.

The port's extra parent slot is therefore an explicit **era-constraint adaptation for its renderer**: the requested never-starved source telemetry replaces reliance on native's continuous local remote simulation. Native event semantics, hangar behavior and source selection are restored; the stronger periodic wire cadence is documented as a transport difference, not certified as byte-for-byte native scheduling. [Phase matrix](../parity/p891-boarded-pose.csv); SIM-0470 records this distinction.

## Root cause and change

Three separate issues were visible once the old teammate stream was removed:

1. P803 demanded a docked parent's pose even though native shows a hangar. The test now checks the station/boarding state and the legitimate missing parent pose before launching.
2. `PerformUndock` sent `SET_CLUSTER` only to the pilot. It now reuses that same encoded guaranteed packet for child peers. `ClientWorld.Apply(SetCluster)` previously marked the packet's source as the owned player and replaced the registry's own id; it now retains the child's identity/cookie and clears its station immediately when its parent launches.
3. A flying parent competed with remote ships for HEAVY selection. Ranked, legacy and optional tier projections now include one source record independently of the remote quota, de-duplicate it against a promoted/selected parent, and use the native source position/fractions. A dead, docked or different-sector parent is not eligible for this extra slot. No teammate position batch is restored. No baseline flags are changed.

The shared wire state feeds Classic and Modern; no style-specific workaround or new HUD behavior was added. The server's existing dirty own-child stream is outside this task and is not used as the source pose.

## Before and after measurements

The final loopback samples use the baseline: native ship updates, immutable snapshot, encode-once, parallel projection, pool transport, native GotoPlan, server GC; interest tiers and native broadcast order remain off. The dedicated optional-projection cases explicitly select their alternate path. The deterministic pressure fixture disables the optional 2 Hz floor so it cannot mask a missing parent guarantee.

| Measurement | Before | After |
|---|---|---|
| Docked P803, default native | Parent `Latest` absent; old assertion fails. | Parent `Latest` remains absent; gunner station and passenger/turret metadata support the hangar. |
| Small loopback launch | Parent HEAVY already happened to arrive in the launch tick, but the child still reported its old station. Usable flight state required **38 additional ticks / 1,900 simulation ms** in the retained baseline run. | Guaranteed source pose **and child station clearance in the launch dispatch: zero additional ticks** after the server processes launch. |
| 400-candidate pressure, deliberately lowest-ranked parent | First flight sample has no parent HEAVY in both serial and parallel regressions. | **40/40 current parent records** per case, with correct time, position, velocity, source reference and source fractions. Six cases cover ranked/legacy/tier projections × serial/parallel, including no target, parent target and another target. |
| Real TCP, two peers with 400 cluster candidates | No parent freshness exception existed. | **40/40 fresh parent poses**, in each of three affected-test runs. |

Baseline receipts: `before-launch.trx`, `before-pressure.trx`, `before-client.trx` in `.scratch/p891/`. The client regression also fails before the fix because the parent wrongly becomes `IsMine`. Final receipts: `final-*-1..3.trx` (8 Core, 5 ClientCore and 7 MonoGame cases per round; **60/60 passed**). The earlier `projections-1..3.trx` retain the six-projection checks before the final quota-only de-duplication refinement.

The launch bound is **N = 0 extra ticks after launch processing**, justified by native's same-event guaranteed fan-out, not by inventing a periodic quota deadline. At the test server's 20 updates/s, a pending launch can wait for a 50 ms dispatch boundary; socket delivery is allowed to finish without advancing simulation. The first three fixed P803 samples processed the request in two pump iterations and then needed zero additional ticks. Flight's parent record is stamped on every port broadcast pass (one 50 ms simulation tick here). TCP delivery is synchronized before measuring the next sample, so socket scheduling cannot be mistaken for ranked starvation.

## Tests and port-model pins

- `P891BoardedLaunchTests` preserves child identity, parent link, turret slot and cookie while applying the parent's launch pose and clearing the child's dock state.
- `P891BoardedPoseTests` checks docking exclusion and 40 flight samples under 400-candidate pressure in all six projection/mode cases; no duplicate parent when targeted, no boarded child in heavy candidates, and ordinary ranked viewers keep their budget.
- P803's default-native real loopback covers docked metadata, actual BOARD_SHIP, launch delivery without an extra tick, turret weapon/occupant identity and TCP pressure freshness. Existing P803 HUD/relay cases remain included in the three affected runs.

`ShipUpdateModel = "port"` was removed from P803. The remaining five explicit pins exercise contracts that this fix does not replace:

| Test file | Why its pin remains |
|---|---|
| `OwnShipCoalesceKeyTests.cs` | Compares the own-ship lane with the legacy teammate position batch's distinct coalesce key. |
| `LiveTurretGunnerTests.cs` | P209 orientation-only change on a boarded **child** reaches another peer through legacy `SINGLE_SHIP_UPDATE`; native uses the separate turret relay, already tested by P803/P822. |
| `ShipUpdateBudgetLiveTests.cs` | Asserts the legacy four-HEAVY folded-light budget. |
| `TeammatePositionBroadcastTests.cs` | Directly tests the legacy teammate position stream. |
| `P884RotationRetentionTests.cs` | Tests retention of latches populated only by the legacy teammate stream. |

## Real windows, both HUD styles

Both bounded two-client runs passed and exited normally. I opened and inspected all six final PNGs (hangar, gunner flight and pilot flight for each style) at 800×480. The higher-resolution offscreen attempt exposed a cropped SDL drawable; the final native-sized window avoids that capture artifact. The setup also waits for the pilot's real docked Gunship purchase before starting the gunner so it boards an equipped turret, rather than an observer seat.

| Style | Boarding and hangar | Flight visible in the inspected image | Same-frame source probe |
|---|---|---|---|
| Classic | Gunner 8099 boards pilot 8098, turret slot 1; hangar says “Turret of PILOT” / “IN TURRET”, with Launch/Buy/Command controls; parent pose absent in the wire log. | Red TURRET reticle, “Turret of: PILOT”, visible moving-source speed **73 mps** and the separate grey velocity-direction parentheses below the turret reticle. Pilot flight capture shows **72 mps**. | `sourcePose=True`, HUD input **72.75 mps**, wire **72.75 mps**, projected marker **(400, 291.64)**. |
| Modern | Same slot 1 and docked state; hangar visibly says “Turret of PILOT” / “IN TURRET”. | Modern gauges, “Turret of: PILOT”, FIRING, speed **73 mps** and the small cyan velocity-direction glyph below the crosshair; pilot flight capture shows **73 mps**. | `sourcePose=True`, HUD input **72.75 mps**, wire **72.75 mps**, projected marker **(400, 291.64)**. |

Screenshots are scratch-only: `.scratch/p891/final-windows/classic/GUNNER/{boarded-hangar,gunner-flight}.png`, `.scratch/p891/final-windows/modern/GUNNER/{boarded-hangar,gunner-flight}.png`, and each style's `PILOT/pilot-flight.png`. The small hangar frame has overlapping existing shop/stat text, but the boarding label and hangar controls are visible; no flight HUD is used there. Pilot/gunner captures occur at different instants, so their rounded speeds are not expected to match each other exactly. `final-windows/classic/` and `final-windows/modern/` retain server/pilot/gunner logs and the wrapper's zero exit results are in `final-windows.log`.

 All windows use `SDL_VIDEODRIVER=offscreen`, an empty `DISPLAY`, isolated writable XDG/HUD profiles, `timeout`, and `--exit-after`; no xvfb or desktop `:0`. The scratch harness uses production Game1 clients, TCP sessions, shop purchase, board and launch messages. Purchase-only `liveSettings.testUnlock` grants tech/money for selecting the Gunship; it changes neither flight constants nor pose outcomes. The scratch wrapper makes a local turret-look action toward the received carrier velocity with a small offset, so the direction lies inside the view; this changes only the local aim/camera input, as the existing auto-yaw proof does. `Game1` computes and draws the velocity marker itself, with its actual projected coordinate logged before capture. No parent pose, speed, boarding outcome or client world state is injected.

## Per-tick cost

For **G airborne child peers**, at most **G additional HEAVY records × 39 bytes** are appended to existing packets each broadcast pass: at 20 updates/s, **780 bytes/s per child** before existing framing, and **zero extra periodic packets**. Existing ranking and its quotas run unchanged; only child peers inspect the selected lists, append a missing source and remove its redundant LIGHT entry. A source already supplied by ranking or target is not duplicated. There is one additional parent pointer/eligibility check per existing peer projection; a child with a missing source adds one relative-record write and cached-record lookup. Only boarded peers scan their selected quota for de-duplication; ordinary peers gain no per-candidate comparisons. The existing per-pass shared capture dictionary captures any newly needed parent at most once across all its children. No new peer-pair collection, status stream, transport worker or per-tick peer scan is introduced. Without boarded peers, ranked bytes and quotas remain unchanged.

Launch adds one existing 54-byte `SET_CLUSTER` message plus the existing packet CRC per child, once per event. The launch-only lookup walks the peer registry once, O(peers), and enqueues O(children) deliveries; it does not run per tick. Native uses its direct child list, while this port resolves connected child peers from its existing registry.

The bounded real TCP sample (two peers, 400 candidates, 40 ticks/run) measured tick means **7.6011 / 7.8386 / 8.4831 ms**, p95 **13.6696 / 16.1389 / 18.4514 ms**, and maxima **17.5234 / 17.7254 / 28.5402 ms**. These are absolute local costs including existing simulation, visibility and projection, not an A/B claim about the fix's isolated CPU delta or a 400-connected-peer benchmark. In the ranked-only deterministic fixture at that pressure, an untargeted child packet has 15 heavy records: the existing density budget's 14 plus one parent. Claude's rig A/B remains the large-match performance authority. No remote host was contacted and no synthetic CPU load was created.

## Gate

| Check | Result / receipt |
|---|---|
| Whole solution | PASS, 0 warnings / 0 errors, `build-final.log`. |
| All tools | PASS, all 94, 0 warnings/errors, `build-tools-final.log`. |
| Server linux-x64 Native AOT | PASS, `aot-final.log`, no IL2026/IL3050 or other warnings. |
| New + P803 affected cases, 3× alone | PASS, 8 Core + 5 ClientCore + 7 MonoGame each round = **60/60**, `final-*-1..3.trx`. |
| Artwork training Fast + Live, both client projects, 3× | PASS, **76 + 60 per round = 408/408**, including Nanite completion. Raw `.scratch/p891/training/` (moved from the runner's timestamped scratch directory after completion); driver receipt `training.log`. |
| Showcase never-parks, 5× | PASS **5/5**, run durations 256.3 / 231.1 / 203.8 / 207.7 / 242.6 s; longest targeted idle 0.609 / 1.315 / 1.413 / 0 / 0 s, all below 5 s. `never-parks-1..5.trx`. |
| Real windows | PASS Classic + Modern, four clients exit 0; six images inspected; `final-windows.log` and `final-windows/`. |
| ONE Fast tier | **FAIL**, 11,748 tests in 543.6 s: 11,739 passed / 6 failed / 3 skipped; six timing violations. All **20 P891/P803 cases passed**. `fast-tier/fast/summary.json`; details below. |
| Glossary, parity, whitespace | PASS (`glossary.log`, `parity.log`, `git diff --check`); both parity CSVs have zero CR bytes. |

The Fast sweep is retained as **red**; it was not repeated. Every failed method and timing offender was selected alone three times, serially, with artwork and server GC. Theory methods ran all their cases on each invocation. Full ordered results and timings are in `.scratch/p891/isolation.json`, with raw receipts in `isolation/`; [the P891 flake ledger](../TEST_FLAKES.md) records all eleven methods.

| Failed Fast case | Three isolated results | Assessment |
|---|---|---|
| P761b queued-station command relay | pass / pass / pass | Previously ledgered target-name/receipt flake; sweep saw “goto” instead of “goto Garrison”. |
| BotFlight independent trajectory | pass / pass / pass | Previously ledgered timing flake. |
| P838 GC profiler | pass / pass / pass | Sweep measured 8,742.1 MB process-wide allocation against a 0.8–16 MB fixture bound; isolated execution is green. No profiler code changed. |
| DockDocking far-from-station status | pass / FAIL / FAIL | Known status-history intermittency, also reproduced using the original server binary: 7/8 pass, one identical stale docked status. Not claimed fixed. |
| NPC defender disengage | FAIL / FAIL / FAIL | Known persistent P872/P887 baseline failure; unchanged NPC path. |
| Probe lethal hit | pass / pass / FAIL | Remains an intermittent unboarded ordnance fixture observation. Original-server replay passed 11/11 serial plus 6/6 at concurrency 3, so this report **does not claim that the baseline replay reproduced this failure**. No collision/damage source changed and the parent branches are inapplicable to this fixture. Its failure is not claimed fixed or 3/3 passing. |

Four other timing offenders (P800 all-hulls GPU, P796 varied purchases, P814 turret-carrier preference and SC2 Nanite support) passed 3/3 below the 10 s limit in isolation; maximum per-case times were 4.273 / 7.182 / 4.539 / 4.694 s respectively. Probe's failed isolated sample still took 16.746 s. P777 warp-exit measurement passed functionally 3/3 but remains over the timing limit at 10.176 / 10.117 / 10.107 s, consistent with the existing ledger.

The baseline experiment compiles a separate Server copy restoring all four modified Server files from original HEAD **b567ee1f**, then runs the same test DLL against that binary in a separate directory. It never reverts the working tree or replaces the fixed test binaries. The first three docking/probe baseline runs passed; a bounded further docking replay reproduced the stale status on run 8. The additional probe replay, including parallel runs of the real test (not synthetic CPU load), did not reproduce its failure. Sources/receipts: `baseline*.py`, `baseline-build.log`, `baseline-results/`, `baseline-more.json`, `baseline-parallel.json`. No assertion, test quarantine, timing limit or production rule was relaxed.

The runner's original timestamped Fast output was moved after completion from `TestResults/tiers-20261030T032640.368112Z/` into `.scratch/p891/fast-tier/`; raw log metadata still records the original execution paths. Disposable AOT/proof/baseline binaries and caches were removed; source harnesses, all cited receipts and final screenshots remain. Raw logs/TRX and retained harness sources are git-excluded under `.scratch/p891/`; screenshots remain there, with no new media added to the repository.

## Native-intent footer

**Classification:** native behavior restoration for docked UI prerequisites, source launch fan-out, child ownership/station/cookie handling and HEAVY source reference/fractions; explicit era-constraint adaptation for the extra per-pass parent telemetry slot. Native has no unconditional per-pass parent exemption. No gameplay constants or baseline configuration defaults are changed. No commits or pushes were made; all changes are left in the working tree.
