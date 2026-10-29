# P882b — training regressions and the Fast + Live training gate

Dispatch base: `a243fa4b`. Lane: Codex; changes left in the working tree, with no commits or pushes. All
execution stayed on the local workstation. Raw logs and TRX are git-excluded
under `.scratch/p882b/` and the training runner's `.scratch/training-*` directory.
Client configuration/logs use scratch `XDG_CONFIG_HOME`. No real-window proof
was required for this host/test change; verification unsets desktop display
variables. No rig, mini or Mac was contacted.

## Causes, native classification and fixes

Both reported failures reproduced on the unmodified dispatch tree (baseline
solution build: 0 warnings / 0 errors). Baseline evidence:
`.scratch/p882b/baseline/baseline.trx` and `baseline.log`.

| Test | Cause and classification | Native rule and fix |
|---|---|---|
| `TR3TrainingMissions34Tests.Mission3_RunsToCompletion_AgainstTheRealEmbeddedHost` | **Stale headless driver**, exposed by P882a's real default loadout. The driver asserted both weapon and missile fire bits before every simulation tick, including the opening narrative. Real guns destroyed derelict 1000 before the aiming demonstration; the graph stalled at **5.3**, long before either later SPACE-key prompt. It was not key interception. | `training/mission3.cpp:291-298` masks fire during the introduction; `training/mission3.cpp:617-649,838-848,927-951` asks the player to fire at the main-gun and missile prompts; `training/trainingmission.cpp:388-410` allows SPACE (`TK_FireBooster=50`, `Igc/igc.h:273`) through the default key-condition branch. The driver now samples one fire action at each matching prompt for graph evaluation, clears it before the next simulation step, and keeps its existing separately scripted kill outcomes. It verifies both main-gun prompts, the missile prompt, target survival through aiming, and acceptance of every SPACE press. All original SPACE/kill/landing/completion assertions remain. |
| `TR4TrainingMissions56Tests.Mission5_RunsToCompletion_AgainstTheRealEmbeddedHost` | **Real production membership regression exposed by P882a**, not a stale assumption about launch. Diagnostic output identified **enemy scout 3001**, at graph **4.3**, as the ship trying to fire without a sector. `CreateDrone` initially inserted it in the map's first sector; `PlaceDrone` inserted it in the authored sector without removing the first membership. Destruction removed only the authored membership and cleared `Sector`, leaving an armed ghost in the first sector's update list. | `training/CreateDroneAction.cpp:75-80` calls `SetCluster`; `Igc/shipIGC.h:215-227` deletes old cluster membership before adding the new cluster. `TrainingHost.PlaceDrone` now removes old sector membership before placement. `training/mission5.cpp:84-106` deliberately leaves the trainee docked at station 1050 in command view; the test now asserts station 1050 at both entry and completion. It does not launch the trainee. |

`P882bTrainingRegressionTests.Placed_armed_drone_moves_between_sectors_once_and_termination_removes_it_everywhere`
is a Live, artwork-backed regression proof. It creates a drone with real weapons,
moves it through multiple sectors (including repeated placement in one sector),
asserts exactly one global sector membership each time, destroys it, asserts no
remaining membership/presence, and advances the real mission with an armed weapon.
The trainee remains docked. `TRN-0010` tracks the native transfer rule and this
proof; its broader **partial** status and outstanding loadout/recipe gaps remain.

## Training gate

`tools/run-training-tests.sh [--no-build] [--repeat N]` runs both client test
projects serially with the existing sequential runsettings. Its selection includes
all training classes, `P880aFrameworkBindingTests`, and the two training crash
guards. There is no Tier filter: Fast and Live cases both execute, including M8.
A failing project does not suppress the other project or subsequent repetitions;
the final exit code remains nonzero. Every run writes a log and TRX to scratch.

CI now runs this gate after its normal Fast sweep; artwork-dependent cases skip
on clean hosted runners. `AGENTS.md`, `docs/CI_TEST_GATES.md` and `GLOSSARY.md`
record the corpus-equipped three-pass requirement for training changes. This
avoids treating a Fast-only sweep as training certification. No lesson was moved
out of Live or exempted from a failure.

