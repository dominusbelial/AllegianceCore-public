# Prosperity + Artifact win conditions (P302)

2026-09-20 · lands on `master` (not pushed — Domingo pushes)

Implements two of the four remaining native game modes (prosperity + artifact)
as the sibling of P191's conquest/territory win conditions, on the **same
single game-over path**. Flags and kills are explicitly NOT built here — that
is the queued P303 task.

## Relationship to native (every claim cited)

Native's 6 win-condition modes live at `src/FedSrv/fsmission.cpp:2330-2503`;
each mode also has its own everyday (non-countdown) trigger. Two are done here.

### Prosperity — economic-threshold win (completing the team-money dev)

- The real prosperity win is **not** a wealth counter compared against a goal;
  it is a special **development**. On entering the game, each side is handed a
  bucket wrapping the `c_didTeamMoney` development (`c_didTeamMoney = 1`,
  `src/Igc/igc.h:512`), priced at `fGoalTeamMoney × WinTheGameMoney`
  (`src/Igc/missionigc.h:770-794`, `c_fcidWinTheGameMoney = 12`, `igc.h:72`).
  The side that **completes** that bucket wins — `FedSrvSiteBase::DevelopmentCompleted`:
  `if (pdev->GetObjectID() == c_didTeamMoney) GameOver(pside, "…proven the economic
  value of their outpost.")` (`src/FedSrv/FedSrv.CPP:11274-11279`).
- `DevelopmentLogic.IsTechOnly` treats `c_didTeamMoney` as never tech-only
  (`src/Igc/developmentigc.cpp:32-47`) — it is not filtered out of the bucket
  catalog. The on-disk PCore001 decodes a development with `DevelopmentId == 1`
  ("Reinforcement", measured; not tech-only/obsolete), so its real bucket is
  present and completable through the existing P189/P119 fund+build pipeline.
- The strict `fsmission.cpp:2395-2420` block is the *countdown-expiry* tie-break
  variant of prosperity (highest team-money dev `GetTime()`), not the everyday
  win condition; the everyday trigger is the completion in `DevelopmentCompleted`.

### Artifact — secure-`nGoalArtifactsCount` win

- Securing at one's own station a carried value equal to `SIDE_TEAMLOBBY`
  (`-2`, `src/Igc/igc.h:514`) increments `pside->SetArtifacts(+1)` and calls
  `CheckForVictoryByFlags(pside, sidFlag)` (`src/FedSrv/FedSrv.CPP:13113-13162`).
- The artifact leg of `CheckForVictoryByFlags`
  (`src/FedSrv/fsmission.cpp:3888-3894`) wins when
  `psideTest->GetArtifacts() >= pmp->nGoalArtifactsCount`.

## Scope boundary / exclusions

- **Flags** (score secured enemy flags, `fsmission.cpp:3883-3887`) and **kills**
  (countdown tie-break, `fsmission.cpp:2468-2492`) are **out of scope** — queued
  as P303. `Side.Flags` exists but the flags win is not wired here.
- **Conquest/territory (P191)** untouched — not modified.
- **Native reference** (`~/Development/Allegiance`) read-only — never edited.

## Implementation

Follows P191's exact architecture (never a second game-over mechanism):

- `Model/Mission.cs`
  - New flags/defaults (stated Stage-1 substitutes, exactly like P191's —
    this port parses no win-mode mission params): `ProsperityEnabled`,
    `ArtifactEnabled`, `ArtifactWinGoal` (= native `nGoalArtifactsCount`),
    `TeamMoneyDevelopmentId = 1` (= `c_didTeamMoney`, `igc.h:512`).
  - `RecomputeProsperityAndArtifacts() : (Side? Winner, bool Changed)` — the
    stage-gated sibling of `RecomputeConquestAndTerritory`. Prosperity branch:
    winner = the side whose `c_didTeamMoney` bucket is `Completed`
    (`FedSrv.CPP:11274-11279`). Artifact branch: first side with
    `Artifacts >= ArtifactWinGoal` (`fsmission.cpp:3890`).
- `Sim/IIgcSite.cs` + `Server/ServerHost.cs` — `ProsperityArtifactComputed`
  seam/hook, mirroring `VictoryByStationsComputed`.
- `Server/Server.cs`
  - `OnProsperityArtifactComputed` (mirrors `OnVictoryByStationsComputed`):
    GAME_STATE(155) on change + `DeclareGameOver` on winner (P178 path).
  - **Prosperity trigger**: `CompleteDevelopment` step 4 — when the completed
    dev is `TeamMoneyDevelopmentId` and `ProsperityEnabled`, recompute + raise
    the seam (`FedSrv.CPP:11274-11279`).
  - **Artifact trigger**: `OnShipDocked` secure leg — when the secured value is
    the artifact sentinel and `ArtifactEnabled`, recompute + raise the seam
    (`fsmission.cpp:3888-3894`).
  - Corrected the P201-era comments that called the artifact branch
    "unreachable" — P302 makes it reachable via a neutral artifact treasure.

**Selection wiring** (the task's per-mode-selection question): the port tracks
no win-mode mission params anywhere (verified — `MissionDef` is a pure wire
codec; `fGoalTeamMoney`/`nGoalArtifactsCount` are not decoded into mission
config), so the per-mission-type selection is the P191-style enable flags,
defaulting `false`. Prosperity/artifact are checked **only** when their flag is
set — never unconditionally alongside conquest/territory.

## Verification

- **Whole-solution build**: all 11 projects, 0 warnings / 0 errors.
- **New tests** (9): `ProsperityArtifactTests` (5 domain) +
  `ProsperityArtifactLiveTests` (4 live). All pass.
- **Must-fail-if-removed**: temporarily reverting each trigger makes its live
  test red (verified both: dev-completion win and artifact-secure win).
- **Live proof (real wire evidence)**:
  - prosperity: real in-process server + TCP bot — fund + build the real
    `c_didTeamMoney` bucket → `Mission.Stage = Over`, `GAME_OVER(62)`
    `iSideWinner = the completing side`;
  - artifact: rescue ship picks up a neutral artifact treasure (carried value
    = sentinel) and returns it to its own station → `Side.Artifacts` reaches
    the goal → `Mission.Stage = Over`, `GAME_OVER(62)` winner = the securing
    side;
  - both disabled: same actions are non-events — no `GAME_OVER`, stage stays
    `Started`.
- **Full suite**: run 4× with P302 (Total 3211; baseline 3202 + 9). Every run
  passes all 9 P302 tests. Residual failures are pre-existing live-network
  timing flakes (turret-binding is the task's permitted flake; asteroids/
  stations/chat/cargo/botflight/gameover-players also flake intermittently) —
  reproduced on the pristine baseline in isolation, i.e. not P302 regressions.
