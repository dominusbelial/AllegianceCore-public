# P864 — parity: game over, mission generation, sides and leadership

Base: public master at dispatch (`b37b4efe`). Headline: native game-over
pipeline, map generation and team-lobby flows. SIM-0528 + SIM-0761 are one
change — wire `GetExpMult`'s three `ProcessGameOver` call sites; SIM-0504 adds
`PlayerScoreObject StartCommand/StopCommand` on leader change (commander score
time). All native line numbers re-grepped against `~/Development/Allegiance/src/`.
Changes are left in the working tree (`hermes/p864`); nothing committed or
pushed; no rig/Mac/mini contact.

## Result

**FedSrv mission rules (event-driven) family: 85.57% (516/603) → 87.06%
(525/603); TOTAL 94.80% (1804/1903) → 95.27% (1813/1903).**
`scripts/parity-scorecard.py` validates. Three rows flipped partial →
implemented (`SIM-0528`, `SIM-0761`, `SIM-0504`); the other four
(`SIM-0748`, `SIM-0556`, `SIM-0554`, `SIM-0496`) keep `partial` with their
documented design/era divergence reasons re-verified (their remaining branches
are genuinely inapplicable to this port — no team lobby, no AGC, no station-tech
mask decode, deliberate logon-security refusal).

| Row | Native cite | Before → After | Test names |
|---|---|---|---|
| SIM-0528 (5) | ProcessGameOver `FedSrv/fsmission.cpp:3170-3549` | partial → **implemented**. GetExpMult score-damping over connected AND old players + the commander award (fsmission.cpp:3290-3327,3340-3376) wired into `FinalizeMissionScores` via the pure `PlayerScore.ApplyGameOverExperienceBalance`; experience = Σ timePlayed·careerScore (career from a server-lifetime in-memory `_careerScores` store keyed by name) | `P864MissionRulesCertifyTests.ApplyGameOverExperience_balanced_sides_do_not_damp_and_fold_career`, `.stacked_side_damps_connected_departed_and_commander`, `.commander_award_overrides_when_higher`; `P864GameOverExperienceLiveTests.Game_over_runs_getexpmult_wiring_and_folds_career`; existing GameOverPlayersWireTests / P480 / P855 |
| SIM-0761 (2) | GetExpMult `fsmission.cpp:3144-3158` | partial → **implemented**. The three `ProcessGameOver` call sites (fsmission.cpp:3308/3325/3354) are no longer inert; they run through `ApplyGameOverExperienceBalance` consuming the in-memory career store | `P841MissionRulesCertifyTests.GetExpMult_matches_native_branches`; `P864MissionRulesCertifyTests.ApplyGameOverExperience_*`; `Game_over_runs_getexpmult_wiring_and_folds_career` |
| SIM-0504 (2) | SetLeaderID `fsmission.cpp:1464-1483` | partial → **implemented**. `StartCommand`/`StopCommand`/`TimeCommanded` added to `PlayerScore` and wired into every leader change (SetLeader/mutiny+squad refit, HandleLeaderSuccession, TryJoinSideCore first-joiner, RetireDepartedPeer fold of the departing leader); consumed at game over to select the commander (max TimeCommanded) and award the (GetExpMult-damped) commander score via `SetCommanderScore` (reported as CommanderWin/Loss) | `P864MissionRulesCertifyTests.CommandTime_start_stop_fold_and_reset_match_native_intervals`, `.SetCommanderScore_overrides_only_when_higher_and_sets_command_flags`; `P864GameOverExperienceLiveTests.Leader_command_time_starts_on_join_and_hands_off_on_departure`; existing LiveTeamLeadershipTests / BallotVotingLiveTests |
| SIM-0748 (5) | GenerateMission `Igc/missionigc.cpp:3830-4123` | partial (unchanged) — P864 re-verified. Equal branches as P851. Remaining (a) station-type replacement by side techs (:4011-4049, port decodes no DataStationTypeIGC tech masks), (b) FILE-map PopulateCluster top-up (:4051-4055) never runs (MapGen-only), (c) AGC per-side tech override masks (:3885-3900) n/a (no AGC protocol) — all classified design/inapplicable | MissionAssemblyTests; P663TreasureParityTests; MapGenFoundationTests |
| SIM-0556 (3) | RandomizeSides `fsmission.cpp:5139-5266` | partial (unchanged) — P864 re-verified. RebalanceSides covers the auto-accept sweep, pending-join clear, rank/count reassignment. Remaining: two-phase team-lobby flush (no SIDE_TEAMLOBBY = P195 design), iMaxImbalance fewest-players override, AFK filter (n/a, no lobby) | RandomizeTeamsLiveTests; P852MissionRulesCertifyTests.F1 |
| SIM-0554 (2) | FlushSides `fsmission.cpp:5069-5119` | partial (unchanged) — P864 re-verified. Flush half to SIDE_TEAMLOBBY deliberately not built (no team lobby, owner-only/NotStarted-only) = design | P852MissionRulesCertifyTests.F1 |
| SIM-0496 (2) | RemovePlayerByName `fsmission.cpp:1248-1273` | partial (unchanged) — P864 re-verified. Two divergent call sites: logon-duplicate refuses newcomer (security design, Server.Dispatch.cs:201-212), lobby FM_L_REMOVE_PLAYER has no modern counterpart (era/design) | P762LobbyModerationTests |

