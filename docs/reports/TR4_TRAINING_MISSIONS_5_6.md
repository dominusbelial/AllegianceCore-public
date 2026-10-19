# TR-4 — Training Missions 5 (Command View) and 6 (Practice Arena)

Branch: `master` (committed `0557ceaf`). Not pushed.

## Scope delivered

TR-1/TR-2/TR-3 ported the training framework, Mission 2 (Basic Flight), and Missions 3/4 (Basic
Weaponry / Enemy Engagement). TR-4 adds the next two native missions as data, enables them on the
training screen, ports the remaining condition/action seams each needs (with native file:line cites),
and proves each runs to completion against the embedded host through the real input path — plus the
real kill-credit wiring the Practice Arena's kill goals require.

1. **Mission 5 "Command View"** — `src/Allegiance.ClientCore/Training/Mission5.cs`, a faithful port of
   `src/training/mission5.cpp` (5 sub-goals, native line cites per goal).
2. **Mission 6 "Practice Arena"** — `src/Allegiance.ClientCore/Training/Mission6.cs`, a faithful port of
   `src/training/mission6.cpp` (respawn + escalating kill-goal waves, native line cites per wave).
3. **New seams** (each cited to native) + constant corrections.
4. **Real kill-credit wiring** for Mission 6: the server raises `KillCreditGranted` at the exact
   `!bLifepod`-gated credit point (Server.Combat), and the embedded host routes it to the mission's
   `ShipKilled` counter — so `KillsReachedCondition` reads the **real** kill credit, never a separate
   counter, and an ejection (excluded upstream by `!bLifepod`) can never inflate it.
5. **Enabled Missions 5 and 6 on the training screen** (others stay "coming soon").
6. **Proof per mission**: a headless completion test against the embedded host through the real input
   path (`TR4TrainingMissions56Tests.cs`), plus a kill-credit / seams test. Offline.
7. Parity-matrix training rows updated (screens + lobby sets).

## Missions 5 and 6 as data

Both are pure data — they build a `GoalList` of `Goal`s and run against the injected `ITrainingSite`,
exactly like Mission2/3/4.

| | Mission 5 | Mission 6 |
|---|---|---|
| Native | `src/training/mission5.cpp` | `src/training/mission6.cpp` |
| Start sector | 1050 (`mission5.cpp:80`) | 2080 (`mission6.cpp:56`) |
| Universe | `training_5`, interceptor 412 (`mission5.cpp:136`) | `training_6`, Adv. IC fighter 488 (`mission6.cpp:294`) |
| Goals | 5 (`mission5.cpp:174-178`) | ~30 sequential steps incl. 19 kill-goal waves (`mission6.cpp:569-961`) |
| Teaches | command map, picking drones, Build / Mine / Go-To / Attack orders, sector-map view, Team display "Take Me To" | a defensive arena: respawning friendly/enemy drones, escalating enemy waves gated on real kill credit |

M5 seeds its 8 friendly drones (3 tower builders + 4 miners + 1 stealth-fighter scout) as root start
actions (`mission5.cpp:181-247`) and spawns the enemy scout in goal 3 (`mission5.cpp:486-488`). M6 seeds
the friendly miners/carriers, the two respawning enemy wingmen and the enemy carrier, then escalates
them through `ModifyCreateDroneActionAction` waves keyed to `KillsReachedCondition` thresholds
(2,4,6,…,49 — `mission6.cpp:569-961`).

## New seams ported (native-cited)

