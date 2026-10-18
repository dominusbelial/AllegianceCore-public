# P739 — simulation parity wave 13: periodic deadlines and their simulation consumers

Entry `51f0c77fceaa5ca665e96ead80a94a9056fea7db` on `codex/p739`. All work is left in the working tree; no commits or pushes. Native references below are relative to **`~/Development/Allegiance/src/`**, read from that local checkout ([native fingerprints](P739_assets/native-sha256.json)).

## Scope and result

Read P690, P691, P692, P708 and P709 before selecting the bundle. Their completed readiness, economy, equipment, ship AI and client callback rows are not redone. Started with the periodic area: **12 open rows / weight 35**, credited **12/47 (25.53%)** at entry.

Selected **ten open rows / weight 30** sharing outer-tick deadlines, lifecycle gates, or the corresponding simulation and wire consumers: six periodic, two updates, one constant and one callback. **Eight rows / weight 24** are completed; two broader rows gain tested native behavior but retain **partial** status and earn no credit. Periodic rises to **51.06%** and overall simulation parity **76.50% → 78.35%**.

No sound or interface matrix rows change. `Sector.Update` structure, broadcast scheduling/interest policy, pods/docking, tech mode/research commander and textures are unchanged. `Server.Broadcast.cs` adds one outer-tick periodic-rule call, not a transport or broadcast-policy change. `Sector.ResolveWarpBombTriggers` captures the already-scheduled deadline for its wire consumer; its scheduler and sector update order are unchanged.

## Rules, implementation and evidence

| Row | Weight | Before → after | Native rule | Production code and executable proof |
| --- | ---: | --- | --- | --- |
| SIM-0133 | 4 | missing → implemented | `FedSrv/fsmission.cpp:2335` | `src/Allegiance.Core/Model/Mission.Countdown.cs:21; src/Allegiance.Server/Server.Periodic.cs:22`; `Countdown_obeys_native_victory_precedence (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:93)` |
| SIM-0137 | 1 | partial → implemented | `FedSrv/fsmission.cpp:5606` | `src/Allegiance.Server/LobbyHeartbeat.cs:108`; `Dirty_lobby_details_coalesce_at_ten_seconds_and_clean_lease_refreshes (tests/Allegiance.Core.Tests/P739LobbyPeriodicTests.cs:30)` |
| SIM-0138 | 3 | partial → implemented | `FedSrv/fsmission.cpp:5817` | `src/Allegiance.Core/Model/PendingBallot.cs:148; src/Allegiance.Server/Server.Lobby.cs:1633`; `Ballot_strict_expiry_all_votes_and_cancellation (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:40)` |
| SIM-0142 | 4 | partial → partial | `FedSrv/FedSrv.CPP:8178` | `src/Allegiance.Server/Server.Lobby.cs:1224; src/Allegiance.Server/Server.Periodic.cs:14`; `Restart_briefing_preserves_absolute_deadline_or_cancels_empty_match (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:205)` |
| SIM-0143 | 4 | partial → implemented | `FedSrv/FedSrv.CPP:8237` | `src/Allegiance.Server/Server.Lobby.cs:1200`; `Start_countdown_requires_strict_expiry_and_players_or_arena_policy (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:189)` |
| SIM-0145 | 2 | partial → partial | `FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468` | `src/Allegiance.Server/Server.Periodic.cs:22; src/Allegiance.Server/Server.Dispatch.cs:2142`; `Chat_recovery_ticks_for_seated_players_and_stops_at_mission_over (tests/Allegiance.Core.Tests/P739TimerLiveTests.cs:99)` |
| SIM-0033 | 4 | partial → implemented | `Igc/igc.h:98; FedSrv/FedSrv.CPP:12551` | `src/Allegiance.Core/Model/Sector.cs:3763; src/Allegiance.Server/Server.Combat.cs:778`; `Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission (tests/Allegiance.Core.Tests/P739TimerLiveTests.cs:45)` |
| SIM-0207 | 3 | partial → implemented | `Igc/igc.h:4861 ; Igc/missileigc.cpp:466` | `src/Allegiance.Server/ServerHost.cs:747; src/Allegiance.Server/Server.Combat.cs:774`; `Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission (tests/Allegiance.Core.Tests/P739TimerLiveTests.cs:45)` |
| SIM-0070 | 3 | partial → implemented | `Igc/igc.h:5081` | `src/Allegiance.Core/Sim/DamageTrackClock.cs:10; src/Allegiance.Core/Model/Mission.cs:660; src/Allegiance.Core/Sim/DamageTrack.cs:86`; `Ships_damaged_at_different_times_share_mission_slot_phase (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:155)` |
| SIM-0074 | 2 | partial → implemented | `Igc/mineigc.cpp:150` | `src/Allegiance.Core/Sim/Mine.cs:295`; `Mine_survives_exact_expiry_and_is_removed_after_it (tests/Allegiance.Core.Tests/P739PeriodicTests.cs:176)` |

