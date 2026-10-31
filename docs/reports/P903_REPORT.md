# P903 — production reachability audit

Date: 2026-10-31. Dispatch base: `c583d7dd`. Changes left in the working tree; no commit, push or remote-host contact.

## Result

Audited exactly 27 rows / 45 weight. Nine GotoPlan rows already run by default and are now `production`; two replay-identity rows are `production` after fixing the initial-generation marker; dead native `Ballot::Cancel` is `n/a`. All fifteen text-core rows retain `test-only`, with explicit native entry-point evidence and a proposed split policy for Claude. No scorer changes or gameplay steering changes were made.

The only runtime change is `mission.BeginReplay()` at `src/Allegiance.Server/Server.Lifecycle.cs:1480`. Native `GenerateMission` marks its first generation and increments on each subsequent generation (`Igc/missionigc.cpp:3846-3857`). The port already transferred replay state during rotation (`Server.Lobby.cs:724-732`) and consumed `GetContextName` in career results (`Server.Lobby.cs:1488`), but boot never initialized the generation marker. Before this fix the first reuse remained replay 0. Boot now yields 0, followed by 1 and 2 on the first two rotations; creation time and mission ID are preserved.

`P903ProductionReachabilityTests.Boot_and_two_rotations_report_distinct_native_contexts_with_replay_counts` fails against the unchanged server at `HasGenerated == false` and passes with the fix. It boots the actual server, invokes its actual rotation and result-builder methods, captures the source-generated JSON POST through an injected HttpClient handler, and checks all three replay/context identities. It does not manually construct the result DTO or increment the replay count. This is a captured HTTP pipeline, not a network test against a live lobby.

The production context uses the `U` prefix. This matches the standalone game's existing club-game rejection (`src/Allegiance.Server/Server.MissionParams.cs:178-181`, native `FedSrv/FedSrv.CPP:7533-7537`). The existing P872 formatter test separately covers the `Z` branch; no unsupported club-game mode was introduced to exercise it.

## Default resolution, including P887

`ServerConfig.Sim` is initialized even when the JSON section is absent (`src/Allegiance.Server/ServerConfig.cs:1130`). `SimConfig.NativeGotoPlan = true` (`SimConfig.cs:10`); `ResolveNativeGotoPlan` lets the environment override JSON (`SimConfig.cs:12-17`): blank means configured value; `0`, `false`, `off` disable it, and other nonblank values enable it. `Server.Lifecycle.cs:1418-1419` assigns the resolved value on every assembled mission. `Ship.NativeGotoPlan.cs:29-31` selects it for NPCs, and `Ship.cs:4863` uses that controller. A raw model assembled in a unit test does not itself opt in; the shipping server does.

P887 did not introduce the GotoPlan default: P875 already enabled it. P887 adopted the rig baseline's other defaults (pool/native/parallel); its report explicitly lists GotoPlan among existing defaults. The stale matrix/glossary still said default-off. No flag default was changed in P903. Existing `P817NativeGotoPlanTests.Flag_defaults_on_and_environment_overrides_config` pins the default and the override semantics; the production binding is independently confirmed in the server boot logs.

