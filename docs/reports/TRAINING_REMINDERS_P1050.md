# P1050 — Basic Flight native reminder proof

The defect is in the port: P941 changed M2's keyboard-booster reminder from an audio action to a repeated text action. P912 correctly expects five text prompts. A waiting player gets a sixth text entry from the changed port, and never gets native's `tm_2_33` reminder. This is deterministic, independent of HUD style or Jet visuals. **M2 qualification is complete: Classic and Modern reminders each pass 3/3, and all eight Basic Flight children pass. All three artwork training selections completed; the first retains one unrelated M8 branch failure. That case passed three isolated retries and a retained pre-fix-binary control; its original failure remains recorded. The final build passed with zero warnings and errors.** This report was created before implementation and kept current. Changes remain in the working tree; no commits or pushes.

## Native intent

Native source was inspected locally, read-only. Paths below are relative to its `src/` directory. The keyboard path has these five goals; all text is emitted once on entry, with no repeated/different text at a reminder.

| Goal | On-screen text on entry | Voice on entry | Waiting-player reminder | Native citation | Classification |
|---|---|---|---|---|---|
| 3.6 | Press the SPACEBAR when you are ready to proceed. | `tm_2_11r` | Same voice after each 30-second boundary | `training/mission2.cpp:347-355` | Design |
| 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` | `tm_2_16r` | Same voice after each 30-second boundary | `training/mission2.cpp:432-441` | Design |
| 5.3 | Use your scroll wheel or joystick throttle to adjust thrust. | `tm_2_18r` | Same voice after each 30-second boundary | `training/mission2.cpp:473-481` | Design |
| 6.16 | Press the SPACEBAR when you are ready to proceed. | `tm_2_24r` | Same voice after each 30-second boundary | `training/mission2.cpp:626-634` | Design |
| 7.13 | Press and hold the SPACE key to fire the booster. | None: `tm_2_33` start action is commented out | `tm_2_33`, which says TAB, after each 30-second boundary | `training/mission2.cpp:741-748` | Native bug, preserved for exact parity |

The native script identifies the repeated recordings with these words (script comments, not a new audio transcription):

| Goal / recording | Authored reminder wording |
|---|---|
| 3.6 / `tm_2_11r` | When you are comfortable with the operation of your craft, press the SPACEBAR to proceed. |
| 4.7 / `tm_2_16r` | Adjust the throttle control full forward on your joystick, or press the BACKSLASH key. |
| 5.3 / `tm_2_18r` | Try adjusting the throttle to see how this gauge changes. |
| 6.16 / `tm_2_24r` | When you are comfortable with the operation of your ship, press the SPACEBAR to proceed. |
| 7.13 / `tm_2_33` | On the keyboard, press and hold the TAB key to fire your boosters. |

The joystick branch of 7.13 plays `tm_2_32` on entry and repeats it, with the button-3 text (`training/mission2.cpp:732-739`); the window proofs use the keyboard branch. Existing joystick behavior is unchanged.

`TrainingMission::CreateTooLongCondition` constructs `PeriodicCondition(NTimesCondition(True, 2, sticky=true), 30)` and a `PlaySoundAction` (`training/trainingmission.cpp:927-932`). `Start` seeds the counter at one (`training/ntimescondition.cpp:36-39`); the first periodic evaluation reaches two and sticky stays true (`:49-57`). Therefore **reminders are unlimited**, once per interval while the goal remains unanswered, not limited to two. `PeriodicCondition` initializes its base on goal start (`training/periodiccondition.cpp:35-41`), tests strict `elapsed > period`, and advances the base by one period (`:52-65`). Thus reminders occur on the first evaluated frame strictly after 30, 60, 90, … seconds, without accumulated drift. Goal success is tested before constraints (`training/Goal.cpp:79-90`), so answering stops reminders. `PlaySoundAction` starts audio, not text (`training/playsoundaction.cpp:35-43`); the client forwards this to the sound buffer creation path (`WinTrek/WinTrek.h:281-282`, `WinTrek/WinTrek.cpp:11253-11280`). Text is independently sent through `ReceiveChat` by `MessageAction::Execute` (`training/messageaction.cpp:45-53`).

The four normal reminder recordings and the timing are lesson design. Fixed prerecorded key names are an era constraint; muting the initial TAB recording but leaving it as a reminder is a native bug, not intentional new instructions or a text repeat. P941 deliberately corrected that bug; this brief explicitly requires exact native M2 behavior, so P1050 restores it. Mission 3's P941 corrections remain outside scope.

## Observed timelines and evidence

Baseline source was checkout `f3d4186b`. Baseline binaries were copied before the production fix; their only added code records existing output at emission time. A tool-local console observer records every prompt, voice and goal transition, with monotonically ordered sequence, training-clock time and active goal in `flight-events.jsonl`. This avoids attributing an action to a later goal after a frame advances the script. The fixed observer additionally records the current training frame delta for exact threshold checks. Existing `[p940] VOICE … started=True` logs independently confirm that all nine reminder-goal voice actions (four initial plus five repeats) created real playback instances in each of the six fixed reminders windows (54 starts total); see the [playback-start receipt](TRAINING_REMINDERS_P1050_assets/playback-starts.json). Raw evidence stays under `<lane evidence dir>` (`.scratch/p1050/`); it includes logs, JSONL, build identities, real-window screenshots and TRX results. No external media is added to the change set. The legacy P912 `.scratch/p940/` and `.scratch/p931/` paths resolve through ignored symlinks into the task evidence archive, so existing log links remain valid while physical evidence stays under `.scratch/p1050/`. Both saved lesson-7 captures were opened and checked: Classic and Modern use their distinct HUD layouts.

<!-- P1050_TIMELINES_START -->

Complete emission-time prompt lists are below. All times are training-clock seconds. The [machine-readable evidence](TRAINING_REMINDERS_P1050_assets/prompt-timelines.json) also includes the ordered voices at these goals and hashes of the full raw JSONL files. Raw logs, complete goal/voice timelines, screenshots and binary identities remain under `<lane evidence dir>`.

**Baseline — Classic, reminders.**
Failed in 531.832 s.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.533293 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 157.802513 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 195.093269 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 292.097096 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 354.512594 | 7.13 | `Press and hold the SPACE key to fire the booster.` |
| 6 | 384.522513 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

| Goal | Voice emissions (time: stem) |
|---|---|
| 3.6 | 101.533293: `tm_2_11r`, 131.533659: `tm_2_11r` |
| 4.7 | 157.802513: `tm_2_16r`, 187.806368: `tm_2_16r` |
| 5.3 | 195.093269: `tm_2_18r`, 225.093903: `tm_2_18r` |
| 6.16 | 292.097096: `tm_2_24r`, 322.099741: `tm_2_24r` |
| 7.13 | None |

**Baseline — Modern, reminders.**
Failed in 537.101 s.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 102.947201 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 159.211219 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 196.494076 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 293.618016 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 356.072463 | 7.13 | `Press and hold the SPACE key to fire the booster.` |
| 6 | 386.080023 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

| Goal | Voice emissions (time: stem) |
|---|---|
| 3.6 | 102.947201: `tm_2_11r`, 132.948489: `tm_2_11r` |
| 4.7 | 159.211219: `tm_2_16r`, 189.211551: `tm_2_16r` |
| 5.3 | 196.494076: `tm_2_18r`, 226.494365: `tm_2_18r` |
| 6.16 | 293.618016: `tm_2_24r`, 323.621289: `tm_2_24r` |
| 7.13 | None |

**Baseline — Classic, default.**
Passed in 350.438 s.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.510891 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 125.805448 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 131.071342 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 196.093298 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 226.548999 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

**Baseline — Modern, default.**
Passed in 350.285 s.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.584290 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 125.860700 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 131.115890 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 196.098188 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 226.475450 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

**Fixed — Classic, reminders.**
Passed.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 102.123363 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 158.404649 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 195.671976 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 292.793007 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 355.252131 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

| Goal | Voice emissions (time: stem) |
|---|---|
| 3.6 | 102.123363: `tm_2_11r`, 132.123912: `tm_2_11r` |
| 4.7 | 158.404649: `tm_2_16r`, 188.408259: `tm_2_16r` |
| 5.3 | 195.671976: `tm_2_18r`, 225.673768: `tm_2_18r` |
| 6.16 | 292.793007: `tm_2_24r`, 322.793866: `tm_2_24r` |
| 7.13 | 385.256077: `tm_2_33` |

**Fixed — Modern, reminders.**
Passed.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.560728 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 157.797353 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 195.062443 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 292.097493 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 354.478389 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

| Goal | Voice emissions (time: stem) |
|---|---|
| 3.6 | 101.560728: `tm_2_11r`, 131.560990: `tm_2_11r` |
| 4.7 | 157.797353: `tm_2_16r`, 187.797809: `tm_2_16r` |
| 5.3 | 195.062443: `tm_2_18r`, 225.062609: `tm_2_18r` |
| 6.16 | 292.097493: `tm_2_24r`, 322.098524: `tm_2_24r` |
| 7.13 | 384.479009: `tm_2_33` |

**Fixed — Classic, default.**
Passed.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.461408 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 125.731338 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 130.983716 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 195.944355 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 226.331014 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

**Fixed — Modern, default.**
Passed.

| Entry | Training time (s) | Goal | Prompt |
|---|---:|---|---|
| 1 | 101.478604 | 3.6 | `Press the SPACEBAR when you are ready to proceed.` |
| 2 | 125.748385 | 4.7 | `Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust.` |
| 3 | 130.999750 | 5.3 | `Use your scroll wheel or joystick throttle to adjust thrust.` |
| 4 | 195.965807 | 6.16 | `Press the SPACEBAR when you are ready to proceed.` |
| 5 | 226.403032 | 7.13 | `Press and hold the SPACE key to fire the booster.` |

<!-- P1050_TIMELINES_END -->

## Fix and regression

- `src/Allegiance.ClientCore/Training/Mission2.cs:419`: restores `CreateTooLongCondition(30.0f, Tm2_33)` at 7.13 and keeps the initial recording muted. Native `mission2.cpp:746-748` requires audio-only reminders; the existing five-text `SequenceEqual` assertion is retained.
- `tests/Allegiance.ClientCore.Tests/TrainingMission2Tests.cs:57`: five headless cases traverse the actual M2 goal graph, verify each entry voice (including initial booster suppression), then test exactly 30/60/90 seconds and the next representable float after each boundary. Each case requires only the configured voice, no additional text, a third repeat (proving unlimited/sticky behavior), and cessation after answering. Integral fixture timestamps prevent rounding from turning an intended exact boundary into a value already beyond it.
- `tools/p912-training-proof/BasicFlightTrace.cs:23`: emission-time JSONL and a strict window oracle. It requires exact ordered `(goal, text)` tuples, exact voice identity and count at each goal, immediate entry audio for the first four goals, and each reminder on the first frame crossing native's strict 30-second boundary. The 32-second waiting policy must yield exactly one repeat per goal. There is no deduplication or contains-only prompt check. The same observer runs for default, muted and missing-audio M2 windows; real playback remains checked by P940's existing installed-audio receipts.
- `TrainingMission.cs`: clarifies that the sticky two-count trigger repeats indefinitely, rather than limiting reminders to two. This is an XML-summary correction only.
- `TrainingPromptAudit.cs` and `P941PromptAuditTests.cs`: scope the documented exception to M2's native keyboard booster reminder. Three existing M3 text corrections remain required; the muted initial recording list still contains all four recordings. The P941 report now links this superseding M2 decision.
- `docs/parity/training-matrix.csv` row TRN-0232 and `GLOSSARY.md`: track the native reminder rule and evidence. `docs/TEST_FLAKES.md` identifies the historical signature as a deterministic port mismatch, not a load flake.
- `tools/run-training-tests.sh --window-proofs-once`: optional switch excludes only the long P912 window class on passes 2–3, as this brief permits. Default gate selection is unchanged; all other Fast/Live training cases, including Nanite, remain selected on all three passes.

The corrected headless regression failed **only 7.13** before the production fix (four pass / one fail); the failing assertion expected native sound 2036 and received no sound. A preceding draft of the fixture also exposed float-boundary ambiguity; that fixture was corrected before drawing a product conclusion. An early no-build attempt overlapped the final build and loaded stale assemblies for its first process; it is not counted as final validation.

## Validation

| Check | Result |
|---|---|
| Initial instrumented solution build | Passed, 0 warnings / 0 errors |
| Post-fix solution build | Passed, 0 warnings / 0 errors, 25.29 s; final rebuild after XML-summary-only clarification also passed 0/0, 8.63 s |
| New headless tests, three fresh isolated processes | Passed **5/0/0 ×3**, 45 / 46 / 46 ms; repeated after the final build: **5/0/0 ×3**, 45 / 41 / 43 ms (passed/failed/skipped) |
| All tool projects | Passed **145/145**, each with 0 warnings / 0 errors |
| Full artwork training gate, repeat 3 | Completed, **failed overall** on unrelated Classic M8 branch: Core **131/0/0 ×3**; MonoGame **297/1/3**, **256/0/1**, **256/0/1** |
| Full P912 class (within training pass 1) | Completed: **41 passed / 1 unrelated M8 branch failure / 2 optional hunt skips**; all eight M2 children passed |
| Classic reminders, two further isolated runs | **Passed / Passed; Classic 3/3 overall** |
| Modern reminders, two further isolated runs | **Passed / Passed; Modern 3/3 overall** |
| Other P912 Basic Flight policies | **All eight children passed**: default, muted, missing-audio and reminders in both styles |
| P941 audit / installed voice loader | Audit **5/0/0 ×3**; loader with null audio backend **1/0/0 ×3** |
| Fast tier and failure isolation | **Failed: 15,372 passed / 4 failed / 6 skipped**, 746.587 s; 18 passing executions over calibrated 13.653 s; assertion failures isolated ×3; all 18 timing offenders passed ×3 within the calibrated limit |
| Glossary / parity / hygiene checks | Passed: 962 linked paths; 241 parity rows; clean diff formatting and new-file hygiene |

The first full training pass exposed an unrelated M8 Classic `sacrifice` failure at 3.22: `did not exercise the combined bomber/outpost loss branch`. The pilot stopped repairing at bomber hull 0.1155 while the outpost still had 0.0466 shield / 1.0000 armor; the bomber died, the outpost survived, and the native bomber-loss message was issued. This is a failure to reach the requested proof branch, rather than a missing M2 prompt. M8/controller/gameplay code is unchanged. The three exact-case replays all passed (**5m50.118s / 5m50.033s / 5m49.339s**, zero skips). One control using the retained pre-fix assemblies also passed (**352.414 s** process wall time). The original failed child took **5m48.772s**. These results demonstrate reachability and do not establish a load cause or prove universal reliability; the branch failure remains unresolved. No M8 expectation or controller code is changed, and the full gate retains its failure. The [M8 isolation receipt](TRAINING_REMINDERS_P1050_assets/training-isolation.json) records each result.

The artwork gate command is:

```sh
export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1050/nuget-http
tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once \
  --results-dir .scratch/p1050/training
