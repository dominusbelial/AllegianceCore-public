# P709 — parity wave 12: client callback delivery

Entry: `5556eb0f` on `codex/p709`. Native sources were read locally under `~/Development/Allegiance/src/`; native paths below are relative to that directory. No commits or pushes are made. Changes remain in the working tree.

**Validation status: blocked by the required parking gate (8/10 pass, 2/10 fail).** The callback bundle closes five contracts and improves three that remain partial. Full-suite verification is complete. The same parking failure reproduces on untouched entry revision `5556eb0f` (2 pass / 1 fail in three final isolated runs); P707 owns the excluded fix.

## Selected bundle

Eight existing **partial callback rows**, weight **24**: SIM-0172–0175, SIM-0193–0195, and SIM-0228. These share the site/chat/message-to-client-state path: formatted text, delivery, queued monetary requests, HUD feedback, and change notifications. They can be exercised through the same real TCP sessions and headless client state without editing simulation scheduling or equipment behavior.

Five contracts now have executable implementations and traced tests. Three broader contracts remain **partial**, with their newly implemented paths and remaining gaps recorded explicitly. No credit is claimed for those three rows. Periodic rows were not selected; this bundle stays entirely within the permitted callbacks area.

`Sector.Update`, swept collision, broadcast scheduling/interest tiers, security, sounds/assets, pods/docking, showcase modes, commander/loadout research, and the P707 parking/dispenser/readiness code are outside this change.

## Native rules, implementation, and intent

| Row | Before → after | Native rule and consumer | Port implementation / proof |
| --- | --- | --- | --- |
| SIM-0172 ReceiveChat | partial → partial | `Igc/igc.h:4792`; `clintlib/clintlib.cpp:2160-2172`; `clintlib/appmsg.cpp:2640-2688`; `WinTrek/trekigc.cpp:4788-4822`: insert chat, then notify listeners, including buoy chat | `ClientWorld.Callbacks.cs:76`: both event logs are updated before `ChatReceived`. `P709ClientCallbacksTests.Chat_and_buoy_callbacks_fire_once_after_the_event_log_is_updated`; TCP tests also exercise message receipt. Complete mute/lobby filters, sonic text, command presentation, and native local-echo behavior remain open. |
| SIM-0173 SendChat | partial → partial | `Igc/igc.h:4801`; `FedSrv/FedSrv.CPP:13652-13693`: preserve sender, route, voice, command, target, and object-model fields into forwarding | `ServerHost.cs:507` → `Server.Callbacks.cs:12`: actual site override, mission-scoped recipient selection, existing guaranteed send. `P709CallbacksLiveTests.Site_chat_preserves_sender_command_target_and_routes_only_to_the_named_team`. Allies/Ship/Admin groups, NPC order forwarding, and null-source mission context on multi-mission hosts remain open. Existing direct producer adapters remain. |
| SIM-0174 SendChatf | partial → implemented | `Igc/igc.h:4812-4825`: format once, then dispatch virtual SendChat with its default command/target arguments | `IIgcSite.cs:117`, `CallbackText.cs:12`, `ConnectionSession.cs:431`. Virtual-dispatch/default-argument, culture, boundary, and TCP tests in `P709CallbackTextTests` and `P709CallbacksLiveTests`. Underlying SendChat group/producer gaps stay in SIM-0173. |
| SIM-0175 GetMoneyRequest | partial → implemented | `Igc/igc.h:4828`; `clintlib/appmsg.cpp:2630-2635`; `WinTrek/trekigc.cpp:4462-4465`; `WinTrek/WinTrek.cpp:10728-10759`: resolve sender, replace queued order with one money request, show critical HUD text; `:7686-7687` strict expiry and `:10627-10652` approval clamp | `ConnectionSession.cs:1102` decodes REQUEST_MONEY → `ClientWorld.Callbacks.cs:35`; accept/execute keys emit PLAYER_DONATE through `Game1.Orders.cs:59`. Tests cover strict 15-second boundary, replacement, reject, reset, missing/self sender, clamping, held/suppressed keys, and real request → approval → server wallet transfer over TCP. |
| SIM-0193 PostPlainText | partial → implemented | `Igc/igc.h:4847`; `WinTrek/trekigc.cpp:3067-3083`: preserve literal text and select critical/noncritical HUD tip | `ClientWorld.Callbacks.cs:10` → existing `Game1.Feedback.cs:88` HUD drain. Literal braces/percent signs, long plain text, criticality, timestamps, and once-only draining are tested. The server's no-console base callback remains a no-op, as native. |
| SIM-0194 PostText | partial → implemented | `Igc/igc.h:4848`; `WinTrek/trekigc.cpp:3086-3102`: format, then call PostPlainText | `IIgcSite.cs:170` and `ClientWorld.Callbacks.cs:17`, used by the money-request HUD. Tested virtual forwarding, invariant formatting, and output bounds. This wrapper does not claim completion of every native notification producer. |
| SIM-0195 PostNotificationText | partial → partial | `Igc/igc.h:4849`; `WinTrek/trekigc.cpp:2994-3018`: only the actual local ship gets text, including while boarded | API declared; `ClientWorld.Callbacks.cs:22` implements the ownership check and shared formatter/HUD path. Owner/carrier/observer tests distinguish source-carrier sound eligibility from text eligibility. All resource/reload/equipment producers are not routed through this callback; P707 and P710 work remains separate. |
| SIM-0228 ChangeGameState | partial → implemented | `Igc/igc.h:4899`; `clintlib/appmsg.cpp:2905`; `WinTrek/WinTrek.h:258`: refresh game-state container after KILL_SHIP processing, for both death and eject | `ClientWorld.cs:3562` → `ClientWorld.Callbacks.cs:67`: revision plus synchronous `GameStateChanged` after state/feedback mutation. Test listener sees the completed mutation and one recorded kill; no mission-stage transition is manufactured. The UI already reads this state each frame. |

