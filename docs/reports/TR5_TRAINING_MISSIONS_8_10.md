# TR-5 — Training Missions 8 (Nanite) and 10 (Free Flight): complete the native training set

Branch: `master` (committed after this report). Not pushed.

## Scope delivered

TR-1..TR-4 ported the training framework, Mission 2 (Basic Flight), Missions 3/4 (Basic Weaponry /
Enemy Engagement) and Missions 5/6 (Command View / Practice Arena). TR-5 completes the native
training set with the last two native missions as data:

1. **Mission 8 "Nanite"** — `src/Allegiance.ClientCore/Training/Mission8.cs`, a faithful port of
   `src/training/mission8.cpp` (TheBored 2007): three sub-goals — mount the Nanite Repair System
   (part 190) and repair the knocked-down home station, defend the miners, and escort a bomber to
   destroy the enemy outpost.
2. **Mission 10 "Free Flight"** — `src/Allegiance.ClientCore/Training/Mission10.cs`, a faithful port
   of `src/training/mission10.cpp`, which native describes as "Cloned mission 6 without intro and
   with less constraints" (`mission10.cpp:6`) — the same escalating kill-goal waves as Mission 6.
3. **New seams** (each cited to native): `GetShipHasMountedCondition`, `SetDroneNanAction`,
   `DamageStationAction`, plus host support for station armour fractions (repair/destroy) and
   partial ship armour.
4. **Enabled both on the real path**: Nanite is now a selectable mission on the training screen;
   Free Flight matches native's debug-only `bStartTraining` launch (no screen button) via an
   `ALLEGIANCE_TRAIN_MISSION` env hook.
5. **Proof per mission**: headless completion tests against the embedded host through the real input
   path, plus a real-window (1280×720) backbuffer screenshot of the training screen with the full
   native set selectable.
6. Training README section + parity-matrix training rows updated.

## Where each native mission appears

Native `trainingscreen.cpp:50-58` lists buttons for missions 1, 2, 3, 4, 5, 6 and 8 (mission 7/Live
removed, "//TM7 removed"). **Free Flight (mission 10) has no button** — it is launched only through
the `bStartTraining` debug flag (`trainingscreen.cpp:105-108`: `g_pnumberMissionNumber->SetValue(
Training::c_TM_10_Free_Flight); OnButtonTrain();`), skipping the slideshow. The port matches: Nanite
is a screen entry (`TrainingScreenModel`), Free Flight is launched via `ALLEGIANCE_TRAIN_MISSION=10`
(the same debug-flag intent) and stays off the chooser. `TrainingMission.RecordKeyPress`
(`trainingmission.cpp:388-396`) already lets the hard-disabled keys through for mission 10, so Free
Flight inherits its key passthrough automatically.

## Missions 8 and 10 as data

Both are pure data — they build a `GoalList` of `Goal`s and run against the injected `ITrainingSite`,
exactly like Missions 2-6.

| | Mission 8 | Mission 10 |
|---|---|---|
| Native | `src/training/mission8.cpp` | `src/training/mission10.cpp` |
| Start sector | 1051 (`mission8.cpp:62-64`) | 2080 (`mission10.cpp:49-53`) |
| Universe | `training_nan`, Adv Rix Scout 685 (`mission8.cpp:89`) | `training_6`, Adv. IC fighter 488 (`mission10.cpp:290`) |
| Goals | 3 (`mission8.cpp:148-159`) | ~30 sequential steps incl. 19 kill-goal waves (`mission10.cpp:369-967`) |
| Teaches | nanite mounting, station repair, miner defence, bomber escort | an open practice arena: defending miners + escalating enemy waves gated on real kill credit |

**Ground truth for the training_nan map (probed from the .igc this session).** Sector/station ids are
distinct namespaces: sector 1051 = home, sector 1052 = enemy. The **home station is station id 1052**
(cluster 1051, side 0), and the **enemy outpost is station id 1051** (cluster 1052, side 1) — native's
`pEnemyStation = GetModel(OT_station, 1051)` (`mission8.cpp:414`) is the enemy outpost in the 1052
sector, not the home station. The host's home-station resolution was therefore generalised from
"station id == start sector" to "the player's-side station in the start sector" (matching native
`GetSide()->GetStations()->first()`), which still yields 2080 for `training_6` etc.

## New seams ported (native-cited)