**Match timeout.** `FedSrv/fsmission.cpp:2339-2504` checks a strict elapsed-time boundary and considers conquest flag-capable station count, territory count, prosperity bucket **invested time**, artifacts, flags, then kills and fewer deaths. Every tied criterion restarts the comparison across **all sides**. Money does not break the final tie, despite the stale native comment. `Mission.EvaluateCountdown` follows that literal order and returns an unresolved draw; the host routes the result through the existing score finalization, GAME_OVER and rotation path. `GameConfig.GameLengthSeconds` enables the rule; absent/nonpositive duration disables it. The original absolute briefing deadline anchors match time. Tests distinguish each competing criterion, a three-side tie followed by a different winner, exact expiry, deaths, final draw and one real rotation. A strict shared timeout guard suppresses due payday and treasure accrual after expiry, while allowing the exact-boundary payday (`FedSrv/fsmission.cpp:2508-2516`); the host still selects the winner after movement.

**Start and restart.** `FedSrv/FedSrv.CPP:8182-8243` begins a native restart briefing when at most 15 seconds remain, using the **remaining** interval rather than adding a new 15 seconds at expiry. Insufficient teams get one warning at 30 seconds, are force-readied during the briefing window and are canceled only when time runs out. Minimum connected players and `AllowEmptyTeams` are explicit game settings; arena policy defaults true to preserve this dedicated server's existing empty-team behavior. Countdown completion requires strict `now > deadline` and either a connected player or arena permission (`FedSrv/fsmission.cpp:1422-1432`). The separately configured P660 `AutoNextMatch` intermission retains its existing policy. **SIM-0142 stays partial:** native maximum-player, imbalance, rank and active-side readiness constraints (`FedSrv/fsmission.cpp:4375-4440`), initial native restart arming and an explicit native AutoStart mode remain absent. No claim is made that those policies are implemented. Existing countdown/rotation tests certify compatibility as well as the new boundary tests.

**Ballots.** `FedSrv/fsmission.cpp:5817-5834` requires `expiration < now`, unless all votes have arrived. Cancellation removes without tallying; the existing production resolver announces/applies a result once and removes the ballot. The new boundary test and existing real TCP mutiny/resign/draw/scoping cases exercise those paths.

**Lobby details.** The native ten-second dirty interval (`FedSrv/fsmission.cpp:21,5606-5627`) coalesces changed player counts and sends the latest value. The HTTP directory still receives its clean 25-second lease refresh, and rejected ownership tokens retry fresh after the minimum interval. DirectPlay cookies and Windows session details are protocol-specific; the port sends the existing named, source-generated registration details and ownership token instead. No reflection serialization is added. Tests use a controllable clock at 9.999/10 and 24.999/25 seconds, plus the existing real in-process directory integration. Manual internal `PumpAsync()` remains an explicitly forced diagnostic/test cycle; the production timer uses `periodic: true`. Responses are disposed, and an in-flight async pump can release its managed semaphore after shutdown.

**Chat recovery.** A seated player's eligible outer tick replenishes budget in NotStarted, Starting and Started, capped at its configured maximum; Over does not replenish (`FedSrv/FedSrv.CPP:8279-8287`). This replaces lazy elapsed-tick credit, which could accrue through ineligible intervals. **SIM-0145 stays partial:** P654's dedicated-server 1000/20/+5 defaults, debt and group-noecho charging differ from native 100/20/+1 and `FedSrv/fsship.h:449-468`. The transport policy is preserved and explicitly receives no parity credit. A live connected player exercises each stage and cap; the existing flood-bound regression remains in the focused cohort.