Source prefixes: `src/Allegiance.Core/Sim/`, `src/Allegiance.ClientCore/State/`, `src/Allegiance.ClientCore/Session/`, `src/Allegiance.Server/`, and `src/Allegiance.ClientCore.Game/`. Tests are in the correspondingly named Core, ClientCore, and ClientCore.MonoGame test projects.

**Native intent:** all eight callback contracts are **design**, preserved as such in `native_class`. C varargs, ANSI buffers, and the literal Insert-key instruction are **era constraints**: managed callers use invariant .NET composite formats, valid UTF-16, a safe 255-character formatted-output cap, and the rebindable Accept/Execute actions. Native `_vsnprintf` can leave an exactly full 256-byte buffer unterminated (`Igc/igc.h:4817-4825`, `WinTrek/trekigc.cpp:2999-3005,3091-3100`); that unsafe boundary is a **bug**, not behavior to reproduce. Plain text is neither reformatted nor truncated by this helper. The client queues donation intent and lets the server apply wallet updates once; it does not add native same-process optimistic credit on top of the existing authoritative wire credit (`clintlib/clintlib.cpp:2564-2581`). Missing hull metadata gets a neutral hull-id caption instead of native's failed assertion.

**Bots:** no controls, mounting, dispensers, energy/ammo, navigation, or AI decision inputs change. Money requests affect the human client's queued approval slot; only explicit Accept/Execute sends a donation. Plain session chat now defaults to native `c_cidNone` rather than `c_cidDefault`, including buoy chat; explicitly supplied command ids are preserved. The real site chat path preserves all command fields but does not add NPC order execution. The regression and continuity checks below exercise these paths independently; a failed hard gate remains a release blocker even when focused callback tests pass.

## Scorecard before → after

Both scorecards validate all **473** simulation rows and **438** interface rows. Partial earns zero credit. Only the eight selected simulation records change; native rule text, citations, weights, and populations are preserved. The interface matrix is unchanged. Raw outputs: [before](P709_assets/scorecard-before.txt), [after](P709_assets/scorecard-after.txt).

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 45.45% (90/198) → **53.03% (105/198)** | 30/66 → **35/66** |
| periodic | 25.53% (12/47) → 25.53% (12/47) | 3/15 → 3/15 |
| constants | 95.56% (129/135) → 95.56% (129/135) | 37/39 → 37/39 |
| sounds | 25.08% (149/594) → 25.08% (149/594) | 46/228 → 46/228 |
| updates | 71.35% (249/349) → 71.35% (249/349) | 59/87 → 59/87 |
| warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| TOTAL | 47.94% (639/1333) → **49.06% (654/1333)** | 177/437 → **182/437** |

## Verification

Build: `NUGET_HTTP_CACHE_PATH=/tmp/p709/nuget-http dotnet build AllegianceCore.slnx --nologo -m:1 -v:q`, **0 warnings / 0 errors**. The cache override supplies a writable NuGet HTTP cache without disabling audit. Build log: `/tmp/p709/build-final.log`.