| Ported class | Native file:line | Behaviour |
|---|---|---|
| `GetShipHasMountedCondition` | `getshiphasmountedcondition.cpp:36-45` | true when any part in the ship's list has the part-type id and mount id >= 0 — the goal-1 gate on mounting Nanite 190 |
| `SetDroneNanAction` | `setdronenanaction.cpp:36-52` | terminate the drone's front weapon and stick Nanite 190 on mount 0 (goal-3 nan wingman) |
| `DamageStationAction` | `mission8.cpp:203` (`pStation->ReceiveDamage(…,12500,…)`); `stationIGC.cpp:147-156` | knock a station's armour down (negative amount repairs) |
| host station armour fractions | `stationIGC.cpp:147-156` | station repair/destroy resolved against real host state (`GetFraction(OT_station, …)`) |
| host partial ship armour | `shipIGC.cpp:1212` (`IshipIGC::ReceiveDamage`) | lets Mission 8's "bomber hull damaged" gate (`mission8.cpp:586`, `GetShipIsDamagedCondition(bomber,1.0)`) fire without destroying the bomber |

Reused, not duplicated: the server's negative-damage repair already exists (`src/Allegiance.Core/Sim/Ship.cs:4509`,
station repair `Sim/Station.cs` per `stationIGC.cpp:147-156`), and Codex SC-2's nanite support
pilots feed it; TR-5's training host realises the mission's nanite/repair outcomes through those
same host facilities.

## Host integration

`TrainingHost` gained the `Nanite` (`training_nan/685/1051`) and `FreeFlight` (`training_6/488/2080`)
variants, a generalized home-station resolution, map-station records, per-station and per-ship
armour fractions, and mounted-part tracking. The three `ITrainingSite` implementations
(`TrainingHostSite`, `ClientTrainingSite`, `FakeTrainingSite`) gained `HasPartMounted`, `MountPart`
and `DamageStation`. `Game1.BeginTrainingMission` accepts `Tm8Nanite` and `Tm10FreeFlight` (variants
+ mission construction); `TrainingScreenModel` marks Nanite `Playable: true`.

## Proof per mission (headless, real input path)

`tests/Allegiance.ClientCore.Tests/TR5TrainingMissions810Tests.cs` (all `[Trait("Tier","Live")]`):

- `Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.Nanite`, drives Mission
  8 through: mounting the nanite (`host.MountPart(player,190)` → `GetShipHasMountedCondition`),
  repairing the knocked-down home station (`host.RepairStation(home)` → the `Not(GetShipIsDamaged(
  station,1.0))` gate), destroying the two stealth fighters (`DestroyDrone`), parking the bomber
  within 500 m of the player then in sector 1052 (the `ObjectWithinRadiusCondition` /
  `GetSectorCondition` gates), scripting the bomber damaged (`DamageShip`, the
  `GetShipIsDamagedCondition(bomber,1.0)` gate) and destroying the enemy outpost (`DamageStation`,
  the station-or-bomber-destroyed gate) — completing with the "Enemy outpost destroyed." win.
- `Mission10_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.FreeFlight`, drives
  the 19 kill-goal waves through the real kill credit (49 credits against a live enemy wingman) and
  resolves the "bomber/scout in home or dead" gates, then completes with the victory message — the
  same drive as the TR-4 Mission 6 proof.
- `Mission8_Seams_LandRealEffects_OnTheHost` — `GetShipHasMountedCondition` / `SetDroneNanAction`
  resolve real mounted-part state; `DamageStationAction`/`GetFraction(station)`/`RepairStation`
  move the home station 1 → damaged → 1; the enemy outpost drops to 0 on a big hit; `DamageShip`
  leaves a ship damaged-but-alive.

Measured this session: the three TR-5 tests **pass 3/3 × 3 isolated runs** (sub-second each with
`StepLight(1.0)`). The full affected training set (48 tests incl. TR-2/3/4) passes **48/48 × 3**.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| `HandlePickDefaultOrder` (autonomous wingman order-picking, M6+M10) | `mission6.cpp:82` / `mission10.cpp:78`, `trainingmission.cpp:527` | design (server AI) | not ported as script data; realized by the embedded server's own NPC/wingman AI (`Ship.NpcUpdates.cs`, SIM-0111..0118). Consistent with TR-4's M6 precedent |
| Free Flight has no training-screen button | `trainingscreen.cpp:105-108` | design | matched: launched via `ALLEGIANCE_TRAIN_MISSION=10` debug hook, stays off the chooser |
| Free Flight lets every key through | `trainingmission.cpp:388-396` | design | already in `TrainingMission.RecordKeyPress`; Mission 10 inherits it |
| Home station = player's side's first station | `trainingmission.cpp` (`GetSide()->GetStations()->first()`), map `.igc` | design | host resolves by cluster + side, not by "station id == start sector" |
| `SetCargoNanAction` declared but never used in M8 | `mission8.cpp` body | n/a | not ported (unused by the mission script) |
| `SetStationDestroyedAction` commented out & unimplemented | `mission8.cpp:622` (`// … SetStationDestroyedAction …`) | n/a | not ported; the outpost-destroyed outcome is scripted via the host station-armour seam |
| Mission 1 (Introduction) is slideshow/voice-over only | `trainingscreen.cpp:182-185` → `ScreenIDPostTrainSlideshow` | design | out of scope (confirmed slideshow/video); stays `Playable:false` |