**Damage credit.** The native shared phase advances only **one** of eight five-second slots per outer mission update, even after a long stall (`Igc/igc.h:5070-5095`; `Igc/missionigc.cpp:3061,3146`). Tracks share that phase, seeded at assembly/rotation time, and lazily synchronize before use without a registry retaining ships. This preserves the native once-before-sector-chunks rule. Totals below 0.5 are deleted and rank sorting waits until new damage (`Igc/common.cpp:2571-2591`). Tests use two real ships first damaged at different times, delayed outer ticks, repeat/stage guards, equality, rotation epoch and the deferred sort rule; existing real kill-credit cases remain included.

**Mine expiry.** `Igc/mineigc.cpp:150-156` keeps a mine alive at exact expiry and removes it only after that timestamp. The direct before/equal/after test is accompanied by existing lifespan, TCP destruction and cross-side visibility cases.

**Warp bombs.** `FedSrv/FedSrv.CPP:12543-12562` reads float slot 32 and sends native uint **milliseconds**, mission-wide across real sides. The simulation already used that loaded delay, but the host used a fixed 15 seconds in tick-index units and sent across unrelated missions. The collision chunk now captures one absolute detonation deadline; both blast scheduling and the wire notice use it, surviving later core edits and time dilation. Native's callback-owned scheduling remains split between the existing sector trigger and host adapter. A real TCP three-peer/two-mission theory tests loaded delays of 5/7/10 seconds, exact wire deadline, warp/type fields, cross-side receipt, once-only notice and exclusion of the other mission. Existing paired destination, dual-source and deadline-order simulation tests stay included.

No selected row is wholly Windows/DirectX/Zone-only or dependent on unavailable assets, so **no new n/a rows** are manufactured. The protocol-specific lobby suboperations are documented above. Existing out-of-scope periodic broadcast rows and the intentionally divergent station-update bug remain open; they are not silently converted into parity credit.

## Scorecard before → after

Both matrices validate: **438 interface rows**, **473 simulation rows**. Partial earns zero credit. Only the ten selected records change, preserving all native fields, weights and identities; CSV line endings are LF. See [matrix audit](P739_assets/matrix-audit.json), [before](P739_assets/scorecard-before.txt), [after](P739_assets/scorecard-after.txt).

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 53.03% (105/198) → 54.55% (108/198) | 35/66 → 36/66 |
| constants | 95.56% (129/135) → 98.52% (133/135) | 37/39 → 38/39 |
| periodic | 25.53% (12/47) → 51.06% (24/47) | 3/15 → 7/15 |
| updates | 79.08% (276/349) → 80.52% (281/349) | 67/87 → 69/87 |
| sounds | 82.47% (461/559) → unchanged | 171/216 → unchanged |
| warnings | 100.00% (10/10) → unchanged | 2/2 → unchanged |
| TOTAL | 76.50% (993/1298) → **78.35% (1017/1298)** | 315/425 → **323/425** |

Interface TOTAL remains **95.23% (1399/1469)** weighted, **382/406** complete. The full four-area entry inventory below records every open row by weight; the selected ten rows indicate their final status.

## Verification

Final whole-solution build: `NUGET_HTTP_CACHE_PATH=/tmp/p739/nuget-http dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false`, **0 warnings / 0 errors** ([log](P739_assets/build.log)). The writable NuGet cache preserves package/audit checks. Shared compilation is disabled because the sandbox's compiler-server mount namespace could not write a newly created Release output directory during an earlier development build; no product or compiler warning is suppressed.