The [selected cohort](P709_assets/cohort.json) lists 79 new/affected exact-method filters. The [invocation ledger](P709_assets/verification.json) records command arguments, environment overrides, timestamps, raw counters, failures, and TRX/log locations for every final verification invocation. Theory filters execute all data cases of that method. Invocations are serial within this lane; other lanes can still load the machine. No external rig/221 or 200v200 invocation is run. Legacy opt-in rig-style recording probes retain their default gates; the required local P700/P660 showcase and explicit local battle are enabled separately.

Focused certification completed: **79 methods × 3 exact-method invocations = 237 invocations / 315 data-case executions, 0 failures / 0 skips**. This includes all **16 new methods / 23 new cases**, plus the affected site defaults, feedback, chat/session, order, key handling, and notification-consumer tests.

**Mandatory showcase gate: FAILED — 8 pass / 2 fail / 0 skip across the ten required isolated runs.** Runs 4 and 9 failed the unchanged five-second parking assertion. Subsequent isolated passes do not convert this into a passing 10/10 gate. The brief explicitly assigns parking/dispenser/readiness fixes to P707 and says not to touch that code; this lane leaves it unchanged. The untouched-HEAD comparison below reproduces the same parking failure.

| Run | Outcome | Longest stopped with target (s) | Kills recorded |
| ---: | --- | ---: | ---: |
| 1 | pass | 4.709 | 15 |
| 2 | pass | 4.214 | 17 |
| 3 | pass | 4.107 | 12 |
| 4 | **fail: parked >5 s** | >5 | test aborted before summary |
| 5 | pass | 4.009 | 14 |
| 6 | pass | 3.714 | 15 |
| 7 | pass | 3.606 | 13 |
| 8 | pass | 4.108 | 15 |
| 9 | **fail: parked >5 s** | >5 | test aborted before summary |
| 10 | pass | 4.411 | 14 |

The failed ships were a **Hvy Interceptor**, ship 8121, side 1, target 8102 (run 4), and an **Adv Scout**, ship 8122, side 1, target 8101 (run 9). Both were in sector 1052 with `WorldReady=true`, `State=active`, and zero velocity. Exact snapshots and stacks are preserved in the invocation ledger.

Local continuity passed **3/3 exact-method runs each**: `MiningSellTests.Loopback_mine_then_dock_sells_ore_and_pays_FM_S_PAYDAY` (three cases total) and `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights` (both death/pod cases each run, six cases total). These exercise real ore sale/payday traffic and purchase → fight → death/pod return or pod destruction → repurchase/relaunch → damage again.

The separate local **15v15 / 90-second** battle passed its positive-kill assertion. [Receipt](P709_assets/battle-receipt.txt) and [detail](P709_assets/battle-details.txt):

```text
RESULT_P632 p709 model=port debug=1 size=15/side window=90s botsReached=30/30 killsA=2 killsB=1 killsTotal=3 deathsA=1 deathsB=2 shots=15827 hits=8586 hitRate=54.2 % friendly=0 shieldHits=5513 hullHits=3073 maxTargeted=30 maxVisibleEnemies=15
```

The required unfiltered suites ran serially after the final build and focused checks:

| Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4691 | 2 | 0 | 780.341 |
| Allegiance.ClientCore.Tests | 1087 | 0 | 0 | 665.799 |
| Allegiance.ClientCore.MonoGame.Tests | 2237 | 0 | 3 | 935.245 |

ClientCore's enabled P660 local 15v15 next-match showcase passed in **21.761 s**;
its additional unfiltered P700 run passed in **100.598 s**. That extra pass does
not replace either failure from the prescribed ten-run gate. MonoGame completed
its human-flight soak and retained only the three existing unavailable private
Meshy-pack skips (`Pack_built_content_directory_exists`,
`Pack_catalogue_merge_yields_identical_resolution_to_before`,
`Pack_catalogue_has_the_full_meshy_set`). Skips are counted from individual TRX
results rather than the runner's aggregate `notExecuted` field.

After **all three suites** finished, both failed Core methods passed **3/3 exact-method isolated retries**:

| Full-pass method | Raw failure | Final isolation |
| --- | --- | --- |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Empty receipt collection at `Assert.Contains` | 3/3 pass |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | Empty receipt collection at `Assert.Single` | 3/3 pass |

These meet the brief's load-flake rule and are recorded in `docs/TEST_FLAKES.md`.
No assertion, timeout, quarantine setting, or transport behavior was relaxed.
The isolated passes do not establish a fix for the underlying receipt races.
The production-tree ledger contains **263 completed invocations**; every exact
filter executed at least one case. Source/test fingerprints still match the
final build ([manifest](P709_assets/source-sha256.json)).