```

`local.env` supplies the installed artwork. The first pass includes the entire P912 class; only that long-window class is excluded on passes 2–3. The additional reminders repeats started after the full class returned, each with an exact style/policy filter in a fresh test process. Result lines and durations below come from the completed TRX files.

<!-- P1050_TRAINING_RESULTS_START -->

These rows are extracted from the preserved TRX files. Durations below are the test runner’s child-test durations, including process startup and completion checks.

| Repeat | Project | Passed | Failed | Skipped | Total |
|---|---|---:|---:|---:|---:|
| 1 | Allegiance.ClientCore.Tests | 131 | 0 | 0 | 131 |
| 1 | Allegiance.ClientCore.MonoGame.Tests | 297 | 1 | 3 | 301 |
| 2 | Allegiance.ClientCore.Tests | 131 | 0 | 0 | 131 |
| 2 | Allegiance.ClientCore.MonoGame.Tests | 256 | 0 | 1 | 257 |
| 3 | Allegiance.ClientCore.Tests | 131 | 0 | 0 | 131 |
| 3 | Allegiance.ClientCore.MonoGame.Tests | 256 | 0 | 1 | 257 |

**Reminders child result lines (TRX outcome, name and duration):**

```text
training-1-Allegiance.ClientCore.MonoGame.Tests: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "reminders") [00:08:56.1957821]
training-1-Allegiance.ClientCore.MonoGame.Tests: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "reminders") [00:08:53.3875991]
isolated-Classic-1: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "reminders") [00:08:52.6966647]
isolated-Modern-1: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "reminders") [00:08:52.9466536]
isolated-Classic-2: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "reminders") [00:08:52.6190705]
isolated-Modern-2: Passed Allegiance.ClientCore.MonoGame.Tests.P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "reminders") [00:08:47.1572770]
```

**All Basic Flight children in the full class:**

| Child | Outcome | Duration |
|---|---|---|
| `Every_lesson_completes_in_one_real_window_session(mission: 2, style: "Classic", policy: null)` | Passed | `00:05:48.4974497` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "reminders")` | Passed | `00:08:56.1957821` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "muted")` | Passed | `00:05:48.9116163` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "missing-audio")` | Passed | `00:01:17.9825362` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "reminders")` | Passed | `00:08:53.3875991` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Classic", policy: "missing-audio")` | Passed | `00:01:17.9250022` |
| `Every_lesson_completes_in_one_real_window_session(mission: 2, style: "Modern", policy: null)` | Passed | `00:05:49.5299471` |
| `Basic_flight_completes_with_muted_missing_or_repeated_narration(style: "Modern", policy: "muted")` | Passed | `00:05:51.1695639` |