## Verification

- Final solution build: **0 warnings / 0 errors** (`.scratch/p882b/build-final.log`).
- `tools/build-tools.sh`: **all 93 projects built**, zero warnings/errors.
- New Live sector-transfer regression: **3/3 isolated passes**, in addition to the cohort.
- Full training cohort: **125 cases × 3 = 375 passes**, zero failures/skips. Discovery
  confirms **107 Fast + 18 Live** cases per pass, with every selected theory row
  present. Detailed individual outcomes: [P882b_training_results.csv](P882b_training_results.csv).
- Raw cohort evidence: `.scratch/training-20261029T034808-6499/{1,2,3}/`.

Each table cell below is **passed / failed / skipped**; theory rows count separately.

| Test class | Run 1 | Run 2 | Run 3 |
|---|---:|---:|---:|
| ClientCore `TR2TrainingTests` | 12 / 0 / 0 | 12 / 0 / 0 | 12 / 0 / 0 |
| ClientCore `TrainingHostMission2Tests` (TR2B) | 2 / 0 / 0 | 2 / 0 / 0 | 2 / 0 / 0 |
| ClientCore `TR3TrainingMissions34Tests` | 3 / 0 / 0 | 3 / 0 / 0 | 3 / 0 / 0 |
| ClientCore `TR4TrainingMissions56Tests` | 4 / 0 / 0 | 4 / 0 / 0 | 4 / 0 / 0 |
| ClientCore `TR5TrainingMissions810Tests` | 3 / 0 / 0 | 3 / 0 / 0 | 3 / 0 / 0 |
| ClientCore `P785TrainingCompletionTests` | 14 / 0 / 0 | 14 / 0 / 0 | 14 / 0 / 0 |
| ClientCore `P880aFrameworkBindingTests` | 12 / 0 / 0 | 12 / 0 / 0 | 12 / 0 / 0 |
| ClientCore `P882bTrainingRegressionTests` | 1 / 0 / 0 | 1 / 0 / 0 | 1 / 0 / 0 |
| ClientCore `TrainingFrameworkTests` | 11 / 0 / 0 | 11 / 0 / 0 | 11 / 0 / 0 |
| ClientCore `TrainingControlMaskTests` | 9 / 0 / 0 | 9 / 0 / 0 | 9 / 0 / 0 |
| ClientCore `TrainingMission2Tests` | 4 / 0 / 0 | 4 / 0 / 0 | 4 / 0 / 0 |
| MonoGame `P785TrainingHandlerTests` | 5 / 0 / 0 | 5 / 0 / 0 | 5 / 0 / 0 |
| MonoGame `P873TrainingJoinTests` | 13 / 0 / 0 | 13 / 0 / 0 | 13 / 0 / 0 |
| MonoGame `P882aTrainingIntegrationTests` (both HUD styles) | 30 / 0 / 0 | 30 / 0 / 0 | 30 / 0 / 0 |
| MonoGame `P749CrashSafetyTests` (two training guards) | 2 / 0 / 0 | 2 / 0 / 0 | 2 / 0 / 0 |
| **Total** | **125 / 0 / 0** | **125 / 0 / 0** | **125 / 0 / 0** |

Exactly **one** Fast sweep ran: **11,616 cases; 11,610 passed / 3 failed /
3 optional skips**, 480.6 seconds. Infrastructure and quarantine checks passed.
The gate remains **red**. Its sole timing violation was the station missile
failure (16.653 s against 10 s). Evidence: `.scratch/p882b/fast/summary.json`.