**Untouched-base comparison: the parking failure is pre-existing.** A read-only
`git archive HEAD` source copy of entry revision `5556eb0f` was built under
`/tmp/p709/base`. The [baseline source manifest](P709_assets/baseline-source-sha256.json)
checks the original versions of every changed tracked source file against HEAD;
all new P709 source/test files are absent. The restored project file also matches
HEAD byte-for-byte. The final baseline build used `dotnet build` with
`--no-incremental -m:1` and completed with **0 warnings / 0 errors** (log:
`/tmp/p709/baseline-certified/build.log`).

The final comparison ran alone, after all production-tree suites and retries,
with a separate output directory and [certified comparison ledger](P709_assets/baseline-parking-certified.json).
It ran at least three exact-method repetitions, stopping once the parking failure
was reproduced: **2 pass / 1 fail / 0 skip**. Baseline run 3 failed at the same
unchanged assertion with a **Hvy Interceptor**, ship **8121**, side 1, target
**8102**, sector 1052, `WorldReady=true`, `State=active`, and **zero velocity**.
The base failure stack points to `/tmp/p709/base/tests/Allegiance.ClientCore.Tests/P700ShowcaseLiveTests.cs:51`.
This establishes that the failure exists without P709's changes; it does not
establish its internal cause or fix it.

**Disposition:** the callback changes and report are ready in the working tree,
but **P709 does not satisfy the mandatory zero-failure parking gate**. The brief
explicitly says P707 owns parking/dispenser/readiness and to leave that code
alone; this wave preserves that boundary. The dispatcher should retain the WIP
and re-run the ten-run gate after P707's fix is integrated. Three selected broad
callback rows also remain partial as itemized above; their remaining behavior
has not been credited or presented as implemented.

Final integrity checks: [audit](P709_assets/integrity-audit.json), validated
scorecards, unchanged source/test fingerprints since the final production-tree
build, and clean `git diff --check`. HEAD remains `5556eb0f`; no commits or
pushes were made.

Exploratory runs are separate from certification: the money-request live fixture initially omitted the starting payday and asserted funds that had not been issued. It now calls the real starting-payday path before requesting money. No product assertion or deadline was relaxed. Compilation corrections were missing type aliases and the fixture's pose type. The final rebuild and exact-method repeats use the corrected sources.

The baseline diagnostic initially queued duplicate runners. Session interruption
returned exit 130 but did not stop their descendants; late writes exposed continued
overlap. All comparison artifacts from `/tmp/p709/baseline-results` and
`/tmp/p709/baseline-final` are excluded. Temporarily withholding only the `/tmp`
comparison project file prevented those bounded loops from starting further tests;
the file was restored before the final isolated comparison. The production-tree
verifier had already finished and was unaffected. The final comparison used `/tmp/p709/baseline-certified`, its own ledger, and
cooperative cancellation control; only that final comparison is used above.

## P734b merge

Resolved P709 `61b3ec354d65a2b4923076258672580cd998c054` onto the P734
base `29174b282a24903e113b991efd8ff2742b9bb149` (master `d0acd9ec` plus
P708). The historical P709/P708 gate holds above describe their original
branches; the results in this section record checks on this merged working tree under
the **2026-10-17 light gate**: three isolated runs per affected method, five
never-parks runs, and **one** complete suite sweep. The dispatcher's mini
runner owns the second sweep.

### Resolution and preserved behavior

`ClientWorld.Apply(ChatMessage)` delegates to the P709 `ReceiveChat` consumer.
That consumer now inserts the event, runs master's unchanged P710 chat sound
classification/leader lookup, queues notification HUD text through
`PostPlainText`, and invokes `ChatReceived` once after those mutations.
`CHATBUOY` retains its event insertion and P709 callback. There is one message
insertion, one eligible sound, and one eligible HUD line per delivery. The new
`Chat_callback_observes_sound_and_notification_once_on_wire_and_direct_delivery`
regression checks the combined contract through both `Apply` and direct
`ReceiveChat`, including state observable inside the listener and empty drains
afterward. Notification voice-over markers remain silent in generic chat
classification; the existing HUD feedback consumer retains notification audio.
`docs/TEST_FLAKES.md` contains the union of both histories.

Native references below are relative to **`~/Development/Allegiance/src/`**.
The reviewed source excerpts and SHA-256 hashes are in
[native-reviewed.txt](P734b_assets/native-reviewed.txt) and
[native-manifest.json](P734b_assets/native-manifest.json).

