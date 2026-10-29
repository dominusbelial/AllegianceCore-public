# P885 — Mission 4 (Enemy Engagement) gates on the native TrekKey ids

Mission 4's cloak and target-enemy gates move off the invented key ids 111/112 onto the native
TrekKey numbers, the same move P881a made for Mission 3. After this change every
`GetKeyCondition` in the ported training missions waits on the number the real client input gate
actually delivers. No commits, pushes or stashes; no remote machines; scratch in
`.scratch/p885/` (git-excluded). Native citations are relative to native `src/`; every line
number was re-grepped at write time.

## Ids moved (native cites)

| Constant | Was | Now | Native cite |
|---|---|---|---|
| `KeyTargetEnemy` (Mission 4's private) | `111` (Mission4.cs, invented) | `TrainingMissionIds.KeyTargetEnemy = 80` | `const TrekKey TK_TargetEnemy = 80` — `Igc/igc.h:303` |
| `KeyToggleCloak` (Mission 4's private) | `112` (Mission4.cs, invented) | `TrainingMissionIds.KeyToggleCloak = 94` (new) | `const TrekKey TK_ToggleCloak = 94` — `Igc/igc.h:317` |

- `src/Allegiance.ClientCore/Training/TrainingMissionIds.cs:37` now carries
  `KeyToggleCloak = 94` beside `KeyTargetEnemy = 80` (`:36`).
- `src/Allegiance.ClientCore/Training/Mission4.cs` — the private `KeyTargetEnemy = 111` /
  `KeyToggleCloak = 112` constants and their "picked id" comment block are deleted; the mission
  uses the shared ids. The two gates that consume them:
  - cloak: `new GetKeyCondition(Site, KeyToggleCloak)` — `Mission4.cs:525`, port of
    `new GetKeyCondition (TK_ToggleCloak)` (`training/mission4.cpp:707`);
  - target-enemy: `new Goal(new GetKeyCondition(Site, KeyTargetEnemy))` — `Mission4.cs:582`,
    port of `new Goal (new GetKeyCondition (TK_TargetEnemy))` (`training/mission4.cpp:798`).
- The port's native key table agrees: `{ 94,"TK_ToggleCloak" }` —
  `src/Allegiance.ClientCore.Game/Rendering/DefaultInputMap.cs:179`; `{ 80,"TK_TargetEnemy" }` —
  `DefaultInputMap.cs:173`.

### Other invented key ids

`grep 'const int Key' src/Allegiance.ClientCore/Training/` (excluding `TrainingMissionIds.cs`)
now returns **nothing**. Mission 4's 111/112 pair was the last invented mission-local key id;
Missions 2/3/5/6/8/10 already use only `TrainingMissionIds` constants (`KeyFireBooster = 50`,
`KeyTargetEnemy = 80`, etc.).

## Input-path proof (real client delivers 94 / 80)

The gate P881a wired is generic over every `TK_*`-tagged action, so the cloak key already
reaches the training gate — no new wiring was needed:

1. **Binding** — the catalog declares the actions with native TrekKey tags:
   `new("ToggleCloak", "WEAPONS", "Toggle cloak [TK_ToggleCloak]", ...)` —
   `InputActionCatalog.cs:126`; `new("TargetEnemy", ..., "[TK_TargetEnemy]", ...)` —
   `InputActionCatalog.cs:271`.
2. **Gate** — `TrainingInputGate` builds `NativeKeys` by matching those tags against
   `DefaultInputMap.TkNumber` (`TrainingInputGate.cs:12-15`); on each frame
   `Update` translates a fresh press of a bound action into `recordKey(nativeKey)`
   (`TrainingInputGate.cs:25-30`) — i.e. the G binding yields **94** and the F binding yields
   **80**, before any ordinary handler sees the key (native filter,
   `WinTrek/WinTrek.cpp:8753-8769`).
3. **Session pump** — `Game1` constructs the gate over the training session
   (`_trainingInputGate = new TrainingInputGate(session.RecordKeyPress)`,
   `Game1.Training.cs:156`) inside `UpdateTrainingKeys` (`Game1.Training.cs:504`).
4. **Mission key gate** — `TrainingMission.RecordKeyPress` (`TrainingMission.cs:133`) applies the
   native disabled/command-view filters (port of `trainingmission.cpp:342-413`; 94 and 80 are in
   neither list) and forwards to the site (`TrainingMission.cs:195`), port of the native default
   branch that hands the key to the mission's key conditions.
5. **Site queue** — `ClientTrainingSite.RecordKeyPress` enqueues the number
   (`ClientTrainingSite.cs:354`); `GetKeyCondition.Evaluate` consumes it
   (`SiteConditions.cs:31`, port of `getkeycondition.cpp:47-52`) — so the goal completes only
   when the delivered number equals the gated one. With 111/112 the numbers never matched and
   the gates could never complete in a real client.

## Tests

- **New Fast (`tests/Allegiance.ClientCore.Tests/P885Mission4NativeKeysTests.cs`)** — three
  phased `FakeTrainingSite` drivers (modeled on `TrainingMission2Tests`):
  - `Mission4_cloak_and_target_enemy_goals_complete_on_the_native_trek_keys` — the cloak goal
    completes on 94 (mission reaches goal 7) and the target-enemy goal on 80 (mission reaches
    goal 8);
  - `Mission4_cloak_goal_does_NOT_complete_on_the_retired_111_112_ids` — pressing 111 and 112 at
    the cloak prompt leaves the mission in goal 6 (then 94 completes it);
  - `Mission4_target_enemy_goal_does_NOT_complete_on_the_retired_111_112_ids` — same for the
    goal-7 gate (then 80 completes it).
- **MonoGame gate test (Mission 4 equivalent of P881a's Mission 3 key test)** —
  `P881aTrainingMissionsTests.Mission4_cloak_and_target_enemy_bindings_deliver_the_native_trek_keys`:
  the production `TrainingInputGate` + `ActionMap` route the real `ToggleCloak`/`TargetEnemy`
  bindings through `Mission4.RecordKeyPress` into the `ClientTrainingSite` key queue as 94 and
  80, and 111/112 are never delivered.
- **Existing Live driver** — `TR3TrainingMissions34Tests` drops `Mission4KeyTargetEnemy` and the
  local `KeyToggleCloak`; the Mission 4 headless completion driver now presses
  `TrainingMissionIds.KeyToggleCloak` (94) and `KeyTargetEnemy` (80).

## Parity matrix

- `TRN-0117` (M4 lesson 06 — cloak) and `TRN-0118` (M4 lesson 07 — engage miner): port cites now
  name the native ids (94/igc.h:317 and 80/igc.h:303) and the real `TrainingInputGate` delivery;
  evidence points at the new Fast + gate tests; `queue=P885`. Both rows stay `partial` for their
  remaining live-model stubs (cloak signature, command-target bindings).
- `python3 scripts/parity-scorecard.py` passes (241 training rows validated).

## Gate results

| Gate | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | all 93 tool projects built, 0 warnings |
| New Fast tests ×3 alone (`P885Mission4NativeKeysTests`) | 3/3 runs, 3 passed each |
| New MonoGame gate test ×3 alone | 3/3 runs, passed each |
| `tools/run-training-tests.sh --no-build` (Fast + Live, both client projects) | **PASS** — ClientCore.Tests 75/75, MonoGame.Tests 58/58 (includes Mission 4 Live completion on the native ids) |
| `tools/run-tests.sh --tier fast` (one sweep) | 2 failures + 1 limit offender, **all pre-existing and ledgered** in `docs/TEST_FLAKES.md` (§ 2026-10-29 P885 gate): `ShopLauncherMountNumberingLiveTests…` (3/3 pass alone), `NpcWingmanDefendLiveTests.Defender_disengages…` (known deterministic base failure, 3/3 fail alone — P885 touches no wingman path), `P750NativeAsteroidTypesTests…` SLOW FAST 12.4 s (test passed; 3/3 alone in ~1 s) |

## Native intent

**Classify: port defect (fixed).** Native Mission 4 gates its cloak lesson on
`GetKeyCondition(TK_ToggleCloak)` (mission4.cpp:707) and the miner engagement on
`GetKeyCondition(TK_TargetEnemy)` (mission4.cpp:798); both are ordinary `TrekKey` constants
defined in the native key table (`igc.h:303,317`). The port had invented local ids 111/112 that
no real key press could ever produce, so both gates were dead code on the real client input path
— a port defect, not a native behaviour difference. Behaviour parity with native is restored:
the numbers the client's `TrainingInputGate` delivers (94, 80) are now exactly the numbers the
lesson goals wait on. No native rule changed; only the port's id plumbing did. Mission 4's
remaining live-model stubs (cloak signature, command-target bindings, TRN-0220 loadout) are
unchanged and stay queued on their rows.
