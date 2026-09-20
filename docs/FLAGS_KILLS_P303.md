# Flags + Kills win conditions (P303)

2026-09-20 · lands on `master` (not pushed — Domingo pushes)

Implements the last two of the six native win-condition game modes (flags +
kills), completing the family on the **same single game-over path** as P191
(conquest/territory) and P302 (prosperity/artifact).

## Relationship to native (every claim cited)

Native's 6 win-condition modes live at `src/FedSrv/fsmission.cpp:2330-2503`;
each mode also has its own everyday (non-countdown) trigger. Two are done here.

### Flags — secure-`nGoalFlagsCount` enemy-flags win

- Capturing + returning an enemy flag: docking at one's own side's station while
  carrying a real enemy flag (carried value = the enemy side's id,
  `!= SIDE_TEAMLOBBY`) increments `pside->SetFlags(+1)` and calls
  `CheckForVictoryByFlags(pside, sidFlag)` (`src/FedSrv/FedSrv.CPP:13136,13149`).
- The flag leg of `CheckForVictoryByFlags` (`src/FedSrv/fsmission.cpp:3883-3887`)
  wins when `psideTest->GetFlags() >= pmp->nGoalFlagsCount` (the
  `sidFlag != SIDE_TEAMLOBBY` real-flag branch; the team-lobby sentinel is the
  artifact leg, already built in P302). `IsFlagsGame()` ⇔ `nGoalFlagsCount != 0`
  (`src/Igc/igc.h:1541-1543`).

### Kills — deathmatch `nGoalTeamKills` kill-threshold win

- A side that earns a credited kill (`psideCredit->AddKill()`,
  `src/FedSrv/FedSrv.CPP:11868`) is tested by `CheckForVictoryByKills`
  (`src/FedSrv/fsmission.cpp:3749-3760`), which wins when
  `psideTest->GetKills() >= pmp->nGoalTeamKills`
  (`src/FedSrv/fsmission.cpp:3756`); the game then ends (`FedSrv.CPP:11871-11880`,
  "…pushes … over the top with a kill"). `IsDeathMatchGame()` ⇔
  `nGoalTeamKills != 0` (`src/Igc/igc.h:1521-1523`).
- This is the everyday deathmatch win, distinct from the *countdown-expiry*
  tie-break fallback that also ranks by kills (`fsmission.cpp:2468-2492`) — that
  is a time-expired decider, not built here.

## Scope boundary / exclusions

- **Prosperity + artifact (P302)** and **conquest/territory (P191)** untouched.
- The **countdown game/timer** (`IsCountdownGame`, `dtGameLength`) and its
  tie-break cascade are **not** built — out of scope; the everyday flags/kills
  triggers are the deliverable.
- **Native reference** (`~/Development/Allegiance`) read-only — never edited.

## Implementation

Follows P191/P302's exact architecture (never a second game-over mechanism):

- `Model/Side.cs`
  - `Kills` + `Deaths` (shorts) — the port of native per-side `m_data.nKills` /
    `m_data.nDeaths` (`sideigc.h:457-473`, `GetKills`/`GetDeaths`). `Kills` is
    what the deathmatch win reads; `Deaths` kept for structural faithfulness.
- `Model/Mission.cs`
  - New flags/defaults (stated Stage-1 substitutes, exactly like P191/P302):
    `FlagsEnabled`, `FlagsWinGoal` (= native `nGoalFlagsCount`, igc.h:1310),
    `KillsEnabled`, `KillsWinGoal` (= native `nGoalTeamKills`, igc.h:1292).
  - `RecomputeFlagsAndKills() : (Side? Winner, bool Changed)` — the stage-gated
    sibling of `RecomputeProsperityAndArtifacts`. Flags branch: first side with
    `Flags >= FlagsWinGoal` (`fsmission.cpp:3883`). Kills branch: first side
    with `Kills >= KillsWinGoal` (`fsmission.cpp:3756`). Flags is consulted
    first (mirrors the native cascade ordering).
- `Sim/IIgcSite.cs` + `Server/ServerHost.cs` — `FlagsKillsComputed`
  seam/hook, mirroring `ProsperityArtifactComputed`.
- `Server/Server.cs`
  - `OnFlagsKillsComputed` (mirrors `OnProsperityArtifactComputed`): GAME_STATE(155)
    on change + `DeclareGameOver` on winner (P178 path).
  - **Flags trigger**: `OnShipDocked` secure leg — when the secured value is a
    real enemy flag (`!=` the artifact sentinel) and `FlagsEnabled`, recompute +
    raise the seam (`fsmission.cpp:3883-3887`, FedSrv.CPP:13149).
  - **Kills trigger**: `BroadcastKillShip` kill-credit block — bump `Side.Kills`
    on a qualifying cross-side credited kill and, gated on `KillsEnabled`,
    recompute + raise the seam (`fsmission.cpp:3749-3760`, FedSrv.CPP:11872).
    `Side.Deaths` is bumped for the victim's side alongside the P215 per-player
    death.

**Selection wiring** (the task's per-mode-selection question): identical to
P191/P302 — the port parses no win-mode mission params (`nGoalFlagsCount` /
`nGoalTeamKills` are not decoded into mission config), so the per-mission-type
selection is the P191-style enable flags, defaulting `false`. Flags/kills are
checked **only** when their flag is set — never unconditionally alongside
conquest/territory/prosperity/artifact.

## Verification

- **Whole-solution build**: all 11 projects, 0 warnings / 0 errors.
- **New tests** (10): `FlagsKillsTests` (6 domain) + `FlagsKillsLiveTests`
  (4 live). All pass.
- **Must-fail-if-removed**: disabling each trigger (the gated recompute raise)
  is exactly what the "disabled non-event" live tests assert — reverting the
  `FlagsEnabled`/`KillsEnabled` raise makes its live win-test red and the
  disabled tests' non-event assertions hold.
- **Live proof (real wire evidence)** — real in-process server + TCP bots:
  - flags: rescue ship picks up an enemy flag treasure (carried value = enemy
    side's id) and returns it to its own station → `Side.Flags` reaches the goal
    → `GAME_STATE(155)` then `Mission.Stage = Over`, `GAME_OVER(62)`
    `iSideWinner = the securing side`;
  - kills: a peer-owned killer destroys an enemy ship → `Side.Kills` reaches the
    deathmatch goal → `Mission.Stage = Over`, `GAME_OVER(62)` winner = the
    killing side;
  - both disabled: same actions are non-events — counters still increment but no
    `GAME_OVER`, stage stays `Started` (the must-fail-if-removed discriminator).
- **Full suite**: run 2× with P303; 3221 total (baseline 3211 + 10). Every run
  passes all 10 P303 tests; residual failures are pre-existing live-network
  timing flakes (turret-binding is the task's permitted flake; asteroids/
  stations/chat/cargo/botflight/gameover-players flake intermittently) —
  reproduced on the pristine baseline in isolation, i.e. not P303 regressions.