| P709 row | Retained rule | Native file:line |
| --- | --- | --- |
| SIM-0172 | Insert ordinary/buoy chat before notifying listeners; preserve master's announcement precedence, own-echo/sonic silence and notification feedback | `clintlib/clintlib.cpp:2160-2172`; `clintlib/appmsg.cpp:2640-2688`; `WinTrek/trekigc.cpp:4536-4560,4788-4822` |
| SIM-0173 | Site chat carries sender, recipient, voice, command, object type/id and object-model flag into mission-scoped forwarding | `Igc/igc.h:4801-4810`; `FedSrv/FedSrv.CPP:13652-13693` |
| SIM-0174 | Format once, then dispatch the virtual SendChat with native default command/target arguments | `Igc/igc.h:4812-4825` |
| SIM-0175 | Resolve sender, replace queued request, show critical text, expire strictly after 15 seconds, clamp explicit approval to current funds and consume once | `clintlib/appmsg.cpp:2630-2635`; `WinTrek/trekigc.cpp:4462-4465`; `WinTrek/WinTrek.cpp:7686-7687,10627-10652,10728-10759` |
| SIM-0193 | Preserve literal plain text and critical/noncritical HUD selection, including notification messages | `Igc/igc.h:4847`; `WinTrek/trekigc.cpp:3067-3083` |
| SIM-0194 | Format then dispatch PostPlainText through the shared HUD queue | `Igc/igc.h:4848`; `WinTrek/trekigc.cpp:3086-3102` |
| SIM-0195 | Notification text belongs to the actual local ship, including while boarded, rather than the source carrier | `Igc/igc.h:4849`; `WinTrek/trekigc.cpp:2994-3018` |
| SIM-0228 | ChangeGameState revision/listener fires after completed KILL_SHIP death/eject state and feedback mutation | `Igc/igc.h:4899`; `clintlib/appmsg.cpp:2797-2905`; `WinTrek/WinTrek.h:258` |

The callback rules remain design. P709's documented managed adaptations are
retained: invariant composite formatting, a safe 255-character formatted cap
without split UTF-16 pairs, rebindable approval actions, and authoritative
server wallet credit rather than a duplicate optimistic client credit
(`Igc/igc.h:4817-4825`; `WinTrek/trekigc.cpp:2999-3005,3091-3100`;
`clintlib/clintlib.cpp:2564-2581`). SIM-0172, SIM-0173 and SIM-0195 remain
**partial** with their original remaining gaps; five rows remain implemented.
No extra callback completion is claimed.

Re-read both the original P708 report and its P734 merge appendix, plus P707's
parking report. P708's eight rules and the P734 docking correction remain:

| P708 row | Retained rule | Native file:line |
| --- | --- | --- |
| SIM-0104 | Five-second unsafe ripcord retry, allied-drone/mining right of way, drift hazard scan, drone-rip indicator and mining cancellation | `Igc/shipIGC.cpp:2065-2080,4154-4228`; `Igc/common.cpp:1648-1751`; `Igc/shipIGC.h:213` |
| SIM-0108 | Arrived NPC miner faces its rock, rolls at 1, kills throttle and clears motion/fire inputs before travel Dodge | `Igc/shipIGC.cpp:2249-2260` |
| SIM-0112 | Visible combat-pilot defend acquisition, strict 1500 radius, return/switch distances, shared 200-pass checks and team chat | `Igc/shipIGC.cpp:2030-2056,2370-2407,2680-2705` |
| SIM-0113 | Side knowledge/scanner visibility, reduced unseen-target lead through three seconds, coast through five, then investigate/drop with interrupted-order restoration | `Igc/shipIGC.cpp:2410-2460`; `Igc/shipIGC.h:380-389,748-787` |
| SIM-0114 | Strict .45/.90/1.5 lifespan bands, manual thrust, speed matching, boost hysteresis, Dodge retention and ten-second retarget with ships before probes | `Igc/shipIGC.cpp:2000-2027,2483-2576,2589-2604`; `Igc/common.cpp:1580-1644` |
| SIM-0115 | Select command target before magazine lock; UseMissiles/full-lock/front-hemisphere/strict transverse-speed gates; repair releases old attack inputs | `Igc/shipIGC.cpp:2578-2593,2673-2677`; `Igc/magazineigc.cpp:103-167`; `Igc/shipIGC.h:743-746` |
| SIM-0117 | Accepted cross-sector attack follows only a return-leading nearest warp at strict distance <800 and scanner >100; retain accepted attack during temporary goto | `Igc/shipIGC.cpp:2707-2745` |
| SIM-0118 | 100-pass goto checks, accepted-attack reacquisition, ten-second aggression, ordered ship/probe priority and temporary flag consumption | `Igc/shipIGC.cpp:2000-2027,2747-2790`; `Igc/igc.h:784-792` |