**Deployment caveat (fixed in Claude's review, the Compose fallback is now `1`):** `docker-compose.yml:102` still supplied `ALLEGIANCE_SIM_NATIVE_GOTOPLAN` with a fallback of `0`. Since the environment wins, a default Compose deployment explicitly selects legacy steering even though the bare server defaults on. These rows have a real default-on production server path; this is not a claim that every deployment enables them. The Compose override is left unchanged because the brief prohibits steering/gameplay changes beyond the two replay/context wiring items. Claude should reconcile that deployment setting with the accepted P875/P887 baseline separately; no further rig A/B is claimed here.

The isolated production test logs confirm immutable snapshots and encode-once enabled, parallel build, pool transport and native ship updates, with GotoPlan enabled. Server GC is enabled for standalone tests/Fast/showcase using `DOTNET_gcServer=1`; the shipping server also has `ServerGarbageCollection=true`. No synthetic load or performance experiment was run.

## Per-row decisions

All native paths below are relative to the read-only native `src/` tree. Cited definitions/callers were re-grepped on this checkout. GotoPlan test names refer to `tests/Allegiance.Core.Tests/P817NativeGotoPlanTests.cs`; every row also shares its default/override test above. Their production port binding is `src/Allegiance.Server/SimConfig.cs:10` → `Server.Lifecycle.cs:1418` → `src/Allegiance.Core/Sim/Ship.NativeGotoPlan.cs:29` and `NativeGotoPlan.cs`.

| Row | Verdict | Native citation | Port rule / test |
|---|---|---|---|
| SIM-0475 | Reclassified: production (already default-on) | `Igc/common.cpp:1157-1559; Igc/common.cpp:1936-1983` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Reverse_drag_stopping_distance; Goto_masks_preserve_current_and_rest_position_tests` |
| SIM-0476 | Reclassified: production (already default-on) | `Igc/common.cpp:894-1011; Igc/common.cpp:1845-1933; Igc/shipIGC.h:380-389` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `FindPath_obeys_visibility_initial_distance_and_coward_final_cluster_exception; Waypoint_replans_on_ship_and_target_cluster_changes_and_enter_completion` |
| SIM-0477 | Reclassified: production (already default-on) | `Igc/common.cpp:1575-1790; Igc/common.cpp:2000-2016; Igc/common.cpp:2136-2309` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Dodge_uses_rcs_not_away_facing_and_respects_skip_and_ships_only; Mines_have_native_speed_gate_and_minefield_slowdown; Divert_uses_tangent_path_around_stationary_blockers` |
| SIM-0478 | Reclassified: production (already default-on) | `Igc/common.cpp:152-254; Igc/common.cpp:1813-1821; Igc/common.cpp:2090-2150; Igc/shipIGC.cpp:2752-2754; Igc/shipIGC.cpp:2795-2833; Igc/shipIGC.cpp:3999-4003` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Wide_angle_turn_uses_native_yaw_escape_and_directional_rcs; Boost_is_cancelled_only_inside_original_target_radius; Wingman_retreat_or_ammo_goto_boosts_only_at_full_throttle_and_carrier_entry_does_not_brake` |
| SIM-0479 | Reclassified: production (already default-on) | `Igc/common.cpp:1392-1398` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Builder_strip_pivots_then_completes_without_dodging_rock` |
| SIM-0480 | Reclassified: production (already default-on) | `Igc/igc.h:4909-4913; Igc/shipIGC.h:341-389; Igc/common.cpp:1873-1874` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Unavailable_target_waits_without_completing_or_steering; Enter_station_goto_clears_on_docking_and_moving_ship_goto_reads_live_position` |
| SIM-0481 | Reclassified: production (already default-on) | `Igc/shipIGC.cpp:3990-4151; Igc/common.cpp:1850-1862; Igc/shipIGC.h:459-478` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Explicit_warp_order_enters_at_speed_boost_uses_original_goal_and_arrival_clears_order; Enter_station_goto_clears_on_docking_and_moving_ship_goto_reads_live_position` |
| SIM-0482 | Reclassified: production (already default-on) | `Igc/igc.h:1921; Igc/buoyigc.cpp:44-48; Igc/shipIGC.cpp:2435,2730,2795-2828` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Native_layer_waits_for_visible_buoy_stop_then_lays_before_movement_legacy_keeps_reach_radius` |
| SIM-0483 | Reclassified: production (already default-on) | `Igc/common.cpp:1817-1821` | `NativeGotoPlan.cs` / `Ship.NativeGotoPlan.cs`; `Explicit_warp_order_enters_at_speed_boost_uses_original_goal_and_arrival_clears_order` |

| Row | Verdict | Native citation | Port citation / test |
|---|---|---|---|
| SIM-0750 | Wired: initialize first generation; production | `Igc/missionigc.cpp:3846-3857,4211-4214`; count is consumed by the context at `4231` and exposed by `AGC/IAGCGameImpl.h:319-322` | `src/Allegiance.Core/Model/Mission.cs:66,94`; `src/Allegiance.Server/Server.Lifecycle.cs:1480`; `Server.Lobby.cs:724-732`; P903 production test above + `P872NativeBehaviourTests.Replay_count_increments_after_first_generation_and_invalidates_context` |
| SIM-0751 | Production: existing result consumer, now correct first replay identity | `Igc/missionigc.cpp:4217-4238`; `FedSrv/fsmission.cpp:2766` persists it, `2845-2854,2937` share it with team/player results | `src/Allegiance.Core/Model/Mission.cs:118`; `src/Allegiance.Server/Server.Lobby.cs:1488`; P903 production test above + `P872NativeBehaviourTests.Context_name_is_the_cached_UZ_timestamp_mission_and_replay_string` |
| SIM-0821 | Reclassified: n/a, unused native method | `FedSrv/fsmission.h:95`; `FedSrv/fsmission.cpp:5836-5844` | `src/Allegiance.Core/Model/PendingBallot.cs:81,149`; `src/Allegiance.Server/Server.Lobby.cs:2762`; `BallotVotingLiveTests.Cancelled_ballot_is_dropped_without_announcing_or_resolving` |

Whole-tree `rg --hidden --no-ignore -n '\bCancel\s*\(' <native-src>` finds just the Ballot declaration and definition; all other hits are unrelated Steam/Wopr cancellation. A broader case-insensitive cancel search also finds no indirect Ballot call or override. The definition only sets `m_bCanceled`; its notification is a REVIEW comment, not implemented native behavior. No production producer was added. `docs/parity/p903-exclusions.csv` records this new exclusion. The exclusion validator now validates supplemental audits alongside all 199 unchanged original P868 entries; it still rejects missing verdicts, duplicates, citation drift and unrelated unclassified exclusions. `scripts/tests/test_p903_exclusions.py` checks the new inventory plus the fail-closed cases. The scorecard itself is untouched.

### Static-core text paths: compiled tool commands and live fallback

These functions are **not just data-build source**. The native project graph compiles `missionigc.cpp` into shared IGC (`VS2022/Igc.vcxproj:169`), links it to the server (`VS2022/Server.vcxproj:263`) and client (`VS2022/Allegiance.vcxproj:277`), and compiles `FedSrv.CPP` / `fsmission.cpp` in the server (`VS2022/Server.vcxproj:196,199`). These project paths are relative to the native repository, one directory above its `src/` tree.

There are two distinct uses:

1. **Offline operator commands in the shipping server executable:** `AllSrv -Core2Text` initializes a temporary mission, loads a core, exports text, terminates/deletes it (`FedSrv/FedSrv.CPP:10385-10424`). `-Text2Core` imports text, dumps a binary and terminates/deletes the temporary mission (`10445-10480`). The converters are outside the `!ALLSRV_STANDALONE` guard which starts at `10481`; they are not confined to the old database build command. The standalone `tools/core2txt/Program.cs:29-54` is the equivalent operator workflow; executable placement does not justify copying it into the game loop.
2. **Real game loading:** `LoadIGCStaticCore` falls back to `<core>/Constants.csv` when the binary is absent, calls `ImportStaticIGCObjs`, adds a cached static core and returns version 1337 (`Igc/missionigc.cpp:2637-2672`). Native server mission construction calls that loader (`FedSrv/fsmission.cpp:164-166`); the client calls it too (`clintlib/clintlib.cpp:4212,4221`). The port loads binary cores and has no corresponding live text fallback. A binary round-trip or `P870StaticCoreLoaderTests` does not prove that missing runtime branch.

**Proposal for Claude:** add a validated `offline-tool` reachability for native capabilities whose actual callers are operator commands, and score those within a separate tool-capability subtotal. It may credit an implemented standalone operator tool with native citations and executable tests; it must not automatically credit all `tools/` references or promote live import functions. Of these fifteen rows, six export-only rows (12 weight) qualify; the other nine (18 weight) also serve native's live import fallback. Those nine retain no production credit until runtime loading is implemented/certified, or their offline and runtime coverage is explicitly split. If a future scorer includes tools in an aggregate, it should name the expanded scope and show a separate runtime score. Do not relabel all fifteen as production, config-only or n/a merely to increase a score. **No scorer or new reachability enum was applied in this lane.**

Every row below stays `implemented/test-only` under the current schema, with the reason and proposed classification recorded in its notes. Port paths are under `tools/core2txt/`; tests are in `tests/Allegiance.Core.Tests/P870Core2TxtTests.cs`. All use the CLI dispatch above. Each has a behavior test rather than the old inventory-only evidence.

| Row | Verdict / proposed classification | Native body (plus entry points above) | Port citation | Test |
|---|---|---|---|---|
| SIM-0686 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:115-125` | `CoreTable.cs:265` | `TechBitFromToken_resolves_loaded_names_case_insensitively_and_NA_unknown` |
| SIM-0687 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:128-149` | `CoreTable.cs:280` | `TechsListToBits_zero_hex_and_named_tokens; Tech_mask_hex_is_native_msb_first_uppercase_and_rejects_odd_length` |
| SIM-0688 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:152-206` | `CoreTable.cs:354` | `PartMaskFromToken_digit_atoi_negative_spaces_and_index` |
| SIM-0689 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:361-374` | `CoreTable.cs:375` | `PartsListToMask_adds_token_bits_including_repeats` |
| SIM-0690 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:377-413` | `CoreTable.cs:391` | `BitsToTechsList_raw_mask_when_flags_not_loaded_then_names` |
| SIM-0691 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:417-449` | `CoreTable.cs:418` | `BitsToPartsList_decimal_when_unloaded_then_names` |
| SIM-0692 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:451-486` | `CoreTable.cs:434` | `LoadTechBitsList_reads_csv_skips_header_reuses_last_name_and_missing_is_false` |
| SIM-0693 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:487-526` | `CoreTable.cs:459` | `LoadPartsBitsList_reads_csv_per_equipment_and_missing_is_false` |
| SIM-0694 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:547-2313` | `StaticCoreRoundtrip.cs:183 + codecs/ Read` | `Foreign_text_folder_without_sidecar_imports_the_same_semantics; All_shipped_static_cores_round_trip_byte_identically` |
| SIM-0752 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:5396-5706` | `StaticCoreRoundtrip.cs:74 + codecs/ Write` | `Exported_text_files_use_native_headers_and_column_counts; Exported_values_match_the_certified_static_core_decoders; All_shipped_static_cores_round_trip_byte_identically` |
| SIM-0785 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:209-357` | `CoreTable.cs:611` | `AbilitiesListToMask_parses_native_ability_names_per_kind` |
| SIM-0786 | Retain; offline implementation exists, live text-import fallback missing | `Igc/missionigc.cpp:530-545` | `CoreTable.cs:484` | `ReadCsv_is_tab_delimited_drops_trailing_empty_field_and_keeps_header` |
| SIM-0791 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:4246-4299` | `CoreTable.cs:664` | `ConstantsDamageComment_returns_native_names_and_fallbacks` |
| SIM-0792 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:4302-5313` | `codecs/ Write + StaticCoreRoundtrip.cs:74` | `Exported_text_files_use_native_headers_and_column_counts; Exported_values_match_the_certified_static_core_decoders` |
| SIM-0793 | Retain; propose offline-tool (export only) | `Igc/missionigc.cpp:5316-5363` | `CoreTable.cs:718` | `ConstantsGlobalComment_returns_native_descriptions_and_fallback` |

## Scorecard

Full outputs: [before](P903_assets/scorecard-before.txt), [after](P903_assets/scorecard-after.txt). Both validate all 857 sim rows; no row outside the brief changed. All other matrices' headline scores are unchanged.

| Sim measure | Before | After |
|---|---:|---:|
| Weighted headline | 2059/2146 = 95.95% | 2072/2144 = 96.64% |
| Completed / eligible | 759/806 = 94.17% | 770/805 = 95.65% |
| Config-only implemented weight | 9 | 0 |
| Test-only implemented weight | 36 | 30 |

The change is +9 from existing default-on NPC behavior, +4 from replay/context production coverage, and -2 eligible weight for dead native Cancel. The remaining 30 implemented text-tool weight still earns zero production credit. Nothing was marked complete by changing the wording of a behavior rule.

## Validation and limitations

| Check | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors |
| `DOTNET=~/.dotnet/dotnet tools/build-tools.sh` | PASS, all 101 tool projects, zero warnings/errors |
| Server Release `linux-x64 --self-contained -p:PublishAot=true` publish | PASS; no warnings, including no IL2026/IL3050 |
| New C# production regression | PASS, three baseline-setting isolated runs (plus one initial smoke); deliberately failed before the fix |
| New Python supplemental-audit tests | PASS, 3 tests ×3 isolated runs |
| Existing affected evidence | PASS ×3: P817 43 cases; P870 text-core 29 cases; two P872 replay/context tests; ballot cancellation-discard test |
| ONE Fast run | **FAIL**: 12,724 passed / 11 failed / 3 optional skips; 12,738 executions in 518.6 s; 9/9 infrastructure checks pass; four passing executions exceeded 10 s |
| Fast failure/slow isolation | Every failing/slow method repeated three times; details in `docs/TEST_FLAKES.md` and the qualification receipt |
| Showcase never-parks | PASS, five serial runs; all use artwork, server GC and pool/native/parallel/default-on GotoPlan |
| Native inventory / n/a audits | PASS: 351 bodies/cases inventoried; original 199 exclusions plus supplemental Cancel exclusion validated with source citations |
| Glossary / CSV / scope / diff | PASS: linked paths exist; LF CSV; exactly 27 authorized rows changed; clean `git diff --check` |

The Fast result remains failed. Profile writes and temporary-install lookup reproduced the known sandbox obstructions; scratch XDG control passes 3/3, and all nine path cases pass 3/3 with a descriptor alias to worktree scratch. The wingman defend null-target assertion reproduces 3/3, matching the open P901/P899 baseline issue. The other four failed methods and all four slow-test methods pass 3/3 alone; no unrelated behavior or assertions were changed. See [Fast receipt](P903_assets/fast-summary.json), [initial isolated evidence](P903_assets/isolated-summary.json), [failure/timing controls](P903_assets/qualification-summary.json) and [combined validation](P903_assets/validation.json).

| Showcase run | Test duration | Reached | Longest speed <1 with target | Shots / hits / kills | Load average at start (1/5/15 min) |
|---|---:|---:|---:|---:|---|
| 1 | 335.425 s | 24/30 | 1.805 s | 1848 / 964 / 9 | 1.63 / 2.69 / 3.70 |
| 2 | 247.538 s | 24/30 | 1.721 s | 1018 / 494 / 4 | 1.82 / 1.64 / 2.88 |
| 3 | 347.380 s | 24/30 | 0.204 s | 586 / 1 / 0 | 0.77 / 1.31 / 2.45 |
| 4 | 236.239 s | 24/30 | 0.906 s | 1489 / 298 / 2 | 0.62 / 1.03 / 1.96 |
| 5 | 214.849 s | 24/30 | 0.000 s | 328 / 2 / 0 | 2.23 / 1.36 / 1.85 |

All five observed actual gunfire/damage, satisfied the five-second parking bound and reported zero sim-pass stage failures. The recent dispatch-base history under P901 also passed 5/5 (206.9 / 217.0 / 270.2 / 220.3 / 466.5 s). That is historical comparison, not a controlled A/B run or a performance claim. P903 changes generation identity only; no bot steering code changed. [Showcase metrics and loads](P903_assets/showcase-summary.json).


The initial build attempt encountered a read-only NuGet HTTP cache outside the sandbox; rerunning with `NUGET_HTTP_CACHE_PATH` under the task scratch directory passed, without disabling auditing or suppressing warnings. Native reference/artwork were read-only; task scratch, TRX and logs are retained at [`.scratch/p903/`](../../.scratch/p903/). Raw builds/caches are removed after qualification; small scorecard and gate summaries are retained in `P903_assets/`.

No rig/mini/Mac access, scorer change, runtime text-import refactor, flag-default change, new service/env variable or HUD/media change was made. The live text-core fallback and the Compose steering override are left for separate work; this brief permits gameplay wiring only for the replay/context items and explicitly defers scorer policy to Claude. No native COM/Windows event-log layer was introduced: the context already rides the port's real career results.

## Native intent

- **Design:** GotoPlan's route, rest, visibility, dodge and completion rules are production AI behavior; replay context is a per-generation event/result identity. Both belong on actual server paths and need tests at their consumers. A modern backend can store structured mission/generation keys and retain the formatted context as compatibility metadata, avoiding identity collisions from the native modulo fields; P903 preserves the native format.
- **Era constraints:** Core text conversion is packaged inside AllSrv and shared IGC because the original Windows project reused the mission object and artwork resolver. A separate modern CLI is a clearer operator interface; native's additional live missing-binary fallback still matters if full loader parity is claimed.
- **Preserved bugs:** SIM-0479's integer-division builder offsets collapse to zero; SIM-0483 compares raw target coordinates across clusters. This audit changes neither. A modern implementation could spread construction approaches and compare route-space distances, but doing so would require an explicit parity departure and gameplay validation.
- **Dead code:** Ballot::Cancel is an unused design stub, not reachable gameplay or an implemented notification. A modern system would remove it, or add an explicit cancellation event with a defined producer and consumer contract; inventing such a feature here would not be native parity.
