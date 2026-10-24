# P785 (PAR-4) — training completion: slideshows, Introduction mission, training clock keys, tutorial callouts

Branch `pi/p785`, worktree at master `6e5c745f` (the brief's cites were re-grepped against
`~/Development/Allegiance/src/` on 2026-10-24; every line number below was verified this session).

## What shipped

| # | Item | Native rule (cites) | Port before | Port after | Data path | Tests |
|---|------|--------------------|-------------|------------|-----------|-------|
| 1 | **TK_PauseTM** | `trainingmission.cpp:347-358` toggles `Time::Pause()/Continue()` (`zlib/ztime.cpp:15-49`, stamp/`negative-offset` freeze, no clock jump on resume); consumed without reaching the key conditions | Catalogue row absent; `RecordKeyPress` returned `true` without pausing anything | `PauseTM` action (`InputActionCatalog.cs`, native Ctrl+Pause default) routed from the flight input (`UpdateTrainingKeys`, the `WinTrek.cpp:8753-8769` gate) into `TrainingMission.RecordKeyPress`, which toggles the new `TrainingClock` (port of `zlib/ztime.cpp`); `Game1.UpdateTraining` feeds `_trainingClock` from `TrainingClock.Advance` and freezes the embedded host (`TrainingHost.Paused`) while paused, exactly as native's in-process clock freeze. Teardown unpauses (`trainingmission.cpp:123-125`) | Client-local static `TrainingClock`; the mission conditions already read `Site.TimeSeconds`, so a frozen clock freezes every goal | `P785TrainingCompletionTests.TK_PauseTM_toggles_pause_and_never_reaches_the_key_conditions`, `.Pause_freezes_the_clock_and_continue_resumes_from_the_freeze_point` |
| 2 | **TK_SkipTMGoal** | `trainingmission.cpp:359-364` — handler is `#ifdef _DEBUG_TRAINING` (XXX NYI, empty case); the define appears in **no** project file, so shipping native falls through to the key conditions (`:402-410`) | No action, no handler | `SkipTMGoal` action (numpad Decimal; native vk 108/VK_SEPARATOR has no MonoGame key) routes to `RecordKeyPress`: shipping mode forwards to the key conditions, debug mode consumes it as the native NYI no-op | Same | `P785TrainingCompletionTests.Clock_and_skip_keys_fall_through_to_key_conditions_in_shipping_mode`, `.Debug_clock_keys_change_the_clock_and_skip_goal_is_NYI` |
| 3-5 | **TK_Increase/Decrease/ResetTMClockSpeed** | `trainingmission.cpp:366-384` — same `_DEBUG_TRAINING` gate; debug bodies shift the clock ±1 / reset, clamped ±3 (`zlib/ztime.cpp:58-67`); the 2^shift scaling exists only in the debug `Time::Now` (`ztime.h:57-63` vs shipping `:76-77`) | No actions, no handlers | `IncreaseTMClockSpeed`/`DecreaseTMClockSpeed`/`ResetTMClockSpeed` (native Ctrl+F11/F10/F12 defaults) route to `RecordKeyPress`; `TrainingClock.SetShift` clamps ±3 and `Advance` scales only behind `TrainingClock.DebugTrainingKeys` (default off = shipping) | Same | `P785TrainingCompletionTests.Debug_clock_keys_change_the_clock_and_skip_goal_is_NYI`, `.SetShift_clamps_to_plus_minus_three`, `.Shift_scales_the_clock_only_behind_the_debug_gate` |
| 6 | **TK_CommandFinishTM** | `igc.h:433`, registered `trekinput.cpp:347,1614` ("Finish goal (training mission only)"), **no case** in `RecordKeyPress` — falls to the key-condition branch (`trainingmission.cpp:402-410`), so any mission `GetKeyCondition(207)` resolves (`GetKeyCondition.cpp:55-60`) | No action | `FinishTM` action (ships unbound — the port's End is `RejectCommand`; the native End chord is offered on the rebind popup) routed to `RecordKeyPress`, which forwards 207 to the site key queue | Same | `P785TrainingCompletionTests.TK_CommandFinishTM_is_delivered_to_the_key_conditions` |
| 7 | **Pre/post-training slideshow flow** | `WinTrek/Slideshow.cpp` — per-slide `StartSound(soundID)` (:105), sound-finish arms a 1 s timer (:123-146), any key/click advances (:147-150, :179-183), exhausted → `Dismiss` (:107-113); `TrainingSlideshow::Dismiss` (:30-50) shows "Training simulation initiated..." and starts the mission; `PostTrainingSlideshow::Dismiss` (:25-29) returns to `ScreenIDTrainScreen`; mission completion routes to the post slideshow unless skipped (`trainingmission.cpp:110-115`, `WinTrek.cpp:3559-3566`) | Absent; mission completion bounced to the chooser | `SlideshowModel` (pure, `src/Allegiance.ClientCore/Training/SlideshowModel.cs`) + `TrainingSlides`/`TrainingSlideData` (the **native slides**, extracted verbatim from the artwork corpus's `tm_*.mdl` `slides` lists — see below) + `Game1.BeginTrainingSlideshow/UpdateTrainingSlideshow` and the `IntroView.TrainingSlideshow` view: pre-Dismiss starts the mission, post-Dismiss returns to the training screen, completion opens the post slideshow (skip flag honoured) | Pure model; the view renders `TrainingSlideData` captions (native text, wrapped; visual stand-in for the full-screen slide images) and plays the slide clip | `P785TrainingCompletionTests.Slideshow_*` (4 tests); handler test `P785TrainingHandlerTests.Slideshow_keys_advance_and_the_post_slideshow_dismisses_to_the_training_screen` |
| 8 | **SIM-0272 — slideshow slide sound** | `Slideshow.cpp:105` `GetWindow()->StartSound(soundID)` | No condition-to-cue path | `SoundManager.PlaySlideSound(stem)`: resolves the slide's `tm_slide_*` clip through the port Content pack, else the installed artwork corpus (`SoundEffect.FromFile`), returns the playing instance; `UpdateTrainingSlideshow` detects `Stopped` → `OnSoundFinished()` → 1 s timer — native's finish-event chain | The `SoundID` of each native slide rides `TrainingSlideData` (stem strings); no new audio media in the repo — clips come from the artwork install | Model-level progression tests above (the sound path is env-dependent: no-op without artwork) |
| 9 | **Introduction mission (TM1)** | `trainingscreen.cpp:182-185` routes TM1 to `ScreenIDPostTrainSlideshow` (the `tm_1_introduction` namespace, `WinTrek.cpp:2503`); **no Mission1 exists** — `Training.cpp:85-89` returns false for `c_TM_1_Introduction` (mission 7 is likewise gone: `g_bIsMission7`, `Training.cpp:86-88,113-116`) | `Playable: false`, "coming soon" | TM1 playable: the chooser launches the 15-slide Introduction slideshow (native text), Dismiss returns to the training screen. Mission 7: native has none (removed, `//TM7 removed` trainingscreen.cpp:57) — nothing to port, and the port never lists it | `TrainingScreenModel.Missions` + `TrainingSlides.Introduction` | Handler test `P785TrainingHandlerTests.Training_screen_enter_on_introduction_opens_the_tm1_slideshow`; model test `Introduction_slideshow_carries_the_native_slides` |
| 10 | **Back to intro** | `trainingscreen.cpp:160-164` `OnButtonBack` → `screen(ScreenIDIntroScreen)` | Escape returned to the Single-Player chooser | Escape → `IntroView.Main` (the main menu) | `Game1.IntroScreen.cs` | Production handler test `P785TrainingHandlerTests.Training_screen_escape_returns_to_the_intro_screen_not_the_chooser` |
| 11 | **Basic Flight launch/runtime (real client)** | `WinTrek.cpp:7401-7421` `StartTraining`: mission start → `screen(ScreenIDCombat)` → ship/camera setup; the whole client flies the lesson | `BeginTrainingMission` built the host but never connected the client — the lesson ran invisibly behind the intro screen | `BeginTrainingMission` now loopback-connects the real client to the embedded host (`BeginConnect("127.0.0.1", host.Port)`, the skirmish path), auto-seats side 0, and ready-check releases immediately (host mission already `Stage=Started`); the training key gate + clock run against the live session | Real `ConnectionSession` — the lesson is flown with the real flight input and HUD | Handler/slideshow tests above; headless full-lesson completion stays in `TrainingMission2Tests`/TR3-TR5 (unchanged) |
| 12 | **Training HUD callouts** | `WinTrek/indicator.cpp:461-735` `RenderTrainingOverlay`: red (`Color(1,0,0,0.9)`, :475) angular two-line pointer from anchor A via B to C (text-width leg, :676-690), hilite dot, description text; Roll/Fuel/Throttle carry a second line (:723-733); 23 overlays | `TrainingOverlay.Current` existed but nothing drew it | `HudTrainingCallout.cs` — the pure geometry table (all 23 overlays, native y-up anchors flipped to the port's top-left space; Sector/Inventory/Target absolute anchors recomposed relative to centre — visual parity) drawn by `HudRenderer.DrawTrainingCallout`, **inherited un-overridden by `ModernHudRenderer`** so both styles draw it; the overlay id rides the shared `HudContext.TrainingOverlayId` from `TrainingOverlay.Current` | Shared `HudModel`/`HudContext` — one model, both renderers | `Every_training_overlay_id_resolves_and_none_resolves_for_zero`, `.Roll_callout_carries_the_native_text_and_second_line`, `.Modern_hud_renderer_inherits_the_training_callout_draw` |
| 13 | **Roll tutorial cue** | `indicator.cpp:634-641` | Numeric roll gauge was the only roll surface (extension) | Overlay id 17 renders the native roll cue in both styles (screenshot pair below); the gauge stays, honestly labelled an extension in `DrawRoll`'s doc | Same | Reflection test above + proofs |
| 14 | **Critical tip 30 s training lifetime** | `console.cpp:913-931`: `fTipTextDuration = Training::IsTraining() ? 30.0f : 7.5f`, evaluated per frame against each slot's set time | Fixed 7.5 s expiry stamped at push | `HudConsoleFormat.CriticalTipLifetime(bool training)`; `Game1.Feedback` stores the set time and `UpdateFeedback` prunes per frame against the current training state — a tip pushed before the mission ends keeps the 30 s lifetime while training lasts, exactly as native | Shared model path (`HudConsoleText.CriticalTips`); both renderers draw via the unchanged `DrawConsoleText` | Model test + production handler test `P785TrainingHandlerTests.Critical_tips_live_30s_while_a_training_mission_runs_and_7_5s_otherwise` |
| 15 | **Slideshow audio content** | tm_*.mdl slides lists (artwork corpus, pin `4a60aa6` in `packaging/sources.json`) | — | `TrainingSlideData.cs` — generated table (extracted with a one-off regex pass over the corpus MDLs during this wave; not committed) of the native `(caption, soundStem)` pairs: TM1 15 slides, M3 13, M4 2, M5 26, M6 1, M8 5 + the post slideshows (2/3/4/5/6/8: 1,1,1,4,1,4) | Static data, cited to the corpus MDLs | `Introduction_slideshow_carries_the_native_slides` |

**Interface matrix consistency:** the training rows exist under both `screens` and `lobby` (P743 split) and both were updated
identically (rows 372-375 / 653-656), plus `screens`/"Training screen" (row 500) and the six `commands` rows (138, 181-184, 188),
the three `hud` rows (390, 420, 457) and `sim-rules-matrix.csv` SIM-0272.

## Native behaviour classified

- **Design (port as-is):** TK_PauseTM pause semantics; FinishTM as a key-condition key; the slideshow progression
  (sound-finish + 1 s + any-key); back-to-intro destination; TM1 = the Introduction slideshow; the 30 s training tip
  lifetime; the training callouts. *Modern better approach:* the slideshow would be a scripted skippable video and the
  callouts contextual tooltips — but parity keeps the native shapes.
- **Era/debug constraint:** `TK_SkipTMGoal` (XXX NYI) and the three clock keys are `_DEBUG_TRAINING`-gated in native and
  the define is in no project file — registered but inert in every shipped build. The port reproduces both halves
  (`TrainingClock.DebugTrainingKeys`, default off). A modern approach ships a real skip key and a visible clock HUD.
- **Bug (noted, not "fixed" beyond parity):** `TK_CommandFinishTM` is advertised as "Finish goal" (`trekinput.cpp:1614`)
  but no shipped mission listens for it; the port forwards it to key conditions identically, so a future mission can use it.

## How the data reaches the client

Everything above rides existing client-local state — **no wire changes**: `TrainingClock`/`SlideshowModel` are pure C#;
the overlay id flows through the existing `SetHudOverlayAction` → `TrainingOverlay.Current` static → `HudContext`
(already the TR-2 route); tips flow through the existing HUD-9 console path. Native computes nothing here that the port
must derive from IGC state — the clock and slide lists are client-side by design.

## Tests (all new, all fail on master — the types/methods under test do not exist there)

`tests/Allegiance.ClientCore.Tests/P785TrainingCompletionTests.cs` (14), 
`tests/Allegiance.ClientCore.MonoGame.Tests/P785TrainingHandlerTests.cs` (5, uninitialized-Game1 production handlers),
updated: `TR2TrainingTests` (TM1 playable), `HelpOverlayTests` (19 groups), `P625KeyMapTests` (19 groups; the F-row
invariant now ignores modified chords — native itself binds Ctrl+F10/F11/F12), `P642ConsoleZoomTests` unchanged (passes
once the catalogue carried the native MDL chords).

## Verification numbers

- `~/.dotnet/dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**.
- `tools/build-tools.sh` — all 73 tool projects built, 0/0.
- New/affected tests 3× alone: P785+TR2 26/26 ×3; P785+HelpOverlay+P625+P642 118/118 ×3.
- Fast tier (`tools/run-tests.sh --tier fast`), final run after all changes: 10 202 tests —
  ClientCore.MonoGame 3308/0, ClientCore 1762/0, Lobby 24/0, Core 5092 passed + **1 failure:
  `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
  — the live-network greet-timeout load flake already ledgered in `docs/TEST_FLAKES.md` (HUD-19, 2026-10-21);
  unrelated to P785 (touches no relay/transport code); passed 3/3 exact-class isolated invocations afterwards.**
  All P785-adjacent suites green. (Earlier fast runs while the wave was in flight exposed the
  catalogue-count/manifest tests above — all fixed.)
- `tools/parity-scorecard.py` — TOTAL 95.02% / 94.27% (unchanged averages, updated rows validate).
- `tools/check-glossary-links.py` — OK (96 paths).
- `packaging/generate-manifest.py --check` — green after regen (see "Packaging note").
- Real-window proofs (`scripts/live-proof.sh` + `ALLEGIANCE_TRAIN_MISSION=1` / `ALLEGIANCE_TRAINING_OVERLAY=17`,
  `docs/reports/P785_TRAINING_COMPLETION_assets/`):
  - `slideshow_tm1.png` — the tm_1_introduction slideshow live (slide 1/15, native narration text, "any key: next").
  - `training_callout_classic.png` / `training_callout_modern.png` — the roll cue (`PUSH Q AND E TO ROLL LEFT AND RIGHT`
    / `PUSH V TO TOGGLE MOUSE CONTROL`) with the red angular pointer + hilite dot in **both** HUD styles.

## Packaging note (needs Domingo's review at merge)

`packaging/artwork-roots.txt` / `artwork-manifest.txt` were regenerated (`generate-manifest.py`, deterministic): the new
code names native training artwork files that exist in the pinned corpus, so the player bundle now ships
`tm_1_introduction.mdl` + its five imported slide bitmaps (`tm_slide_backgroundbmp`, `tm_slide_circle_overlaybmp`,
`tm_loadout_backgroundbmp`, `tm_helpbmp`, `tm_teleportbmp`) — ~2.6 MB, all pre-existing public-corpus files, no new
media created. The `tm_slide_*.ogg` narration clips were already bundled ("keep all audio").

## Unfinished / notes

- The slideshow renders text stand-ins for native's full-screen slide images (the MDL images are loaded from the
  shipped artwork at runtime only in native; the port does not yet compose the MDL images — visual parity, allowed).
- Slide audio auto-advance requires the artwork corpus installed; without it the slide waits for a key (native
  behaves the same with an unplayable clip).
- The port's six training keys route through `UpdateTrainingKeys` before the other flight handlers; a physical key
  shared with a port action (End = RejectCommand) still triggers that action's normal handler, unlike native's
  single-TK translation — harmless today (RejectCommand is a training-disabled key) and noted for the next input wave.
- Mid-mission quit → `SetSkipPostSlideshow` (native `DoQuitMission`, `WinTrek.cpp:3559-3566`) is plumbed through
  `_trainingSkipPostSlideshow` but no in-flight quit path currently sets it (the port has no training-specific quit
  verb yet); completion always takes the post slideshow.
- The mission-2 goal graph itself is unchanged; "full native lesson completion through the real client" is proven to
  the launch/runtime/key-gate level here; scripted-input end-to-end lesson completion in a live window remains with
  the AutoProof wave (the headless suites already drive the full mission graph against the embedded host).