P708's intentional corrections of native's inverted retry comparison and the
zero-drag analytic limit remain (`Igc/shipIGC.cpp:2066-2078,4216`). The
P734 idle-combat guard still preserves docking autopilot thrust ownership
(`Igc/common.cpp:1287-1540`); active combat retains P708's control decisions
(`Igc/shipIGC.cpp:2483-2604`). P707's two-second escape and half-second hold
are explicitly **showcase harness policy**, unchanged here. Its native
underpinnings remain close-band speed matching, compact velocity zero below
1 m/s, lateral controls and manual-thrust priority
(`Igc/shipIGC.cpp:2492-2511,2589-2602,2965-3005`;
`Igc/igc.h:2282-2310,2444-2454`).

The [source comparison](P734b_assets/ours-preservation.json) finds 2,392
existing files under `src/`, `tests/`, and `tools/` byte-identical to P734;
the eight changed existing files contain only the P709 callback, chat,
queue/input and donation additions. Reviewed diffs:
[ClientWorld](P734b_assets/ClientWorld.diff),
[automatic merge](P734b_assets/automatic-merge.diff).
In particular, P708 simulation, P707 parking, P699c docking and all MCP sources
are unchanged from P734. P712's tech/bucket reconciliation, P713 screens,
P706 and P710-P717 sound work survive; the chat sound invocation moves into
the callback consumer. No P733 finale/speed or HD-1/HD-2 implementation work
was performed.

### Matrix reconciliation

[Row audit](P734b_assets/matrix-audit.json): all **473** simulation rows were
compared by row ID with master, P734 and P709. Exactly the eight P709 callback
rows differ from P734; exactly the P708 eight plus P709 eight differ from
master. Every other master row is identical, including **all periodic rows**.
All eight P708 rows remain **implemented**, byte-for-byte equivalent as CSV
records to P734. P709's eight rows match its branch, retaining three partial
statuses. The matrix already contained the correct union, so no rewrite was
necessary; it contains **zero CR bytes** and uses LF endings. The
[scorecard](P734b_assets/scorecard.txt) validates all 473 simulation and 438
interface rows. Despite the brief's shorthand, P709 selected callbacks only;
its report explicitly selected no periodic rows.

### Light-gate verification

Whole-solution build:
`NUGET_HTTP_CACHE_PATH=/tmp/p734b-nuget-http dotnet build AllegianceCore.slnx --nologo -m:1 -v:q`,
**0 warnings / 0 errors** ([build.log](P734b_assets/build.log)). The separate
local showcase driver also builds 0/0 ([log](P734b_assets/showcase-build-mixed-final.log)).

**Focused checks: 208 methods × three exact-method processes = 624/624
passing invocations, 978 data-case passes, zero failures/skips.** The deduplicated
[cohort](P734b_assets/cohort.json) includes P709's full 79-method cohort,
P708's full 79-method cohort, the four P707 parking methods, all eight P699c
station-corpus methods, all 37 client sound-event methods and the new combined
chat regression. Each theory filter executes all its data cases. Both new regression theory
cases ran on each repeat.
The [ledger](P734b_assets/verification.json) retains exact assembly/filter
commands, raw counters, durations, outputs, and compressed TRX/log evidence
for each invocation.

Tests use the existing `local.env` corpus configuration without exposing its
contents. `ALLEGIANCE_LIVE_P542_PROOF=1` and
`ALLEGIANCE_LIVE_P660_SHOWCASE=1` enable the local opt-in proofs. Each selected
method runs alone through `dotnet vstest` against the built assembly. Focused checks and parking runs were serial within this lane. Suite projects
also run serially; independent showcase attempts overlapped the sweep. No assertion, deadline,
quarantine or equipment/readiness behavior was relaxed. The [verifier](P734b_assets/verify.py) refuses a second full sweep. The initial
[serial controller](P734b_assets/run-gates.py) stopped after cancellation of
the incomplete first showcase, before starting any suite. The final sweep
was launched once through the verifier, followed by any required isolated
failed-method triplets.

**Never-parks gate: 5/5 passed, zero failures/skips.** The original
five-second assertion and battle checks were unchanged. Maximum observed
low-speed-with-target interval was **2.014 seconds**. All five battles recorded
real shots/hits and zero simulation-stage failures; **68 kills** total. No
base-rate comparison was triggered because none failed.