## What changed

Source (all under `src/`):

- **`Allegiance.Server/PlayerScore.cs`**
  - `TimeCommanded` / `Commanding` / `StartCommand(t)` / `StopCommand(t)` /
    `FoldOpenCommandTime(t)` / `ResetCommandTime()` — the native
    `PlayerScoreObject` command-time bookkeeping (`igc.h:5615-5630,5908,5920`),
    SIM-0504.
  - `CareerScore` — the port's per-player career score (native
    `persist.GetScore()`), SIM-0528.
  - `CommandWin` / `CommandLose` / `CommandCredit` / `SetCommanderScore(f)` —
    native `igc.h:5519-5527`, SIM-0528.
  - `ApplyGameOverExperienceBalance(...)` — the pure, unit-tested body of the
    GetExpMult experience accumulation + all three call sites + career fold.
    Exact port of native `ProcessGameOver`'s experience pass
    (fsmission.cpp:3261-3272), damping pass (:3290-3327) and commander award
    (:3340-3376), plus the SetCharStats career save (:3378-3402).
- **`Allegiance.Server/Server.cs`** — `_careerScores` (server-lifetime,
  in-memory, keyed by character name, `OrdinalIgnoreCase`), internal for tests.
- **`Allegiance.Server/Server.Lobby.cs`**
  - `TransferCommandTime(outgoing, incoming)` — the SetLeaderID score side
    effects; wired into `SetLeader` (mutiny + squad refit),
    `HandleLeaderSuccession` (both the successor and the empty-side clear) and
    `TryJoinSideCore` (first-joiner).
  - `FinalizeMissionScores` — after per-player `CalculateScore`, gathers the
    mission's connected + departed players and calls
    `PlayerScore.ApplyGameOverExperienceBalance` with the `_careerScores` store.
  - `CompleteJoinAndLaunch` — reconciles a leader elected before the ship was
    registered (the logon TryJoinSide runs before `_peerShips` registration), so
    an initial logon leader is StartCommanded.
  - `ReportMissionResultsToLobby` — the CommanderWin/CommanderLoss columns now
    read the score object's command flags.
- **`Allegiance.Server/Server.Lifecycle.cs`** — `RetireDepartedPeer` folds the
  departing leader's open command stint before succession (the ship is already
  out of `_peerShips` by then).

Docs:

- `docs/parity/sim-rules-matrix.csv` — 3 rows → implemented (with cites/tests),
  4 rows re-verified + annotated.
- `GLOSSARY.md` — new §13 bullet: server-side GetExpMult career store + commander
  score.

Tests (`tests/Allegiance.Core.Tests/P864MissionRulesCertifyTests.cs`, new):