Bounded [training results](TRAINING_REMINDERS_P1050_assets/training-results.json) include all P912 case outcomes and the actual window-pass/native-reminder receipt lines.

Passes 2–3 contain exactly the same non-P912 case set as pass 1 in each project; only the permitted long-window class was omitted.

<!-- P1050_TRAINING_RESULTS_END -->

The complete Fast sweep remains **failed**; isolated passes do not turn it green or establish a load root cause. All four assertion signatures already occur in `docs/TEST_FLAKES.md`; these gameplay/UI implementations are unchanged.

| Exact failure | Broad signature | Three fresh one-case processes (s) |
|---|---|---|
| `P1017BuyFlowLiveTests.Money_change_while_docked_flips_need_and_buy_Live` | Expected `Muted`, actual `Worse`; 2.791 s | pass 3.118 / pass 3.212 / pass 3.217 |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | `7.0000000000000009` above inclusive 7 s; 0.212 s | fail 0.613 / pass 0.708 / pass 0.735; same one-ULP signature |
| `LivePositionRequestTests.Empty_side_request_auto_accepts` | Missing expected receipt; only `SetTeamLeader` present; 0.087 s | pass 0.454 / pass 0.489 / pass 0.456 |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | `8.04999924` outside [4, 8]; 0.220 s | pass 0.750 / pass 0.767 / pass 0.673 |