| Run | Reached | Longest low-speed with target | Shots | Hits | Kills |
| --- | ---: | ---: | ---: | ---: | ---: |
| parking-01 | 25/30 | 1.804 s | 3624 | 1206 | 13 |
| parking-02 | 26/30 | 2.014 s | 3604 | 1497 | 13 |
| parking-03 | 26/30 | 1.912 s | 4031 | 1576 | 12 |
| parking-04 | 26/30 | 1.904 s | 3840 | 1552 | 16 |
| parking-05 | 24/30 | 1.612 s | 4382 | 1558 | 14 |

Raw receipts: [parking-metrics.json](P734b_assets/parking-metrics.json);
individual `parking-01` through `parking-05` TRX/logs are linked from the ledger.

### Local two-match showcase

**Passed: two natural Stage Over events, automatic rotation, zero simulation-stage failures.**
P733 is absent from the P734/master input ([availability](P734b_assets/finale-availability.json)),
so the brief's **unlocked** fallback was used; research-mode finale behavior
is not claimed. Civs are **25 vs 3**. The final scene uses the existing native
**Brawl** map (one shared sector, `Igc/mapmakerigc.cpp:1707-1734`) and **45 vs 45**
legal mixed-fleet clients: per side, 21 fighters, six interceptors, six scouts,
and 12 bombers. The existing explicit-fleet API resolves loaded-core role
representatives and buys ordinary legal loadouts. There are 91 actual peers
including the observer; admission is configured to 128 per IP.

The existing finale settings use both sides, one-minute minimum age, 50%
relaunch share on each side, five recent kills, and escort share 1/cap 12.
These are local scene settings, not P733 implementation changes. The server
uses normal base-elimination rules. No poses, damage, kill counters, mission
stages or game-over calls are supplied by the final driver.

| Match | Stage Over wall time | Simulation clock | Actual server outcome |
| --- | ---: | ---: | --- |
| 1 | 198.333 s | 274.050 s | Alpha won by outlasting all other sides. |
| 2 | 245.232 s | 358.650 s | Bravo won by outlasting all other sides. |

The server recorded station **1 destroyed by ship 8184**, then side 1 losing
its last restart station and Alpha winning; in match two it recorded station
**0 destroyed by ship 8264**, side 0 losing its last restart station and Bravo
winning. These are ordinary station-destruction and side-deactivation paths,
with the native outlasting/deactivation counterpart at
`FedSrv/fsmission.cpp:5318-5420`. The second mission was sampled Started
three times with **all 90 bots active and world-ready**, before ending. A
third fresh NotStarted mission was observed after automatic rotation.

Evidence: [driver](P734b_assets/showcase/Program.cs),
[timeline](P734b_assets/showcase.json),
[station/stage receipt](P734b_assets/showcase-receipt.txt), and
[console](P734b_assets/showcase-run.log.gz). The helper process exited 0.
The MCP cumulative combat summary retained its first-round values after
rotation, so it is not presented as a second-round kill count. Fresh missile,
station-destruction and stage receipts, plus the base-health samples, establish
second-round combat and completion.

Earlier incomplete scenes are retained separately; none substitutes for the
completed two-match proof above. All had zero observed simulation-stage
failures but no Stage Over before controlled cancellation:

| Diagnostic | Configuration | Last sampled wall time | Stage Over |
| --- | --- | ---: | ---: |
| [Attempt 1](P734b_assets/showcase-attempt-1.json.gz) | SingleRing, 45/side, finale at 5 min | 933.7 s | 0 |
| [Attempt 2](P734b_assets/showcase-attempt-2.json.gz) | SingleRing, 45/side, finale off | 271.2 s | 0 |
| [Attempt 3](P734b_assets/showcase-attempt-3.json.gz) | SingleRing, 45/side, ordinary 100-kill-goal diagnostic | 511.9 s | 0 |
| [Attempt 4](P734b_assets/showcase-attempt-4.json.gz) | Brawl, 45/side, finale off | 241.0 s | 0 |
| [Attempt 5](P734b_assets/showcase-attempt-5.json.gz) | Brawl, 45/side, finale at 1 min | 286.1 s | 0 |
| [Attempt 6](P734b_assets/showcase-attempt-6.json.gz) | Brawl, 90/side, finale at 1 min | 331.7 s | 0 |