Native AOT publish: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -p:UseSharedCompilation=false -o /tmp/p739/aot` (same writable cache), **succeeded, no IL2026/IL3050** ([log](P739_assets/publish.log)). The resulting native executable is about 14 MB; its SHA-256 and unchanged source fingerprints are in the [integrity audit](P739_assets/integrity-audit.json) and [source manifest](P739_assets/source-sha256.json). All new configuration remains on the source-generated JSON path. No reflection serialization is introduced.

**Final focused verification:** **79 methods × 3 prescribed individual invocations = 237 invocations / 300 case executions**, 299 passed, 1 raw failure, no skips. This includes **16 new methods / 25 cases**, all **75/75 case executions passed**. The remaining affected cohort covers real ballots, start/restart/rotation, native blast scheduling, kill credit, mine lifespan/visibility/destruction, treasury sale, bot purchase/death/pod/relaunch, local showcase rotation and source-generated configuration binding. Exact method filters, commands, timestamps, counts, errors and raw artifacts are in the [cohort](P739_assets/cohort.json), [qualification](P739_assets/focused-qualification.json) and [invocation ledger](P739_assets/verification.jsonl).

The final focused raw failure was `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`: empty collection at `Assert.Single`, line 414. Its next two prescribed runs and **three fresh isolated retries all passed**, satisfying the brief's load-flake rule. The same symptom is recorded by P709. No assertion, deadline, quarantine or product code was changed in response. See `certified-focus-039-1` and `certified-retry-039-1` through `-3`.

**Mandatory parking comparison.** Untouched entry Debug binaries were built before source changes and used for **five isolated base runs**, all passed. Their binary fingerprint is [base-binaries.json](P739_assets/base-binaries.json). No Debug rebuild occurred during those base runs. The final source cohort was rebuilt and certified separately. The prescribed final **five isolated parking runs** use the same unchanged test; its additional unfiltered assembly execution is reported separately in the suite evidence.

| Build | Run | Outcome | Longest stopped with target (s) | Kills |
| --- | ---: | --- | ---: | ---: |
| entry base | 1 | pass | 1.905 | 13 |
| entry base | 2 | pass | 1.504 | 12 |
| entry base | 3 | pass | 2.005 | 15 |
| entry base | 4 | pass | 1.913 | 13 |
| entry base | 5 | pass | 2.005 | 16 |
| final | 1 | pass | 1.913 | 13 |
| final | 2 | pass | 2.008 | 16 |
| final | 3 | pass | 2.004 | 12 |
| final | 4 | pass | 1.507 | 13 |
| final | 5 | pass | 2.017 | 15 |

Base pass rate **5/5**; final prescribed pass rate **5/5**. Full receipts and failures are in [parking-summary.json](P739_assets/parking-summary.json).

**Local showcase battle.** The explicit 15v15, 90-second battle boots its own ephemeral loopback server (`Port=0`); it does not connect to the rig. Its positive-kill assertion passed. [Receipt](P739_assets/battle-receipt.txt), [detail](P739_assets/battle-details.txt):

```text
RESULT_P632 p739 model=port debug=1 size=15/side window=90s botsReached=30/30 killsA=2 killsB=0 killsTotal=2 deathsA=0 deathsB=2 shots=14911 hits=5972 hitRate=40.1 % friendly=0 shieldHits=3789 hullHits=2183 maxTargeted=28 maxVisibleEnemies=14
```

Bot purchase → fight → death/pod return or pod destruction → repurchase/relaunch → damage again passed **3/3 isolated invocations**, both lifecycle cases each time. Mining sale/payday passed **3/3 alone**. The controlled production-bot TCP missile battle also passed **3/3**, each asserting a credited kill. Local P660 15v15 bots/humans rotation was explicitly enabled and passed its three focused invocations. No external rig/221 or 200v200 job was invoked; legacy opt-in remote/rig probes keep their default gates.

**One full sweep only**, after final focused verification. Core, ClientCore and MonoGame ran once each, serially, with `local.env` loaded and `ALLEGIANCE_LIVE_P660_SHOWCASE=1`. No second full sweep was run.

| Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4878 | 1 | 0 | 967.365 |
| Allegiance.ClientCore.Tests | 1305 | 1 | 0 | 2679.564 |
| Allegiance.ClientCore.MonoGame.Tests | 2431 | 0 | 3 | 977.463 |

After all three suites completed, each failing case received three fresh isolated observations. Raw full-pass failures remain above and in the ledger; isolated qualification does not establish an underlying fix.

| Method | Raw symptom | Fresh isolated outcome |
| --- | --- | --- |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 passed; load-flake criterion met |
| `Allegiance.ClientCore.Tests.P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches` | both sides real development and newly legal upgraded launch timed out; sim=11463.199999973434 | 3/3 passed; load-flake criterion met |

The [case-level qualification](P739_assets/full-suite-qualification.json) verifies the exact originally failing case names and all three passing outcomes.

The P705 qualification targets the original `civA: 25, civB: 3, size: 10` failure: the first fresh invocation ran all four civilization cases, followed by two fresh exact-case invocations using `FullyQualifiedName=...&DisplayName~civA: 25,`. Each exact-case invocation was checked to execute one case. Thus the original failure received three fresh observations, with its assertions and deadlines unchanged.

Skipped counts come from individual TRX outcomes, not the runner aggregate. Existing unavailable private Meshy-pack prerequisites account for these skips: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before`. No new skip was added.