Fast infrastructure passed **9/9**. Six skips: three optional Meshy-pack cases, two existing ChangeStation cases whose map has no same-sector enemy station, and the artwork manifest check whose pinned-corpus precondition is not met. There are no new-test or training-artwork skips in Fast. All 18 timing offenders passed three exact one-case processes each (**54/0/0**), all under the calibrated limit. Exact-case timing and outcomes are retained in the [timing isolation receipt](TRAINING_REMINDERS_P1050_assets/timing-isolation.json). Bounded receipts: [Fast summary](TRAINING_REMINDERS_P1050_assets/fast-summary.json), [assertion isolation](TRAINING_REMINDERS_P1050_assets/fast-isolation.json).

| Timing offender | Broad sweep (s) | Three exact-case repeats (s) |
|---|---:|---|
| `Allegiance.ClientCore.MonoGame.Tests.Skins.JetFlightSymbologyTests.Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology` | 17.368 | 4.425 / 4.390 / 4.321 |
| `Allegiance.ClientCore.MonoGame.Tests.Skins.JetSkinScaffoldTests.Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model` | 15.427 | 3.474 / 3.357 / 3.490 |
| `Allegiance.ClientCore.MonoGame.Tests.Skins.JetAmberPaletteTests.Amber_draw_allocates_zero_bytes_per_frame` | 19.469 | 4.759 / 4.721 / 4.684 |
| `Allegiance.ClientCore.MonoGame.Tests.Skins.JetTargetLockTests.Draw_with_a_locked_leading_ordered_target_allocates_nothing` | 18.307 | 4.890 / 4.902 / 4.894 |
| `Allegiance.ClientCore.MonoGame.Tests.Skins.JetStoresCautionTests.Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing` | 14.868 | 3.657 / 3.682 / 3.688 |
| `Allegiance.ClientCore.Tests.P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production(civ: 5)` | 14.696 | 1.685 / 1.711 / 1.736 |
| `Allegiance.ClientCore.Tests.P704TechLoadoutTests.Dock_local_masks_are_never_combined_and_finale_uses_the_same_bomber_selection` | 13.970 | 6.164 / 6.132 / 6.150 |
| `Allegiance.ClientCore.Tests.P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | 25.062 | 4.293 / 4.360 / 4.335 |
| `Allegiance.ClientCore.Tests.P832bProsperityTests.No_goal_decision_trace_matches_dispatch_base` | 14.334 | 1.727 / 1.731 / 1.665 |
| `Allegiance.ClientCore.Tests.P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 25)` | 18.382 | 6.289 / 6.226 / 6.195 |
| `Allegiance.ClientCore.Tests.SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 27.766 | 3.842 / 3.803 / 3.830 |
| `Allegiance.ClientCore.Tests.SC3CloakPolicyTests.Research_loadouts_offer_real_legal_cloaks_without_unlock_bypass` | 14.577 | 1.397 / 1.421 / 1.416 |
| `Allegiance.ClientCore.Tests.P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 26.263 | 4.275 / 4.039 / 4.020 |
| `Allegiance.ClientCore.Tests.SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 3)` | 19.681 | 5.826 / 5.849 / 6.435 |
| `Allegiance.Core.Tests.P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates(civ: 5)` | 32.823 | 4.100 / 3.501 / 6.382 |
| `Allegiance.Core.Tests.P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | 25.574 | 3.026 / 3.013 / 2.652 |
| `Allegiance.Core.Tests.P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable(civ: 201)` | 16.749 | 1.600 / 1.587 / 1.403 |
| `Allegiance.Core.Tests.P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes(civ: 3)` | 19.212 | 2.446 / 2.933 / 2.860 |

A redundant overlapping initial proof build hit a shared runtimeconfig file lock while the solution build was writing it. The solution build succeeded; later builds are sequenced to avoid this operator-induced collision. No package/cache permission failure blocked work. All build/test invocations set the task-local NuGet HTTP cache.

All task-owned build, test and evidence-collection processes finished before handoff. The brief is archived in ignored task scratch and is absent from the change set. No commits or pushes were made.

## Native intent / Modern approach

**Native intent:** one text prompt on entering each goal; four initial reminder voices; no initial keyboard-booster voice; indefinite audio-only reminders at strict, drift-free 30-second intervals, stopping when answered. Exact parity includes the native TAB/SPACE reminder inconsistency.

**Modern approach:** preserve the requested behavior, expose the complete emitted timeline, and test the timer with a controlled clock plus real-window evidence. A future product decision could use a shared prompt object for both initial and repeated guidance to avoid stale recorded key names; that would be an explicit behavior change, outside P1050.
