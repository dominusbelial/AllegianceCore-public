# P522 — first-launch pregame-flight SHIP_RESET, every-undock SHIP_RESET, and the undock false red flash

**Stage:** P522 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Symptom (Domingo's live playtest, 2026-10-03)

1. **First launch of a new game teleports back.** "first dock try is giving me a repeated
   cycle, I teleport to the same spot I started at full speed, and the second try it docks."
   Server `[ctrlak]` for the new session: `dropped[pregame=47]`, then `accepted=14
   rejected=1 mean-reject-dist=1264.97m … pregame=99`, then stable; client
   `[sim] seeded from server pose …` → `[sim] RESUMED sending native SHIP_UPDATE` →
   `[sim] SHIP_RESET #1 — snapped to server pose`.
2. **Every undock causes one SHIP_RESET.** Client log `~/monogame-client-run-undock-reset.log`:
   every `[monogame] undocked: leaving hangar` is followed within a line or two by
   `[sim] SHIP_RESET #N`. Server peer-4 summary `rejected=4 mean-reject-dist=704.92m` —
   exactly one ~705 m reject per launch.
3. **A false red damage flash right after undock.** "right after undocking I see the
   collision warning and then it goes away … red flash around the screen", with neither the
   client nor the server logging any collision or damage around the undocks.

## Root causes (verified in source)

### 1. The client flies during the pre-game countdown → the server drops every update as "pregame"
The port's server has a correct, deliberate P180 pre-game hold: while `Mission.Stage !=
Started` an inbound SHIP_UPDATE is ignored and counted `DroppedPreGame`
(`Server.Dispatch.cs:141-144`; native's equivalent is that a not-yet-launched ship has
`GetCluster()==NULL` and so its pose is dropped, `FedSrv.CPP:4858-4859`). The client,
however, entered **Flying** the moment the start **countdown began**
(`Game1.Panes.cs` `UpdateReadyCheck`, the old `CountdownStartLocalMs is not null → Flying`
branch), then started flying and emitting native SHIP_UPDATEs while the mission was still
STAGE_STARTING — so the server dropped ~99 of them as pregame. When the mission truly
started, the first *accepted* update was ~1.26 km off the start point, got rejected, and
`SHIP_RESET` snapped the ship back carrying its current velocity — the repeated teleport.
It only happened on the **first** launch of a fresh game because that is the only time a
normal pre-game peer was present when the countdown began.

**Native holds the player through the countdown; it only enters the game at STAGE_STARTED.**
`appmsg.cpp:2330-2346`: `STAGE_STARTING → SetCountdownStarted + OnMissionCountdown`,
`STAGE_STARTED → SetInProgress + OnMissionStarted`; the client's `OnEnterMission` is what
switches to the combat screen (`WinTrek.cpp:8147-8154 → screen(ScreenIDCombat)`) and the
countdown timer is drawn in the game view meanwhile (`WinTrek.cpp:8129-8137`). So the ship
is held (docked, not flying, not sending) until the mission Starts.

### 2. On undock the sim resumes from a stale pose instead of the server's launch pose
On undock the server clears the station, repositions the ship to the station's real launch
point and re-sends `SET_CLUSTER` carrying that pose (`Server.Docking.cs` `PerformUndock`,
`1907-1923`; native `FedSrv.CPP:7040-7052` + `CFSPlayer::SetCluster`). The client's P508
own-ship sim **resumed from the pose it was frozen at** on dock — the pre-dock / held pose —
rather than re-seeding to the authoritative launch pose (`Game1.Prediction.cs` old
`else if (_ownSim.Frozen) _ownSim.Resume()`), so its first post-undock SHIP_UPDATE was
~700 m off the launch point, got rejected, and `SHIP_RESET` teleported the ship — once per
undock.

### 3. The undock false red flash is a state transition misread as damage
`DamageFlashTracker` arms on any decrease in the local ship's hull **or** shield fraction
between frames, and its baseline is never reset across transitions
(`DamageFlashTracker.cs`; fed at `Game1.Rendering.cs:1494`). An undock — especially into a
newly-bought hull, or across a `SHIP_RESET` pose/health jump — legitimately changes the
reported fractions (previous ship's full shield → the new hull's starting shield), so the
tracker read it as damage and flashed red with no damage at all.

## Fixes

### 1. Hold the player in READY-CHECK until STAGE_STARTED (scope 1 + 3)
`Game1.Panes.cs` `UpdateReadyCheck` now has a **single** release: `MissionStage.Started`
(the P309 `MISSION_STAGE(37)` broadcast from `CompleteStartCountdown`,
`Server.Lobby.cs:1055`, which reaches every mission peer). The countdown-began exit is
removed, so the ship is held (not flown, not sending — the sim only runs in `Flying`) for
the whole countdown, exactly like native. Because the release is `Started`, the client's
first native SHIP_UPDATE is always accepted: **zero pregame drops, no first-launch
SHIP_RESET**. The countdown is surfaced clearly while held: `DrawReadyCheck` computes the
remaining seconds (same `BriefingCountdownSeconds` formula as `IsBriefingVisible`) and
`ReadyCheckLayout.Build` renders a prominent `LAUNCHING IN n` row (the port's held view of
native's GameStateContainer countdown timer).

### 2. Re-seed the sim from the launch SET_CLUSTER pose before resuming on undock (scope 2)
`Game1.Prediction.cs` `UpdateOwnShipSimulation` now, on the freeze→resume edge, **re-seeds
the sim from the current authoritative server pose (`up`, which is the undock SET_CLUSTER's
launch pose) before `Resume()`**, gated on a *fresh* server update having arrived since the
freeze (record `_ownSimFrozenAtTick = mine.LastUpdateTick` at freeze; only re-seed/resume
when `mine.LastUpdateTick != _ownSimFrozenAtTick`). This guarantees the first post-undock
SHIP_UPDATE matches the launch point → accepted → **zero rejects, zero SHIP_RESETs per
undock**. It also covers warp/sector-change/respawn resumes uniformly (native always
continues from the authoritative pose after a discontinuity). Applies to any `StationId`
undock, ripcord end, sector change and respawn.

### 3. Reset the damage-flash baseline across transitions + launch with full shields (scope 3)
`DamageFlashTracker` gained `ResetBaseline()` (clears the decrease-detection baseline
without arming, and without cutting an active real flash). `Game1.Prediction.cs` calls it on
**hull change, SHIP_RESET, freeze (dock/death/ripcord/warp) and resume (undock/launch)** —
every transition that legitimately jumps the wire fractions and is not damage.
`DamageDebrisTracker` needs no reset: it is a pure level function of the current hull
fraction with no cross-frame baseline (documented in the code). And the **parity question is
answered**: native's BUY_LOADOUT restock tops the mounted shield to full **after** the
loadout rebuild (`FedSrv.CPP:12756-12764`, `pship->SetFraction(1.0f)` then
`pshield->SetFraction(1.0f)`); the port's `Server.ShipCommands.cs` `BuyLoadout` path now
does the same (`ship.SetShieldFraction(1.0f)` after the mount loop), so a freshly-bought or
hull-swapped ship always launches with full shields — the server half of the parity.

## Scope items
1. Held until `Started` (ReadyCheck stays up, countdown shown) — no pregame drops, no
   first-launch SHIP_RESET. ✅
2. Re-seed the sim from the server's authoritative launch pose before the first
   post-undock update — zero rejects, zero SHIP_RESETs across 3 dock→undock cycles. ✅
3. Damage-flash baseline reset on dock/undock/hull-change/respawn/SHIP_RESET + server
   launches hulls with full shields (native parity). ✅
4. The P180 server gate is **unchanged** (it is correct). ✅
Out of scope confirmed untouched: the death/respawn latch (P521); mission rotation
(P439/P480/P481).

## Verification

### Whole-solution build
`/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors**.

### Unit / pure-logic tests (Allegiance.ClientCore.MonoGame.Tests)
- **`DamageFlashTrackerTests` +3** — `ResetBaseline` suppresses the false flash across a
  legit value jump (the undock/hull-change case); a real subsequent hit still flashes; an
  already-lit flash is not cut short. Must-fail proven: with `ResetBaseline` neutered to a
  no-op the first test goes RED (the 1.0→0.914 shield drop false-arms).
- **`ReadyCheckLayoutTests` +2** — `LAUNCHING IN n` is emitted (highlighted) when a countdown
  is passed, and omitted when it isn't; rows stay printable ASCII.

### Live test — REQUIRED, failing-first (Allegiance.ClientCore.Tests) `P522UndockReseedLiveTests`
A real `ConnectionSession` drives the real production own-ship stack (`OwnShipSimulator` +
`OwnShipServerClock` from real FM_CS_PING echoes + `SendShipPose`) over a real TCP wire to a
real in-process server, replicating the production resume edge
(`Game1.Prediction.UpdateOwnShipSimulation` step 5). The sim is parked at a deterministic
1500 m open-space pose, in-flight sends are drained, the sim is frozen there, the server
ship is teleported into the dock radius and undocked; on undock the server repositions to
the launch point + SET_CLUSTER.
- **`Three_dock_undock_cycles_zero_rejects_and_zero_SHIP_RESETs_with_reseed` (the fix)** —
  3 consecutive dock → undock cycles: measured **0 rejects, 0 SHIP_RESETs** every cycle
  (`[P522-UNDOCK] cycle 0/1/2: undock+settle rejects 0 resets 0`).
- **`Without_reseed_each_undock_adds_a_reject_and_SHIP_RESET` (the bug, discriminating)** —
  the SAME real arc with the re-seed disabled (resume-from-stale-pose) rejects the stale
  pose and fires a SHIP_RESET: `[P522-UNDOCK] FINAL (no reseed): undock rejects 1,
  SHIP_RESETs 1`. Red on the bug, green on the fix.

### Live test — server shield parity `P522ShieldParityLiveTests`
A real `BUY_LOADOUT` hull swap (base hull → granted successor, the native P488 path) after
the shield is wounded to 0.4 restores `Ship.ShieldFraction` to 1.0 — native parity
(`FedSrv.CPP:12756-12764`). Must-fail proven: removing the `SetShieldFraction(1.0f)` line
makes the test RED (shield stays 0.4 → 0 on the rebuild).

### Full suite
`scripts/run-tests.sh` (P471, sequential) run multiple consecutive times. Every project
segment was green for the new work — **Allegiance.ClientCore.MonoGame.Tests 1197/1197** and
**Allegiance.ClientCore.Tests 433/433** (incl. all new P522 tests) were fully green in the
cleanest complete pass; the only failures across all runs were pre-existing environmental
live-network flakes in OTHER suites, each confirmed green in isolation:
`ThreePeerSeededSessionTests.SeedThreePeers…DeterministicVisibility`,
`FullSessionKillIntegrationTests.FullSession_ThreePeers…RealCrossSideKill`,
`MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire`,
`LiveRipcordTests.Cancel_while_active_aborts_sector_wide`,
`TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost…`,
`LiveStationRescueTests.Lifepod_reaching_rescue_station…`,
and the skill-documented `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`.
**No P522 test (P522UndockReseedLiveTests, P522ShieldParityLiveTests, or the flash/countdown
unit tests) failed in any full-suite run.** Numbers reported are the measured pass totals
from these runs.

## Cited files
- `clintlib/appmsg.cpp:2330-2346` (STAGE_STARTING/STAGE_STARTED) · `WinTrek.cpp:8129-8137,
  8147-8154` (countdown timer + OnEnterMission → ScreenIDCombat).
- `FedSrv.CPP:4858-4859` (server SHIP_UPDATE gate on in-cluster) · `FedSrv.CPP:7040-7052`
  (undock) · `FedSrv.CPP:12756-12764` (BUY_LOADOUT shield-to-full).
- Port: `Game1.Panes.cs` (`UpdateReadyCheck`, `DrawReadyCheck`), `Game1.Prediction.cs`
  (`UpdateOwnShipSimulation`), `DamageFlashTracker.cs` (`ResetBaseline`),
  `Server.Docking.cs` (`PerformUndock`), `Server.ShipCommands.cs` (BUY_LOADOUT),
  `Server.Dispatch.cs` (P180 gate, unchanged).
