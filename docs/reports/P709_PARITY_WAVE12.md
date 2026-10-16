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