| Ported class | Native file:line | Behaviour |
|---|---|---|
| `GetPickedCondition` | `GetPickedCondition.cpp:35-38` | true when the console's picked object is the target |
| `GetViewSectorCondition` | `GetViewSectorCondition.cpp:32-34` | true when the view cluster id equals the sector |
| `GetShipIsDamagedCondition` | `getshipisdamagedcondition.cpp:49-59` | fraction ≠ expected; absent ⇒ "fraction 0" (feeds M6's dead gates) |
| `KillsReachedCondition` | `KillsReachedCondition.cpp:22-26` | mission's real kill count ≥ threshold |
| `CommandAcknowledgedCondition` | `CommandAcknowledgedCondition.cpp:37-40,43-46` | last-acknowledged-command match (queued-order accept) |
| `AddRespawnAction` | `addrespawnaction.cpp:31-34` | register a `CreateDroneAction` on the mission's respawn list |
| `ModifyCreateDroneActionAction` | `modifycreatedroneactionaction.cpp:26-74` | mutate an existing `CreateDroneAction` (behaviour/tech/hull/name/location/command) |
| `CreateDroneAction.SetCreatedName` | `createdroneaction.cpp:199-201` | rename a drone (M6 relabels escalating enemies) |

**Constant corrections (spot-check was wrong, proven by native).** TR-3's report read the wingman bits
as `UseMissiles=0x10` (igc.h:786-791); native `igc.h:785-792` is `c_wbbmStrafe=0x01, c_wbbmUseMissiles=0x02,
c_wbbmUseMines=0x04, c_wbbmRunAt30Hull=0x10, c_wbbmRunAt60Hull=0x20, c_wbbmInRangeAggressive=0x40,
c_wbbmTempSectorAggressive=0x80`. Corrected the port and added the missing bits (M6's wave behaviours
depend on them). Also added `c_cidDefend=3, c_cidRepair=6, c_cidMine=8` (igc.h:898-912) and the
`PaneExpandedTeam=0x001000` pane mask (console.h:52). The `CreateDroneAction` default wingman behaviour
was corrected to native `UseMissiles|RunAt60Hull` (createdroneaction.cpp:42).

## Host integration + real kill credit

`TrainingHost` gained the `CommandView` (`training_5/412/1050`) and `PracticeArena`
(`training_6/488/2080`) variants, plus command-view state the M5/M6 seams read: picked object
(`SetPicked`/`IsPicked`), viewed sector (`SetViewSector`/`ViewSector`), armour fraction
(`GetFraction`) and the acknowledged command (`AcknowledgeCommand`). The host also gained
`SetActiveMission` and `RecordKillCredit`.

**Real kill credit (the task's core requirement).** Native counts a training kill in
`TrainingMission::ShipKilled` (`trainingmission.cpp:515-524`): the launcher on the player's side and the
victim a wingman. In the port the server's real kill-credit path already excludes ejections via the
`!bLifepod` gate in `BroadcastKillShip` (Server.Combat.cs:1417-1420). A minimal `KillCreditGranted`
event is raised at that exact credit point (Server.Combat.cs:1469-1472), and `TrainingHost` subscribes
to it and calls the active mission's `ShipKilled` — so Mission 6's `KillsReachedCondition` reads the
real credit. The headless proof drives the same single `RecordKillCredit` method to script a kill
outcome (mirroring how TR-3 scripts `DestroyDrone` for the destroyed outcome); an ejection never
reaches it, and destroying a drone without credit does not move the counter (guarded by a test).

## Proof per mission (headless, real input path)

`tests/Allegiance.ClientCore.Tests/TR4TrainingMissions56Tests.cs` (all `[Trait("Tier","Live")]`):

- `Mission5_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.CommandView`, drives
  Mission 5 through the SPACEBAR prompt, tower pick (`GetPickedCondition`), Build/Mine/Go-To/Attack
  orders (`GetCommandCondition`), the view-sector changes, and the enemy-scout destruction
  (`GetShipIsDestroyedCondition2`), then completes.
- `Mission6_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.PracticeArena`, drives
  the 19 kill-goal waves through the real kill credit (49 credits against a live enemy wingman) and
  resolves the "bomber/scout in home or dead" gates, then completes with the victory message.
- `Mission6_KillCredit_IsRealCredit_NotASeparateCounter` — a real credit of an enemy wingman by the
  player's side increments the count; destroying a drone without credit does **not**; a no-launcher
  (ejection-style) non-credit and a non-wingman (miner) victim do **not**. Proves the count is the real
  credit, never a separate counter.
- `Mission56_CommandViewSeams_LandRealEffects_OnTheHost` — picked / view-sector / fraction /
  acknowledged-command / `GetShipIsDamagedCondition` resolve real host state.

Measured this session: each of the four TR-4 tests **passes 3/3 × 3 isolated runs** (sub-second with
`StepLight`). The affected TR-2 screen test (`TrainingScreen_ListsNativeMissions_PlayableThroughEnemyEngagement`)
also passes 3/3 alone.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| M5/M6 count the player-side's wingman kills only | trainingmission.cpp:515-524 | design | `RecordKillCredit` applies launcher-side + wingman-victim semantics |
| An ejection is not a kill | Server.Combat.cs:1417-1420 (`!bLifepod`) | design | event raised only at the real credit point; ejection excluded upstream |
| `GetShipIsDamaged(…,0.0)` treats an absent ship as "dead" | getshipisdamagedcondition.cpp:58-59 | design | reproduced exactly (M6's "home or dead" gates) |
| Spawn-group respawn waits for the whole group | trainingmission.cpp:183-218 | design | reproduced; the M6 driver keeps the scout alive to feed kills, leaving the bomber-gate trivially resolved by absence |
| Respawn downtime ~10-300 s narrative pacing | addrespawnaction.cpp / trainingmission.cpp:179 | era constraint | kept as data; headless `StepLight` advances the clock cheaply |

## Modern alternative considered

The respawn list and kill-counter in the port reuse the existing authoritative embedded host (no
divergent world), exactly as TR-2b/TR-3. The one new decision is wiring Mission 6's kill goals to the
server's real kill-credit event rather than maintaining a parallel "training deaths" counter — the
modern choice of a single source of truth for kills, so training statistics can never diverge from the
real credit ledger.

## Verification (measured)

- Whole-solution build `AllegianceCore.slnx` (Debug): **0 warnings, 0 errors**.
- `src/Allegiance.Server` changed (Server.cs / Server.Combat.cs) → **AOT publish** (`dotnet publish …
  -r linux-x64 --self-contained -p:PublishAot=true`): succeeded, **no IL2026/IL3050**.
- New TR4 tests (Tier=Live) **3× alone**: `Failed: 0, Passed: 4` × 3 runs.
- Affected TR2 screen test **3× alone**: 1/1 × 3.
- **Fast tier once**: PASS — 8776 tests (Lobby 19/19 · ClientCore.MonoGame 2483 pass / 3 not-exec ·
  ClientCore.Tests 1433 pass / 1 flake · Core 4825/4825; quarantine 12).
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` **5×**: 5/5 pass
  (Server.Combat touched; no flight/sim change, but run per the gate).
- **Full suite** (`run-tests.sh --tier all`, once): fast tier 8776 ran clean apart from the single
  known `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` load-flake
  (already listed in `docs/TEST_FLAKES.md:1485`; confirmed 3/3 pass in isolation this session). The
  live tier **stalled** under concurrent Codex-agent live-suite load (multiple agents were running
  live showcases on this box; the documented hazard in the P731 lesson that concurrent live suites
  stall `run-tests.sh`). Per the light gate I did **not** re-run the full suite hoping for clean; the
  fast-tier numbers above are measured, and the P700 bots/flight gate was covered separately (5/5).

Raw logs: `~/allegiance-scratch/TR4/` (fast + full-suite logs; AOT publish). Evidence size kept ≤ 2 MB.

## Remaining (honest)

As in TR-3, the **interactive real-window run** — navigating the MonoGame client (intro → Single
Player → Training → the mission) on a real X display and pumping the mission through its interactive
inputs (SPACEBAR, command-view picks/orders) while capturing 1280×720 backbuffer autoshots — is not
delivered this session. The framework, both missions, the host and the headless proofs are all real
and verified; the pixel-level interactive half needs client-side input automation (the real window on
`DISPLAY=:0` is available, but no xdotool-style driving harness exists and several Codex agents are
currently occupying the box). I have not fabricated any screenshots.

## Files changed

Added: `src/Allegiance.ClientCore/Training/Mission5.cs`, `Mission6.cs`,
`CommandViewConditions.cs`, `RespawnAndEscalationActions.cs`;
`tests/Allegiance.ClientCore.Tests/TR4TrainingMissions56Tests.cs`.

Modified: `TrainingMissionIds.cs`, `ITrainingSite.cs`, `CreationActions.cs` (ClientCore);
`TrainingHost.cs`, `TrainingHostSite.cs` (SinglePlayer); `ClientTrainingSite.cs`,
`Game1.Training.cs`, `TrainingScreenModels.cs` (Game); `Server.cs`, `Server.Combat.cs` (Server);
`FakeTrainingSite.cs`, `TR2TrainingTests.cs` (tests); `docs/parity/parity-matrix.csv`.
