# P909 — real training site bindings

Training parity increases from **384/904 (42.48%) to 481/904 (53.21%)**, adding 97 production points across 24 rows. All 34 assigned rows were audited; five remain partial with specific gaps below. Five implemented primitives have no native executable lesson caller and receive no production credit. No other parity rows changed.

Changes remain in the working tree. No commits, pushes, stashes, remote-host access or synthetic load. Artwork was configured through the existing `local.env`. Raw logs, TRX and build output are retained under `.scratch/p909/`; this sandbox permits worktree writes, so evidence was kept there rather than outside the worktree.

## Scorecard

| Training metric | Before | After |
|---|---|---|
| Framework weight | 130/298 (43.62%) | 227/298 (76.17%) |
| All training weight | 384/904 (42.48%) | 481/904 (53.21%) |
| Completed eligible production rows | 102/237 | 126/237 |

The 34 assigned rows contain 136 points: 97 now earn production credit, 23 remain
partial, and 16 are implemented but native-unreachable/test-only. Changing
reachability does not remove their weight from the denominator.

## What changed

The production client site now reads the real command selection, rendered sector, own simulator pose/velocity and camera/autopilot state. Native camera ids map to all existing camera modes; pane flags use shared console state; eyeball actions change the actual own-ship status. Weapons use the ordinary client selection/equipment paths. A training logon supplies both mounted parts and the separate cargo hold in its ordinary loadout message, rather than sending an empty list for a ship already equipped on the server.

Key conditions have independent native boolean latches: every started condition sees a matching press, Start clears stale input, Evaluate consumes only its own latch, and held input does not generate another edge. The existing general TrainingInputGate supplies TrekKey events through the normal rebindable input map.

Model resolution uses the live mission registries, including un-authored buoys and their subsequent movement/deletion, independent of remote replication interest, with real ship/station/probe/buoy radii and real damage fractions. Scanner queries call the existing native visibility math, including signature, side resistance, effective range, flag bypass, sector identity and asteroid occlusion. Docked/boarded ships are excluded. Commander messages enter ordinary ReceiveChat with individual attribution.

Drone recipes apply weapon/nanite/booster/missile/mine substitutions, real station/expendable base data, source-ship placement/orientation, accepted model commands and extra recipe buoy holds. Practice Arena and Free Flight now use their actual native carrier-relative recipes; Command View tower layers receive expendable type 87. CreateProbe creates a real launcherless simulation model and replicates it to the local trainee.

Native acknowledgement differs from the brief's suggested binding: `WinTrek/WinTrek.cpp:986` sets CommandAcknowledgedCondition from a selected **quick-chat leaf**, and `training/trainingmission.cpp:161` resets it at lesson start. Ordinary queued-order acceptance is not that native event. The port follows the native producer and has a real Game1 quick-chat test.

## Evidence key

Every T test below uses a real embedded TrainingHost, loopback ConnectionSession, production ClientTrainingSite and Game1.CreateTrainingFacilities. Both Classic and Modern theory cases run. Graphics-independent tests construct Game1 without a graphics device and supply its actual required state, including the normal part-type resolver; there is no fake training site. The sound wait test uses the production no-audio-device/stopped-handle path; it does not claim an acoustic playback measurement.