The kill-goal diagnostic did not produce a win: source review found that
owned-ship eject handling returns before the side-kill accounting in
`Server.Combat.cs:1351-1357,1456-1463`. The final driver removes that goal;
this separate existing scoring issue was not changed or credited. The
90-per-side Brawl scene advanced simulation slowly under load; the final
explicit fleet retained a full 12-bomber wing at 45 ships per side without
changing weapons, damage, flight or speed implementation.

Attempt 1 was stopped by making its task-owned output path a directory,
which unwound through the driver's normal disposal/finally path; subsequent
attempts use the explicit task-owned `.cancel` file. All stopped servers
logged clean shutdowns. A nullable-struct error in an exploratory helper
build (`showcase-build-mixed.log`) was corrected before the final 0/0 helper
build; a mistakenly launched previous helper binary was stopped and its
`showcase-stale-build.log` is retained. It is excluded from certification.
No additional full test-suite sweep was started by these scene attempts.

### Single full-suite sweep and failure isolation

**Exactly one completed unfiltered sweep**, including all Core, ClientCore,
MonoGame and Lobby tests. Claude's mini runner owns the second sweep.

| Project | Passed | Failed | Skipped | Wall seconds | Evidence |
| --- | ---: | ---: | ---: | ---: | --- |
| Core | 4812 | 1 | 0 | 975.918 | [TRX](P734b_assets/suite-Core.trx.gz) |
| ClientCore | 1190 | 0 | 0 | 2107.481 | [TRX](P734b_assets/suite-ClientCore.trx.gz) |
| ClientCore.MonoGame | 2263 | 0 | 3 | 958.094 | [TRX](P734b_assets/suite-ClientCore.MonoGame.trx.gz) |
| Lobby | 19 | 0 | 0 | 0.810 | [TRX](P734b_assets/suite-Lobby.trx.gz) |

Raw totals: **8,284 passed / 1 failed / 3 existing optional skips**.
The only skips were the unavailable private Meshy-pack tests:
`Pack_built_content_directory_exists`,
`Pack_catalogue_has_the_full_meshy_set`, and
`Pack_catalogue_merge_yields_identical_resolution_to_before`.
Skipped names are counted from individual TRX results, not the runner's
aggregate `notExecuted` field. No required new/affected or local proof was
skipped. The completed MonoGame run includes the existing ten-minute flight
soak: `P695HumanShipUpdatesLiveTests.Human_flies_for_ten_minutes_with_turns_boost_tidi_and_recovers_from_correction`
passed in **605.257 seconds**. Its individual result and duration remain in
the TRX and final summary.

After all four projects finished, each failed method passed **three fresh
exact-method isolated invocations**, without changed assertions, deadlines,
transport code or quarantine settings:

| Full-suite method | Raw observation | Isolated evidence |
| --- | --- | --- |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 pass: [1](P734b_assets/suite-retry-000-1.trx.gz), [2](P734b_assets/suite-retry-000-2.trx.gz), [3](P734b_assets/suite-retry-000-3.trx.gz) |

These observations satisfy the brief's load/timing-flake rule and are recorded
in [TEST_FLAKES.md](../TEST_FLAKES.md). The isolated passes do not establish an
underlying cause or a fix. No second full-suite sweep was run.

### Final integrity and disposition

[summary.json](P734b_assets/summary.json) verifies all 208 exact triplets,
five clean parking runs, two natural Stage Over events, a single complete
unfiltered sweep, all required failure triplets, only existing optional skips,
and unchanged source/native fingerprints. The matrix SHA-256 still matches
its row audit and contains zero CR bytes. The final helper's
[source hashes](P734b_assets/showcase-source-manifest.json) match the completed
proof and its process exited 0. No conflict markers remain in either resolved
file. The summary's `clean` field is **true**.

The read-only [index audit](P734b_assets/index-preservation.json) finds
**18,736 unchanged indexed file contents**, exactly the three intended
nonconflict working-tree modifications (callback consumer, merge regression,
and P709 report), no missing files, and the same two unmerged index entries.
The [before](P734b_assets/merge-metadata-before.json) and
[after](P734b_assets/merge-metadata-after.json) snapshots prove HEAD, branch ref,
index bytes, MERGE_HEAD and ORIG_HEAD are unchanged. The dispatcher must stage
the resolved conflict contents; no staging or merge completion was performed.

**Disposition: merge contents resolved and the required light gate complete.**
P709 callback intent, P708 simulation rules and later master work are retained.
P733 finale/speed and HD-1/HD-2 implementation are untouched. All changes and
evidence remain in the working tree. No commits, pushes, deployments,
rig/221 or 200v200 runs were performed.