## Modern alternative considered

Native simulates the whole battle tick-by-tick; TR-3/4/5 reuse the authoritative embedded server as a
single source of truth (no divergent "training world"), and TR-5 extends that to station armour and
nanite repair through the same server facilities SC-2 added — rather than duplicating a parallel
health ledger. The headless driver scripts the *outcomes* the script gates on (mount, repair, kills,
damage, destruction) through dedicated host seams, mirroring how TR-3/4 script target-picks and
kills; the player-visible intent (mount the nanite, repair the station, defend the miners, escort
the bomber, win) is preserved.

## Verification (measured)

- Whole-solution build `AllegianceCore.slnx` (Debug): **0 warnings, 0 errors**.
- `src/Allegiance.Server` / `src/Allegiance.Core` were **not** changed → the light gate's AOT-publish
  conditional does not apply (no AOT run needed).
- New TR-5 tests (Tier=Live) **3× alone**: `Failed: 0, Passed: 3` × 3.
- Affected training set (TR-2..TR-5, 48 tests) **3× alone**: `48/48` × 3.
- **Fast tier once** (`tools/run-tests.sh --tier fast`, final code): **PASS** — infrastructure PASS;
  Lobby 19/19 · MonoGame 2486 pass / 3 not-exec · ClientCore 1454/1454 · Core 4828/4828 ·
  quarantine 12. (An earlier fast run flagged one failure, `P674ParityScorecardTests
  .Both_repository_matrices_validate_and_print_scorecards`, caused by a malformed parity-matrix row
  from my CSV edit; fixed and re-verified **8/8 × 3**.)
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`: not re-run — this
  change does not touch the server broadcast/transport/sim flight/bot path (training-host-only
  state); a separate Codex agent ran it 5× on this box during the session.
- No transport/server/bot change → the mandatory live-bot-route check does not apply (nothing in
  that class was modified).

Raw logs: `~/allegiance-scratch/TR5/` (fast runs, per-run test logs, AOT not applicable). Evidence
size kept ≤ 2 MB.

## Real-window proof (delivered)

A genuine **real-client-window backbuffer** screenshot was captured on the live X display
(`DISPLAY=:0`), 1280×720, via `ALLEGIANCE_AUTOSHOT` → `GetBackBufferData` → `SaveAsPng` (the same F11
screenshot path the game uses), driven by a **temporary** `ALLEGIANCE_TRAINING_SCREEN=1` launch hook
that forced the client straight to the mission chooser (reverted afterward; repo clean). It shows the
Training screen with all **seven** native missions listed and **six of them selectable** — Basic
Flight, Basic Weaponry, Enemy Engagement, Command View, Practice Arena and **Nanite** (no "coming
soon"); only **Introduction** (slideshow-only) stays marked coming-soon, and **Free Flight is absent**
(no native button). Asset:
`docs/reports/TR5_assets/TR5_training_screen_full_native_set.png`.

## Remaining (honest)

As in TR-4, the **running-mission render** in the real window — a Mission 8/10 mission actually
started and rendering 3D from the embedded host — is not delivered: the embedded training host boots
the map and logs the mission started, but its minimal server does not send `LOGONACK` to seat the real
client session, so the client cannot enter the airborne mission and stays on the chooser. That
real client-seating engineering remains the same open item TR-3/TR-4 identified; I have not fabricated
a running-mission screenshot. The headless completion proofs (above) exercise the full mission logic
through the real embedded-host input path.

## Files changed

Added: `src/Allegiance.ClientCore/Training/Mission8.cs`, `Mission10.cs`, `Mission8Seams.cs`;
`tests/Allegiance.ClientCore.Tests/TR5TrainingMissions810Tests.cs`.

Modified: `ITrainingSite.cs` (ClientCore); `TrainingHost.cs`, `TrainingHostSite.cs` (SinglePlayer);
`ClientTrainingSite.cs`, `Game1.Training.cs`, `Game1.IntroScreen.cs`, `TrainingScreenModels.cs`
(Game); `FakeTrainingSite.cs`, `TR2TrainingTests.cs` (tests); `docs/parity/parity-matrix.csv`;
`packaging/README-PLAYERS.txt`.

Asset: `docs/reports/TR5_assets/TR5_training_screen_full_native_set.png` (real-window 1280×720
backbuffer).