- **T1**: `Production_facilities_read_client_view_pick_and_apply_camera_panes_and_weapon_selection` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:62`.
- **T2**: `Real_models_supply_geometry_damage_scanner_and_both_command_slots` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:108`.
- **T3**: `Drone_recipes_apply_upgrades_spawn_orientation_fallback_and_accepted_buoy_command` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:149`.
- **T4**: `Key_gate_commander_messages_quick_chat_ack_and_eliminated_target_use_real_client` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:197`.
- **T5**: `Respawn_modifications_and_geometry_use_native_radii_and_live_identity` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:245`.
- **T6**: `Suspended_sound_uses_real_site_playback_handles_and_wait_list` in `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs:286`.

## Assigned row verdicts

“Implemented / test-only” means executable primitive and real-site proof, with no headline production credit. “Partial” identifies the remaining behavior, not merely an absent certification label.

| Row / native item | Verdict | Native source | Port source | Real-site test | Result / exact remaining gap |
|---|---|---|---|---|---|
| TRN-0004 AddRespawnAction | Implemented | `training/AddRespawnAction.cpp:31-34` | `src/Allegiance.ClientCore/Training/RespawnAndEscalationActions.cs:19` | T5 | Registration and respawn processing now execute real site recipes, including live replacement identity and modified tech/name. |
| TRN-0006 CommandAcknowledgedCondition | Implemented | `training/CommandAcknowledgedCondition.cpp:37-46; WinTrek/WinTrek.cpp:986; training/trainingmission.cpp:161` | `src/Allegiance.ClientCore.Game/Game1.Session.cs:1804` | T4 | Reads the client quick-chat selection latch. Native WinTrek.cpp:986 sets it from the chosen quick-chat command; ordinary queued-order acceptance is a different event. |
| TRN-0010 CreateDroneAction | Partial | `training/CreateDroneAction.cpp:60-210` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:51` | T3 | Real default equipment, upgrades, base data, carrier-relative placement and accepted targets are bound. Remaining: native SaveLoadout cache persistence is absent; buoy command holds are not retired on every server-driven slot change/death. |
| TRN-0012 CreateProbeAction | Implemented / test-only | `training/CreateProbeAction.cpp:38-48` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:583` | T3 | Creates and replicates a real launcherless Probe with native side/type/position/time. No native lesson instantiates CreateProbeAction; includes and definitions only. Keep test-only; no invented lesson caller. |
| TRN-0013 CreateWaypointAction | Partial | `training/CreateWaypointAction.cpp:37-47` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:572` | T3 | Creates a mission-owned buoy and client command geometry, with native visible/hidden radii. Remaining: command-buoy visibility/decals are not rendered as native GetVisibleF requires, and retirement is incomplete outside host-issued commands. |
| TRN-0022 GetCommandCondition | Implemented | `training/GetCommandCondition.cpp:43-54` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:908` | T2 | Reads independent accepted OR current live command slots, including absent-model success. |
| TRN-0024 GetKeyCondition | Implemented | `training/GetKeyCondition.cpp:39-58` | `src/Allegiance.ClientCore/Training/SiteConditions.cs:22` | T4 | General rebindable TrainingInputGate reaches independent per-condition boolean latches; Start resets stale presses, held keys do not repeat. |
| TRN-0025 GetPickedCondition | Implemented | `training/GetPickedCondition.cpp:35-38` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:235` | T1 | Reads the actual command selection used by the mouse/rectangle path; host diagnostic SetPicked cannot override the result. |
| TRN-0032 GetShipIsDamagedCondition | Implemented | `training/GetShipIsDamagedCondition.cpp:49-59` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:936` | T2 | Reads live ship/station/asteroid/probe/mine fractions. Diagnostic damage tables do not override repair or retained-zero state. |
| TRN-0033 GetShipIsDestroyedCondition | Implemented | `training/GetShipIsDestroyedCondition.h:26-28` | `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs:249` | T2 | Missing model and zero real fraction satisfy destruction; cached eliminated targets remain observable. |
| TRN-0037 GetViewSectorCondition | Implemented | `training/GetViewSectorCondition.cpp:32-34` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:237` | T1 | Reads RenderSector, the same active cluster used by the command-camera renderer, including acknowledged remote previews. |
| TRN-0041 MessageAction | Implemented | `training/messageaction.cpp:45-52` | `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs:496` | T4 | Uses ordinary ReceiveChat with the real commander sender, individual channel and trainee recipient; paired sound playback remains the normal client facility. |
| TRN-0042 ModifyCreateDroneActionAction | Implemented | `training/ModifyCreateDroneActionAction.cpp:57-74` | `src/Allegiance.ClientCore/Training/RespawnAndEscalationActions.cs:47` | T5 | Sentinel-filtered recipe mutation now reaches real subsequent equipment/name/placement on respawn. |
| TRN-0046 ObjectMovingTowardsCondition | Implemented | `training/objectmovingtowardscondition.cpp:35-66` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:224` | T5 | Geometry uses actual hull/model radii and the own simulator position/orientation/velocity; moving-toward test uses a real ship and buoy. |
| TRN-0047 ObjectPointingAtCondition | Implemented | `training/objectpointingatcondition.cpp:56-99` | `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs:212` | T5 | Pointing predicate resolves live model radii and own-simulator pose rather than a fixed ship radius. |
| TRN-0048 ObjectWithinRadiusCondition | Implemented | `training/objectwithinradiuscondition.cpp:65-113` | `src/Allegiance.ClientCore/Training/GeometricConditions.cs:17` | T5 | Lazy once-initialized radius adds both live radii; real-client test distinguishes thresholds just below and above their sum. |
| TRN-0061 SetAutopilotAction | Implemented | `training/SetAutopilotAction.cpp:42-45` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:301` | T1 | Real client autopilot setter/getter and host mirror include the native targetless latch. Disabling clears the navigation plan while preserving accepted/current commands (Igc/shipIGC.cpp:3974-3981). |
| TRN-0062 SetCargoNanAction | Implemented / test-only | `training/SetCargoNanAction.cpp:34-106` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:1204` | T5 | Corrected native front-weapon preservation, nanite/gatling cargo counterpart, dispenser and mine cargo recipe. No native executable mission instantiates this class (mission8 includes its header only). |
| TRN-0063 SetCommandAction | Partial | `training/setcommandaction.cpp:71-95` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:671` | T3 | Real accepted/current slots and all eight native model target kinds, queued trainee order UI, and host-side buoy counts are bound. Remaining: non-player queued slots are not written, and direct server command changes/ship retirement bypass host consumer bookkeeping. |
| TRN-0066 SetDisplayModeAction | Implemented | `training/setdisplaymodeaction.cpp:32-40` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:361` | T1 | All 14 native camera ids map to real shared Game1 camera state; command TD/34 selects command view and top-down state. Command displacement is always enabled in the existing CommandCamera. |
| TRN-0071 ShipSeesObjectCondition | Implemented | `training/ShipSeesObjectCondition.cpp:50-55; Igc/shipIGC.h:341-369` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:1294` | T2 | Calls shared native visibility math with effective scanner range, target signature/resistance, real radii, sectors, flag bypass and asteroid LOS; excludes docked/boarded scanners. |
| TRN-0072 ShowEyeballAction | Implemented | `training/ShowEyeballAction.cpp:32-37` | `src/Allegiance.ClientCore/State/ClientWorld.Orders.cs:24` | T1 | Writes the actual replicated own status bit until the next status update, exactly like native ShowEyeballAction; both HUDs read that status. |
| TRN-0073 ShowPaneAction | Implemented | `training/ShowPaneAction.cpp:32-43` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:338` | T1 | All native tutorial pane flags clear siblings and apply the request, including command-pane drawing and hit testing in the shared command HUD. |
| TRN-0075 SuspendedPlaySoundAction | Implemented / test-only | `training/SuspendedPlaySoundAction.cpp:40-47` | `src/Allegiance.ClientCore/Training/AdditionalPrimitives.cs:30` | T6 | Concrete action registers its sound-finished wait before playback and ignores NA. No native live lesson instantiates it; native ResetAction Execute is commented out. |
| TRN-0076 SuspendedSoundFinishedCondition | Implemented / test-only | `training/SuspendedSoundFinishedCondition.cpp:21-31` | `src/Allegiance.ClientCore/Training/AdditionalPrimitives.cs:23` | T6 | Concrete wait retains the played action through managed ownership. Native callers are the unused suspended action and ResetAction.PlaySound, whose Execute caller is commented out; test-only. |
| TRN-0077 SwapWeaponAction | Implemented | `training/SwapWeaponAction.cpp:33-35` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:389` | T1 | Real Game1 swap path maps fixed and turret-relative mounts and all swappable equipment kinds. The loopback test swaps a real cargo weapon and observes both server mutation and the client echo. Training join exports cargo as well as mounted parts. |
| TRN-0078 ToggleWeaponAction | Implemented | `training/ToggleWeaponAction.cpp:32-49` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:240` | T1 | Uses the real ToggleFixedWeapon path; switching to another occupied fixed mount selects single fire and pressing it again groups, shared by both HUDs. |
| TRN-0080 TurnShipAboutCondition | Implemented / test-only | `training/TurnShipAboutCondition.cpp:33-47` | `src/Allegiance.ClientCore/Training/AdditionalPrimitives.cs:14` | T5 | Concrete strict greater-than-ten-degree predicate captures Start forward from a live model. Native lesson files include its header but instantiate no predicate; no invented production caller. |
| TRN-0081 TurnToAction | Implemented | `training/TurnToAction.cpp:55-71` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:19` | T5 | Own ships use local simulator kinematics and pose; other source ships use their own real mass, torque, speed and turn rates. Non-ship targets resolve. Existing TurnToAction applies native half damping or clears overrides when absent. |
| TRN-0082 TypeIDTarget | Implemented | `training/TypeIDTarget.cpp:33-36` | `src/Allegiance.SinglePlayer/Training/TrainingHostSite.cs:46` | T5 | Reads live mission registries for ship/station/warp/asteroid/probe/mine/treasure/buoy, preserving lazy identity across destruction and respawn; own ship retains local simulator pose. |
| TRN-0083 CreateDrone spawn at ship | Implemented | `training/CreateDroneAction.cpp:65-81` | `src/Allegiance.ClientCore/Training/CreationActions.cs:172` | T3 | Native spawn-at-live-ship cluster/offset/orientation and missing-or-docked-source fallback are implemented, with real Mission6/10 carrier-relative production recipes. |
| TRN-0084 CreateDrone tech loadouts | Partial | `training/CreateDroneAction.cpp:91-192` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:51` | T3 | Weapon/nanite/booster/missile/proximity-mine substitutions now apply to real equipped and cargo parts. Remaining: native SaveLoadout persistence is not mirrored; level below -3 hits native uninitialized pprox and is explicitly rejected when mine-capable. |
| TRN-0085 CreateDrone accepted command | Partial | `training/CreateDroneAction.cpp:201-207` | `src/Allegiance.ClientCore/Training/CreationActions.cs:32` | T3 | Creation targets now set the accepted slot with the real model and add the extra native buoy recipe hold. Remaining: native ship-slot consumers on death or server-issued replacement are not centrally retired; host bookkeeping covers host-issued writes only. |
| TRN-0086 CurrentTarget eliminated cache | Implemented | `training/CurrentTarget.cpp:46-71` | `src/Allegiance.ClientCore/Training/CombatConditionsAndActions.cs:15` | T4 | Caches the initial invalid target as well as valid targets; switching live selections reanchors, while an eliminated cached target is preserved for destruction checks. |

## Native caller audit

`rg` over the native training implementation finds no construction of `CreateProbeAction`, `SetCargoNanAction`, `SuspendedPlaySoundAction`, or `TurnShipAboutCondition`. Some lessons include the headers; includes are not executable callers. SuspendedSoundFinishedCondition is constructed by SuspendedPlaySoundAction and `ResetAction::PlaySound` (`training/resetaction.cpp:98-109`), but the former has no lesson caller and ResetAction's Execute body is commented out (`training/resetaction.cpp:43-88`). These five rows remain test-only. Adding lesson steps would change native behavior and was deliberately avoided.

Native source was reread and line numbers rechecked for all assigned classes. `tools/p873-training-audit.py` validates the full inventory. Only the 34 requested matrix rows were edited.

## Screenshots

Inspected real offscreen Game1 backbuffers, both at the same Basic Flight milestone:

- [Classic](P909_assets/classic.png): cockpit label, circular native gauge artwork, Commander marker, base and asteroids, the equipped PW Mini-Gun 3 weapon strip and the real five-slot cargo hold.
- [Modern](P909_assets/modern.png): the same cockpit scene and markers, Modern numeric armor/shield/thrust gauges, HUD Tactical / RADAR All labels, cargo summary and weapon strip.

The Command View captures add [Classic](P909_assets/command-classic.png) and [Modern](P909_assets/command-modern.png). Both visibly show the command grid, station/asteroids and shared command-button panel. Classic also shows the real five-slot cargo strip. Both retain the existing lower-left CHASE caption fallback; the actual command projection and command camera state are active, as the capture names and tests confirm.

These show the actual client and shared camera/equipment state. They do not certify every pane combination, command-buoy visibility or a sustained eyeball override. No layout-coordinate conversion changed. The four PNGs total less than 1 MB; media still needs the owner's normal review before any future push.

Commands used the existing local artwork configuration, `SDL_VIDEODRIVER=offscreen`, per-style `ALLEGIANCE_HUD_SETTINGS` and `XDG_CONFIG_HOME` in task scratch, `ALLEGIANCE_TRAINING_START=2`, and `--autoshots 7 --exit-after 12`. Raw capture logs are `.scratch/p909/classic/current-capture.log`, `.scratch/p909/modern/current-capture.log` and `.scratch/p909/command-{classic,modern}/capture.log`; Command View uses `ALLEGIANCE_TRAINING_START=5`.

## Validation

- Solution build: **0 warnings, 0 errors** (`.scratch/p909/build-final.log`).
- All 104 tool projects built (`.scratch/p909/build-tools-verified.log`).
- Server linux-x64 self-contained NativeAOT publish passed with no IL2026/IL3050 (`.scratch/p909/aot-final.log`).
- New real-site suite: **12 passed, 0 skipped, 3/3 alone** (`.scratch/p909/tests/p909-final-{1,2,3}.trx`).
- All headless training tests, including the affected M3/M4/M5/M6/M8/M10/framework cohort: **80 passed, 0 skipped, 3/3 alone** (`.scratch/p909/tests/affected-final-{1,2,3}.trx`).
- Full Fast + Live training gate, three repeats: **3/3 passed: 80 headless + 79 client cases per repetition, 477 passed executions, 0 failed, 0 skipped** (`.scratch/p909/training-final-verified.log`, raw results in `.scratch/p909/training-final-evidence/`). The intermediate attempt in `.scratch/p909/training-final.log` had two failures in its third repetition: the new wire-swap fixture omitted the normal Game1 part resolver. The fixture now supplies that real resolver; raw results are in `.scratch/p909/training-intermediate-evidence/`. The failed attempt is retained as evidence and is not counted as a passing gate.
- One Fast-tier sweep: **12,826 discovered cases, 577.4 seconds, FAIL**, with 3 skipped cases and the known non-task failures/timing overruns ledgered in `docs/TEST_FLAKES.md` (`.scratch/p909/fast/summary.json`). Infrastructure checks passed. No second full Fast sweep was run.
- Showcase never-parks five-run gate: **5/5 passed, 0 skipped** (`.scratch/p909/showcase-{1..5}.log`). No performance result or remote rig claim.
- Native inventory audit: **241 rules, 82 primitive types/macros, 0 errors** (`.scratch/p909/native-audit.log`). Glossary links and `git diff --check` pass. Exactly the assigned 34 matrix rows changed; CSV remains LF and the added source passes the public-hygiene scan (`.scratch/p909/hygiene.json`).
- Machine-readable results: [validation summary](P909_assets/validation-summary.json). Raw per-test counters are retained in `.scratch/p909/validation-summary.json`.
- Scorecards: `.scratch/p909/scorecard-before.txt` and `.scratch/p909/scorecard-after.txt`. Other headline areas are unchanged.

The first training sweep exposed four task regressions in legacy helpers: command tests wrote only diagnostic ids, and Nanite's destroyed-outpost fixture changed a diagnostic fraction without removing a real station. The helpers now write real slots; the fixture damages/removes the real outpost, with a damage type admitted by its actual defense matrix. All 80 headless training cases and all 12 real-site cases passed three times after the fixes. The initial failed sweep remains recorded in `.scratch/p909/training-gate.log` with raw results in `.scratch/p909/training-attempt-evidence/`; it is not counted as a successful gate.

The required Fast sweep retained seven failing executions: profile restoration on a read-only config path, multiplayer death-latch timing, floating-point timer boundary, baseline NPC defend target, miner progress, a docking status assertion, and a quarantined side-elimination hit. The quarantine retry passed. Three executions exceeded the calibrated 10-second Fast limit. Isolation results:

| Non-task case | Three-run isolation |
|---|---|
| Profile restore | 0/3 at the original read-only path; 3/3 with a writable task-local XDG config root |
| Multiplayer death latch | 3/3 |
| Timer delay cases | all three cases passed 3/3 |
| NPC defend target | 0/3; persistent baseline null target already ledgered under P872/P887/P895/P902/P905 |
| Miner contention | 3/3 |
| Docking status | pass / pass / fail; the assertion includes another ship's docked status |
| Warp-exit timing | 3/3; the original execution passed but exceeded 10 seconds |
| Side elimination | 3/3, in addition to the runner's passing quarantine retry |

None of these Core simulation or multiplayer policy paths was changed. New server behavior is guarded by LocalTraining. The original sweep remains a failure, not relabeled green. Raw isolation TRX files and logs are in `.scratch/p909/tests/`; `docs/TEST_FLAKES.md` records the exact results.

Late docked-source fallback and live buoy lookup edge corrections were additionally verified with all 12 real-site cases and all 80 headless training cases, each repeated three times on the final source. The long full-run evidence spans these corrections; earlier repetitions preceded those two edge fixes.

## Native intent

- **Design:** conditions observe the same selected models, camera, input and fractions that gameplay uses. A training diagnostic field is not a substitute for those values. Commands and spawn recipes retain real object identity.
- **Era constraint:** native training shares the client simulation in one process. This port's embedded server plus loopback mirror requires ordinary model/loadout replication while retaining local own-ship input authority. Managed references replace manual sound-action deletion; unused native classes remain unused.
- **Native bugs:** CreateDroneAction initializes `b_addedConsumer` false (`CreateDroneAction.cpp:42`) and tests it when adding a hold (`:206-207`) but never sets it true; its destructor release (`:50-55`) therefore never executes. The port preserves the per-execution extra recipe hold rather than silently claiming the flag is repaired. Below tech -3, native leaves `pprox` uninitialized (`:177-184`); the mine-capable port rejects that invalid input explicitly. Neither is a new modern lesson rule.
- **Modern alternative:** a centrally owned model lease and explicit typed recipe/save-loadout API would remove the remaining buoy lifetime and cache gaps. Those should preserve native externally visible behavior and need their own production proofs. A camera/console facility interface remains shared between both HUDs; lesson scripts should not synthesize input or duplicate UI state.
