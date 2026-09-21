# MISSION_STAGE_P309 — fix join-burst hardcoding MISSION_STAGE(37) to Started

Two independent read-only reviews of `src/Allegiance.Server/Server.cs`
converged on this bug: kimi-k3 (finding #2,
`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-kimi-k3.md`) and
`zai`'s glm-5.3 (finding M7,
`.../allegiance-server-review-glm53.md`). The join burst inside
`CompleteJoinAndLaunch` unconditionally sent `MISSION_STAGE(37)` with
`Stage = StageStarted` to every joining peer, with no check of the mission's
actual `Stage`. A peer joining during the lobby/ready/countdown period was told
the game was already live while the server's pre-game hold dropped every
control input — a lie the rest of the same codepath contradicts (the
`ENTER_GAME` join-sync send just below is correctly gated on
`Mission.Stage == Started`). No `MISSION_STAGE` was ever re-sent later when the
mission actually started.

## Root cause

`src/Allegiance.Server/Server.cs:6180-6202` (join burst) hardcoded
`StageStarted`. Missions are born `NotStarted` (P180) and only progress through
the ready-up countdown, but the burst told every joiner `Started` regardless.

## The fix (all in `src/Allegiance.Server/Server.cs`)

1. **Join burst sends the mission's real stage**
   (`Server.cs:6194-6202`). Replace the hardcoded `StageStarted` with
   `live.Ship.Mission.Stage` (fallback `NotStarted` when no mission). The
   `MissionStage` enum values (`NotStarted=0..Over=3`, `Sim/IgcEnums.cs:64-70`)
   are byte-identical to the wire `AGCGameStage` constants
   (`Wire/MissionStage.cs:36-45`), so a direct int cast is the faithful
   mapping.

2. **Whole-mission re-send on every real stage change**
   (`BroadcastMissionStageToMission`, `Server.cs:3736-3751`) — the faithful
   port of native `CFSMission::SetStage`'s MISSION_STAGE broadcast
   (`fsmission.cpp:1759-1762`, `g.fm.SendMessages(GetGroupMission(), ...)`
   whenever `m_misdef.stage` changes). Wired into the two real transitions:
   - **`StartCountdown` → `Starting`** (`Server.cs:3661`): native ends
     `StartCountdown` in `SetStage(STAGE_STARTING)` (`fsmission.cpp:1976`).
   - **`CompleteStartCountdown` → `Started`** (`Server.cs:3699`): native ends
     `StartGame` in `SetStage(STAGE_STARTED)` (`fsmission.cpp:2239`). This is
     the re-send every already-joined peer needs — a pre-game joiner told
     `NotStarted`/`Starting` at join must learn the game really started, not
     just the next new joiner.

   The audience is a whole-mission `_peerShips` sweep (per-mission scoped,
   matching `BroadcastEnterGameToMission`/`SendToMissionPeers`).

## Client half (item 3)

The MonoGame client already gates its local flight simulation on the
`ReadyCheck`/`Flying` state machine (only `Flying` runs `UpdateFlying`,
`Game1.cs:1022-1023`), so a pre-game joiner never simulates flight while the
mission is `NotStarted` — the functional gate exists. But that handoff was
anchored **only** on `SET_START_TIME` (received at countdown-begin), which a
peer joining mid-countdown / after start never receives, stranding it in
`ReadyCheck`. With the server now sending the real stage and re-broadcasting
`MISSION_STAGE(Started)` at the real start, the client correctly reacts to
receiving a non-`Started` stage by gating on the authoritative stage:
`Game1.cs:3999` transitions `ReadyCheck → Flying` when
`world.Mission.Stage` reaches `Started` (in addition to the existing countdown
signal). This closes the mid-join gap and is the client half of the same bug.

## Live proof (in-process server + loopback bot harness)

New `tests/Allegiance.Core.Tests/LiveMissionStageTests.cs` (3 tests) drives a
real in-process `Srv` + real TCP bots; the bot `MissionStages` accumulator
(Bot.cs) captures each `MISSION_STAGE(37)` in arrival order. Real wire values:

```
P309-WIRE-JOIN:     bot 'Pre' received MISSION_STAGE stage=0 (NotStarted) at join while mission stage=NotStarted
P309-WIRE-START:    bot '8096' MISSION_STAGE sequence = [NotStarted,Starting,Started]
P309-WIRE-START:    bot '8097' MISSION_STAGE sequence = [NotStarted,Starting,Started]
P309-WIRE-LATEJOIN: late bot 'Late' received MISSION_STAGE stage=2 (Started) at join into a Started mission
```

This is exactly the required live proof: join a fresh (not-yet-started) mission
→ received stage is `NotStarted`, not `Started`; trigger the real start
(ready-up auto-start) → `MISSION_STAGE(Started)` is actually sent to the
already-joined peers at that moment.

**Must-fail proof:**
- Reverting the join burst to the old hardcoded `Started` makes
  `Join_while_mission_not_started_receives_non_started_stage` red
  (`Expected: 0, Actual: 2`).
- Removing the `CompleteStartCountdown` broadcast makes
  `Ready_up_auto_start_broadcasts_started_stage_to_already_joined_peers` red
  ("auto-start did not broadcast MISSION_STAGE(Started) to both peers").

## Build / test status

- `dotnet build` (Server, Bot, ClientCore.MonoGame) → **0 warnings, 0 errors**.
- New `LiveMissionStageTests` → **3/3 pass**; goes red when the fix is removed.
- `Allegiance.ClientCore.MonoGame.Tests` → **576/576 pass** (includes the
  Game1.cs change).
- `Allegiance.ClientCore.Tests` → **157/157 pass**.
- Full `Allegiance.Core.Tests` suite run 7× (3228 total = 3225 baseline + 3
  new): every run's failures are the **documented / pre-existing environmental
  live-network flakes** only — `LiveTurretGunnerBindingTests` (documented
  persistent), `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`
  (documented), plus a rotating set (`LiveMoneyTests.Board_refund…`,
  `StationsUpdateLiveTests.*`, `CargoBayReloadTests.Reload_NA…`,
  `GameOverPlayersWireTests.*`). Two of the rotators
  (`LiveMoneyTests.Board_refund…`, `StationsUpdateLiveTests.Damaged…`) were
  reproduced **failing on the pristine tree** (all P309 changes stashed, new
  test moved aside), and none of the failing tests reference any P309 symbol —
  all environmental, none a P309 regression. My 3 new tests are green in every
  full-suite run.

## Out of scope (separate tasks)

- **P307** — sector-membership desync fix (relocation sites). Separate root
  cause, separate task; P309 touches only stage reporting.
- **P308** — eject-pod dead-fraction fix. Separate root cause.
- Redesigning the pre-game/ready-check flow (P281/P282) — P309 only fixes the
  stage value lied about in the join burst + its real transition re-send.
- Any other finding from either review (alliance-array overflow,
  `_missions[0]` hardcoding, payday/readiness-on-disconnect gaps, god-class
  notes, etc.).
- The native reference (`~/Development/Allegiance`) — read-only, untouched.