| Fast failure | Observation / disposition |
|---|---|
| `StationCombatLiveTests.Cross_side_missile_hits_station_shield_exact_fraction_armor_untouched` | Missile did not hit the station (`StationCombatLiveTests.cs:278`); also exceeded the Fast duration limit. **3/3 standalone follow-up passes** (0.397 / 0.402 / 0.390 s). Broad-run timing observation; this test never uses the changed training-host placement path. No station/missile code or threshold changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Null `DefendTarget` at `NpcWingmanDefendLiveTests.cs:179`, matching the existing deterministic dispatch-base failure ledgered under P872 and observed by P882a. No defend code changed. |
| `BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over` | Empty ballot receipt collection at `BallotVotingLiveTests.cs:602`; draw-chain receipt ordering has historical ledger coverage. Standalone follow-ups **pass / pass / fail**, with the same line-602 failure on the third run. Confirmed intermittent in isolation; no ballot code changed and this path never invokes training-host placement. The failure remains open. |

Standalone station/ballot evidence: `.scratch/p882b/follow-up-{1,2,3}/follow-up.trx`.
The existing wingman defend failure was not repeatedly retried; the ledger already
records its deterministic dispatch-base reproduction. No Fast sweep was rerun.

The required unchanged `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
passed **5/5**, serially. Each run reached 24/30 objectives and observed real battle
hits; no synthetic load was generated.

| Run | Outcome | Test duration (s) | Longest speed < 1 with target (s) |
|---|---|---:|---:|
| 1 | Pass | 218.981 | 0.000 |
| 2 | Pass | 236.491 | 1.503 |
| 3 | Pass | 344.331 | 1.517 |
| 4 | Pass | 210.615 | 1.008 |
| 5 | Pass | 367.307 | 1.109 |

Raw showcase evidence: `.scratch/p882b/showcase-{1,2,3,4,5}/showcase.trx` and
matching `showcase-N.log` files.

**Known Nanite failure:** `TR5TrainingMissions810Tests.Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost`
passed **3/3** in the final cohort (0.374 / 0.368 / 0.229 s; see TRX for exact
values). Its test and lesson graph were not edited. The historical **“bomber
never crossed into sector 1052” failure did not reproduce**, including in an
isolated run against the original dispatch-base host (**1/1 pass**, 0.372 s).
It is not evidence that P882b fixed the historical Nanite gap.

For that comparison, an isolated scratch copy of `Allegiance.SinglePlayer` was
compiled with the exact `HEAD` version of `TrainingHost.cs` and run in a separate
copy of the test runtime. Other SinglePlayer source/dependency code is unchanged
by this lane. A control run of M5 against that runtime reproduced the original
sectorless-fire exception (its stack points into the scratch baseline host),
confirming the original host was loaded. Working-tree source and final build
outputs were never swapped or reverted. Evidence: `.scratch/p882b/nanite-baseline/`
and `.scratch/p882b/baseline-control/`. This does not reclassify the earlier P873
result as a flake or certify interactive Nanite completion.

Additional checks passed: native training audit (**241 rules / 82 primitive
families / zero errors**), parity scorecard validation, glossary links (**304
paths**), Compose configuration, shell syntax, CI workflow YAML, working-tree
secret/private-path/media scan and whitespace checks. No media was added. No
commit-range publish-guard result is claimed because this lane makes no commits.

## Native intent

**Design:** preserve native command training from the station, unique cluster
membership, authored prompt/input order, both SPACE acknowledgement pairs, all
three derelict kills and landing termination. The production key switch and lesson
graphs are unchanged. Headless mission completion still uses its established
scripted combat outcomes; it is not a claim of interactive live lesson completion
or full training parity. Existing partial matrix rows retain their limitations.

**Era constraints:** native in-process `SetCluster` becomes removal and insertion
in the embedded host's managed sector lists. The tests sample client action state
at graph evaluation separately from the authoritative simulation tick; real weapon
and lifecycle behavior remain covered by the production-binding integration tests.
No service, store, port, environment variable or wire protocol was added.

**Native bugs:** none changed or newly waived. The armed ghost is a port defect;
unconditional pre-prompt fire is a test-driver defect. Existing native training
quirks and the previously classified lifecycle deviations are outside this fix.