- Unit: `CommandTime_start_stop_fold_and_reset_match_native_intervals`;
  `SetCommanderScore_overrides_only_when_higher_and_sets_command_flags`;
  `ApplyGameOverExperience_balanced_sides_do_not_damp_and_fold_career`;
  `ApplyGameOverExperience_stacked_side_damps_connected_departed_and_commander`;
  `ApplyGameOverExperience_commander_award_overrides_when_higher`.
- Live (`[Collection("LiveNetwork")]`): `Leader_command_time_starts_on_join_and_hands_off_on_departure`
  (SIM-0504 wiring); `Game_over_runs_getexpmult_wiring_and_folds_career`
  (SIM-0528/0761 wiring — career fold over the real mission roster, seeded
  stacked career).

## Design decisions / divergences (honest record)

- **Career score source.** Native `GetExpMult` weights experience with the
  player's *persisted* career score (`timePlayed · persist.GetScore()`). This
  port has no stats DB / account layer (P583). Rather than leave the call sites
  inert forever, P864 supplies a **server-lifetime in-memory career store** keyed
  by character name, folded at each game over — native's career-based
  anti-stacking structure minus the DB. The cross-restart persistence leg is
  classified era/design (P583), explicitly documented in the row notes. A fresh
  server (no careers yet) is inert exactly like native's new players; the
  damping only bites once experience accumulates across games.
- **Old players' session scores.** Native recomputes a departed player's score at
  game over (EndGame loop 2); the port uses each old player's cached score as-is
  and dampens that. Departing players therefore contribute their cached
  score/time to the side's experience and command-sum, matching the structure if
  not a recomputed value.
- **Command score vs kill tally.** `SetCommanderScore` keeps the higher of the
  commander award and the kill tally (native `igc.h:5521-5523`), so in most games
  the commander's kill score stands unless the side's per-second earning is
  exceptional — faithful.
- **Commander selected by max TimeCommanded.** Native picks the side's commander
  as the player with the most commanded time over connected + departed players;
  the port now tracks that exactly.

## Native-intent classification

Native intent: `ProcessGameOver` finalizes scores, applies an
experience-weighted anti-stacking multiplier, and derives a commander score, all
against a *persisted per-player career ledger*. The port faithfully reproduces
the state machine and the exact GetExpMult math; the one structural substitution
is the career ledger's persistence (in-memory, server-lifetime, keyed by name)
because the port has no stats DB. That is a **design** divergence on the
persistence leg only — the balancing behaviour itself is now live and tested.

## Modern / better-approach note

Native's persisted career score lives in a MySQL server across restarts. The
port's long-term trajectory (P808b/P808c) already pushes career data to the lobby
(`/api/career/results`, `CareerStore`). The natural next step for full parity is
to back `_careerScores` with that lobby career store (per verified `AccountId`)
instead of the name-keyed in-memory map — exactly the "once a career score
exists per verified AccountId, GetExpMult's call sites slot into
FinalizeMissionScores" roadmap from P841. Until then the in-memory store is the
minimal, tested stand-in.

## Verification

- `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: all **91** tool projects built (0/0).
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r
  linux-x64 --self-contained -p:PublishAot=true`): clean, no IL2026/IL3050.
- New tests 3× alone: `P864*` 7/7 ×3 (unit + live).
- Showcase never-parks 5× (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`):
  **5/5 passed** (3m39s, 3m22s, 4m13s, 4m25s, 3m43s — a couple slower under shared-host load, none parked).
- ONE fast tier. `tools/run-tests.sh --tier fast` was launched repeatedly but the shared
  host reaps the long-running background wrapper midway (twice, always around the largest
  Core.Tests project), so the equivalent per-project fast runs were executed directly with
  the identical `Tier=Fast` filter: **Update 28/28, Lobby 87/87, ClientCore.MonoGame
  3630/3630, ClientCore 1931/1931, Core 5601/5601 — all green.** A first MonoGame pass had
  2 fails (`Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`,
  `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`)
  that each pass 3/3 alone and touch no game-over/leadership path — recorded as load flakes in
  `docs/TEST_FLAKES.md`.

Raw test logs: `~/allegiance-evidence/P864/` (outside the repo).