The earlier 71-method exploratory cohort, before adding timeout/payday suppression, is preserved separately in the ledger (`focus-*` labels). Its one mine-visibility EXPORT receipt failure passed three fresh retries; it is not counted as final source certification. Both initial and final focused flakes, plus any full-suite qualification, are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). The initial 22-case development pass and early compile attempts are separate from the final gate above. Final build/source hashes, CSV validation and `git diff --check` were checked after all work. No commits or pushes were made.


## Entry open inventory: callbacks, updates, periodic, constants

All missing/partial rows at entry, descending weight and then area/id. These are the audited entry statuses, with a final-status column only for the selected bundle. The machine-readable full records, including remaining-gap notes, are in [open-before.json](P739_assets/open-before.json).

| Weight | Row | Area | Entry status | Selected final | Native item | Native citation |
| ---: | --- | --- | --- | --- | --- | --- |
| 5 | SIM-0063 | updates | partial | — | swept_collision_time_order | `Igc/clusterIGC.cpp:255` |
| 4 | SIM-0033 | constants | partial | implemented | c_fcidWarpBombDelay | `Igc/igc.h:98; FedSrv/FedSrv.CPP:12551` |
| 4 | SIM-0133 | periodic | missing | implemented | countdown_game_winner | `FedSrv/fsmission.cpp:2335` |
| 4 | SIM-0142 | periodic | partial | partial | auto_start_restart_ready_rules | `FedSrv/FedSrv.CPP:8178` |
| 4 | SIM-0143 | periodic | partial | implemented | start_countdown_expiry | `FedSrv/FedSrv.CPP:8237` |
| 4 | SIM-0044 | updates | partial | — | ore_seen_by_side | `Igc/asteroidigc.h:58` |
| 4 | SIM-0045 | updates | partial | — | asteroid_build_completion | `Igc/asteroidigc.h:90` |
| 4 | SIM-0048 | updates | partial | — | build_effect_phase_animation | `Igc/buildingeffectigc.h:160` |
| 4 | SIM-0059 | updates | partial | — | docked_npc_launch_rules | `Igc/clusterIGC.cpp:116` |
| 4 | SIM-0060 | updates | missing | — | station_consumed_rock_fog | `Igc/clusterIGC.cpp:141` |
| 4 | SIM-0062 | updates | partial | — | global_preplot_plot_execute_order | `Igc/clusterIGC.cpp:170` |
| 4 | SIM-0064 | updates | partial | — | mine_attrition | `Igc/clusterIGC.cpp:325` |
| 4 | SIM-0065 | updates | partial | — | visibility_every_tenth_pass | `Igc/clusterIGC.cpp:348` |
| 4 | SIM-0101 | updates | partial | — | npc_plan_legality | `Igc/shipIGC.cpp:1938` |
| 4 | SIM-0107 | updates | partial | — | miner_capacity_and_depletion | `Igc/shipIGC.cpp:2208` |
| 4 | SIM-0109 | updates | partial | — | constructor_drill_alignment | `Igc/shipIGC.cpp:2272` |
| 4 | SIM-0110 | updates | partial | — | idle_npc_complaint_backoff | `Igc/shipIGC.cpp:2308` |
| 4 | SIM-0119 | updates | partial | — | goto_completion_and_layer_lay | `Igc/shipIGC.cpp:2802` |
| 3 | SIM-0168 | callbacks | partial | — | ClusterUpdateEvent@4781 | `Igc/igc.h:4781 ; Igc/clusterIGC.cpp:357` |
| 3 | SIM-0169 | callbacks | partial | — | BuildStation@4783 | `Igc/igc.h:4783 ; Igc/buildingeffectigc.h:348` |
| 3 | SIM-0172 | callbacks | partial | — | ReceiveChat@4792 | `Igc/igc.h:4792` |
| 3 | SIM-0173 | callbacks | partial | — | SendChat@4801 | `Igc/igc.h:4801 ; Igc/shipIGC.cpp:1895; Igc/shipIGC.cpp:1898; Igc/shipIGC.cpp:1901; Igc/shipIGC.cpp:2171; Igc/shipIGC.h:1732` |
| 3 | SIM-0176 | callbacks | partial | — | PlaySoundEffect@4830 | `Igc/igc.h:4830 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193` |
| 3 | SIM-0177 | callbacks | partial | — | PlaySoundEffect@4831 | `Igc/igc.h:4831 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193` |
| 3 | SIM-0178 | callbacks | partial | — | PlayNotificationSound@4832 | `Igc/igc.h:4832 ; Igc/afterburnerIGC.cpp:119; Igc/cloakigc.cpp:75; Igc/dispenserigc.cpp:98; Igc/magazineigc.cpp:96; Igc/shieldIGC.cpp:82; Igc/shieldIGC.cpp:186; Igc/shipIGC.cpp:1608; Igc/shipIGC.h:616; Igc/shipIGC.h:617; Igc/shipIGC.h:646; Igc/shipIGC.h:647; Igc/shipIGC.h:1302; Igc/shipIGC.h:1307; Igc/weaponIGC.h:46` |
| 3 | SIM-0180 | callbacks | partial | — | PlayVisualEffect@4834 | `Igc/igc.h:4834 ; Igc/shipIGC.cpp:836; Igc/shipIGC.cpp:1006; Igc/shipIGC.cpp:1191; Igc/shipIGC.cpp:1195` |
| 3 | SIM-0182 | callbacks | partial | — | TerminateModelEvent@4836 | `Igc/igc.h:4836 ; Igc/modelIGC.cpp:61` |
| 3 | SIM-0183 | callbacks | partial | — | TerminateMissionEvent@4837 | `Igc/igc.h:4837 ; Igc/missionigc.cpp:3082` |
| 3 | SIM-0185 | callbacks | missing | — | KillAsteroidEvent@4839 | `Igc/igc.h:4839 ; Igc/asteroidigc.h:162; Igc/clusterIGC.cpp:155` |
| 3 | SIM-0186 | callbacks | partial | — | DrainAsteroidEvent@4840 | `Igc/igc.h:4840` |
| 3 | SIM-0190 | callbacks | partial | — | KillBuildingEffectEvent@4844 | `Igc/igc.h:4844 ; Igc/shipIGC.cpp:218` |
| 3 | SIM-0195 | callbacks | partial | — | PostNotificationText@4849 | `Igc/igc.h:4849 ; Igc/afterburnerIGC.cpp:120; Igc/cloakigc.cpp:76; Igc/dispenserigc.cpp:99; Igc/magazineigc.cpp:97; Igc/shieldIGC.cpp:83; Igc/shipIGC.h:612; Igc/shipIGC.h:618; Igc/shipIGC.h:642; Igc/shipIGC.h:648; Igc/weaponIGC.h:47` |
| 3 | SIM-0197 | callbacks | partial | — | TargetKilled@4851 | `Igc/igc.h:4851` |
| 3 | SIM-0199 | callbacks | partial | — | DamageShipEvent@4853 | `Igc/igc.h:4853 ; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312` |
| 3 | SIM-0205 | callbacks | partial | — | LandOnCarrierEvent@4859 | `Igc/igc.h:4859 ; Igc/shipIGC.cpp:1050; Igc/shipIGC.cpp:1057` |
| 3 | SIM-0206 | callbacks | partial | — | RescueShipEvent@4860 | `Igc/igc.h:4860 ; Igc/shipIGC.cpp:865; Igc/shipIGC.cpp:907; Igc/shipIGC.cpp:1035; Igc/shipIGC.cpp:1040` |
| 3 | SIM-0207 | callbacks | partial | implemented | WarpBombEvent@4861 | `Igc/igc.h:4861 ; Igc/missileigc.cpp:466` |
| 3 | SIM-0208 | callbacks | partial | — | HitWarpEvent@4862 | `Igc/igc.h:4862 ; Igc/shipIGC.cpp:636` |
| 3 | SIM-0209 | callbacks | partial | — | HitTreasureEvent@4863 | `Igc/igc.h:4863 ; Igc/treasureIGC.h:92` |
| 3 | SIM-0212 | callbacks | partial | — | ContinueRipcord@4866 | `Igc/igc.h:4866 ; Igc/shipIGC.cpp:280` |
| 3 | SIM-0213 | callbacks | partial | — | UseRipcord@4867 | `Igc/igc.h:4867 ; Igc/shipIGC.cpp:286` |
| 3 | SIM-0214 | callbacks | partial | — | FireMissile@4868 | `Igc/igc.h:4868 ; Igc/magazineigc.cpp:118` |
| 3 | SIM-0219 | callbacks | partial | — | ChangeStation@4876 | `Igc/igc.h:4876 ; Igc/shipIGC.h:483` |
| 3 | SIM-0220 | callbacks | partial | — | ChangeCluster@4879 | `Igc/igc.h:4879 ; Igc/shipIGC.h:254` |
| 3 | SIM-0221 | callbacks | partial | — | CommandChangedEvent@4882 | `Igc/igc.h:4882 ; Igc/shipIGC.h:788` |
| 3 | SIM-0229 | callbacks | partial | — | CreateBuildingEffect@4901 | `Igc/igc.h:4901 ; Igc/shipIGC.cpp:884` |
| 3 | SIM-0231 | callbacks | partial | — | GetCluster@4909 | `Igc/igc.h:4909 ; Igc/asteroidigc.cpp:29; Igc/buildingeffectigc.h:84; Igc/buoyigc.cpp:60; Igc/clusterIGC.cpp:312; Igc/clusterIGC.cpp:313; Igc/clusterIGC.cpp:416; Igc/clusterIGC.cpp:444; Igc/clusterIGC.h:418; Igc/clusterIGC.h:436; Igc/common.cpp:481; Igc/common.cpp:687; Igc/common.cpp:726; Igc/common.cpp:772; Igc/common.cpp:775; Igc/common.cpp:785; Igc/common.cpp:788; Igc/common.cpp:871; Igc/common.cpp:901; Igc/common.cpp:923; Igc/common.cpp:972; Igc/common.cpp:989; Igc/common.cpp:1021; Igc/common.cpp:1034; Igc/common.cpp:1699; Igc/common.cpp:1841; Igc/common.cpp:1854; Igc/common.cpp:1873; Igc/common.cpp:1925; Igc/common.cpp:2244; Igc/common.cpp:2332; Igc/common.cpp:2333; Igc/common.cpp:2753; Igc/common.cpp:2782; Igc/igc.h:4912; Igc/igc.h:4919; Igc/igc.h:5333; Igc/igc.h:5334; Igc/magazineigc.cpp:127; Igc/mapmakerigc.cpp:239; Igc/mapmakerigc.cpp:263; Igc/mapmakerigc.cpp:264; Igc/mapmakerigc.cpp:279; Igc/mapmakerigc.cpp:335; Igc/mapmakerigc.cpp:816; Igc/mapmakerigc.cpp:822; Igc/mapmakerigc.cpp:828; Igc/mapmakerigc.cpp:834; Igc/mapmakerigc.cpp:989; Igc/mapmakerigc.cpp:1039; Igc/mineigc.cpp:62; Igc/missileigc.cpp:108; Igc/missileigc.cpp:371; Igc/missionigc.cpp:3661; Igc/modelIGC.h:589; Igc/probeigc.cpp:66; Igc/probeigc.cpp:392; Igc/probeigc.h:169; Igc/projectileIGC.cpp:193; Igc/shipIGC.cpp:1765; Igc/shipIGC.cpp:1922; Igc/shipIGC.cpp:1950; Igc/shipIGC.cpp:2036; Igc/shipIGC.cpp:2354; Igc/shipIGC.cpp:2376; Igc/shipIGC.cpp:2714; Igc/shipIGC.cpp:2720; Igc/shipIGC.cpp:2727; Igc/shipIGC.cpp:3785; Igc/shipIGC.cpp:3949; Igc/shipIGC.cpp:3970; Igc/shipIGC.cpp:4101; Igc/shipIGC.cpp:4156; Igc/shipIGC.cpp:4347; Igc/shipIGC.cpp:4378; Igc/shipIGC.h:349; Igc/shipIGC.h:1614; Igc/shipIGC.h:1644; Igc/shipIGC.h:1917; Igc/shipIGC.h:1929; Igc/shipIGC.h:2207; Igc/shipIGC.h:2252; Igc/shipIGC.h:2284; Igc/shipIGC.h:2307; Igc/shipIGC.h:2355; Igc/shipIGC.h:2356; Igc/sideigc.cpp:342; Igc/sideigc.cpp:367; Igc/sideigc.cpp:371; Igc/sideigc.cpp:373; Igc/sideigc.cpp:375; Igc/sideigc.cpp:438; Igc/stationIGC.cpp:45; Igc/stationIGC.cpp:68; Igc/stationIGC.h:314; Igc/treasureIGC.cpp:201; Igc/warpIGC.cpp:29; Igc/warpIGC.h:80; Igc/warpIGC.h:155; Igc/weaponIGC.cpp:177; Igc/weaponIGC.cpp:235` |
| 3 | SIM-0232 | callbacks | partial | — | GetRipcordCluster@4915 | `Igc/igc.h:4915 ; Igc/common.cpp:811; Igc/common.cpp:824; Igc/shipIGC.cpp:3741; Igc/shipIGC.cpp:3760; Igc/shipIGC.cpp:4262; Igc/shipIGC.cpp:4281` |
| 3 | SIM-0233 | callbacks | partial | — | Reload@4923 | `Igc/igc.h:4923 ; Igc/shipIGC.h:610; Igc/shipIGC.h:640` |
| 3 | SIM-0138 | periodic | partial | implemented | ballot_resolution_boundary | `FedSrv/fsmission.cpp:5817` |
| 3 | SIM-0141 | periodic | partial | — | server_sim_broadcast_cadence | `FedSrv/FedSrv.CPP:4238` |
| 3 | SIM-0144 | periodic | partial | — | station_state_docked_broadcast | `FedSrv/FedSrv.CPP:8253` |
| 3 | SIM-0150 | periodic | partial | — | warp_reentry_timer | `FedSrv/fsship.cpp:1185; FedSrv/fsship.cpp:1190` |
| 3 | SIM-0470 | periodic | partial | — | ship_status_delta_and_turret_updates | `FedSrv/FedSrv.CPP:3693` |
| 3 | SIM-0471 | periodic | partial | — | target_hull_shield_fraction_updates | `FedSrv/FedSrv.CPP:4079` |
| 3 | SIM-0067 | updates | partial | — | explosion_mine_strength | `Igc/clusterIGC.cpp:443` |
| 3 | SIM-0070 | updates | partial | implemented | damage_credit_slot_decay | `Igc/igc.h:5081` |
| 3 | SIM-0089 | updates | missing | — | shield_five_percent_activation_sound | `Igc/shieldIGC.cpp:93` |
| 3 | SIM-0100 | updates | partial | — | npc_ammo_goal_recovery | `Igc/shipIGC.cpp:1715` |
| 2 | SIM-0014 | constants | partial | — | c_fcidRipcordTime | `Igc/igc.h:73; FedSrv/FedSrv.CPP:1676` |
| 2 | SIM-0145 | periodic | partial | partial | chat_budget_recovery | `FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468` |
| 2 | SIM-0147 | periodic | partial | — | network_roll_call_and_disconnect | `FedSrv/FedSrv.CPP:7942` |
| 2 | SIM-0074 | updates | partial | implemented | mine_expiry_boundary | `Igc/mineigc.cpp:150` |
| 2 | SIM-0078 | updates | partial | — | model_spin | `Igc/modelIGC.cpp:76` |
| 1 | SIM-0137 | periodic | partial | implemented | lobby_dirty_rate_limit | `FedSrv/fsmission.cpp:5606` |


Raw TRX/log evidence for this task is archived outside the repo at `~/allegiance-evidence/P739/` (Claude, 2026-10-18, evidence size rule).
