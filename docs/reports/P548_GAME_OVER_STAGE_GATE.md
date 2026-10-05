# P548 — Game-over stage gate & auto-assign leader

Date: 2026-10-05 · Branch: `master` (not pushed) · Suite: 3× `scripts/run-tests.sh`

## Summary

Two related parity bugs, both verified live, both fixed and proven failing-first.

1. **"Stage Over fires on every peer drop"** — the port's win-by-inactive-sides
   check ran on *every* disconnect and ended the mission even in a **NotStarted
   lobby**. Native gates this behind `fDeactivate = (STAGE_NOTSTARTED !=
   stage) && !HasPlayers(psideOld, false)` (`FedSrv/fsmission.cpp:1147`), so a
   lobby drop never deactivates/ends. Fixed by porting native's stage +
   side-empty gate.
2. **Auto-assigned players never become team leader** — the auto-assign path
   set `SideId` directly (`Server.cs:753`) and skipped `TryJoinSide`, so the
   first player onto an empty side wasn't elected leader. Native funnels *every*
   placement (auto or explicit) through `AddPlayerToSide`, which elects the
   first player onto an empty side as leader (`fsmission.cpp:601-613`). Fixed by
   routing auto-assign through the same join path.

## Native references (all cited)

- `FedSrv/fsmission.cpp:1147-1149`
  `bool fDeactivate = (STAGE_NOTSTARTED != m_misdef.stage) && !HasPlayers(psideOld, false); if (fDeactivate) DeactivateSide(psideOld); else CheckForSideAllReady(psideOld);`
- `FedSrv/fsmission.cpp:5318-5427` `DeactivateSide` (deactivate → SIDE_INACTIVE
  → chat → `CheckForVictoryByInactiveSides` at `:5381`)
- `FedSrv/fsmission.cpp:3767-3788` `CheckForVictoryByInactiveSides`
- `FedSrv/fsmission.cpp:601-613` `AddPlayerToSide` leader election
  (`if (!HasPlayers(pside, true)) … SetLeaderID(sideid, shipid)`)
- `FedSrv/fsmission.cpp:4755-4761` `RequestPosition` → `AddPlayerToSide`
  (every placement flows through the same join path)

## Root cause (bug 1)

`CheckForVictoryByInactiveSides(Mission)` (`Server.Lobby.cs`) returned early
**only** when `mission.Stage == MissionStage.Over`, and `ReleasePeer`
(`Server.Lifecycle.cs:~1226-1230`) called it on **every** disconnect. So a drop
that left ≤ 1 side with players declared game-over **in a NotStarted lobby
too**. `DeclareGameOver` then rotated + migrated the survivors into a fresh
NotStarted mission, which the *next* drop ended again — one "Stage Over" per
drop (the live ×4).

## Fix (bug 1)

- `ReleasePeer` now computes native's gate and only acts when
  `mission.Stage != NotStarted && the departing side now has no players`
  (`CountPlayersOnSideExcluding(mission, departedSideId, live.Ship) == 0`), then
  calls the new `DeactivateSideForPeerDeparture(mission, side)`.
- `DeactivateSideForPeerDeparture` (`Server.Lobby.cs`): marks the side
  inactive, broadcasts `SIDE_INACTIVE(68)` + `<name> is no more.`
  (`fsmission.cpp:5371-5377`), then runs `CheckForVictoryByInactiveSides`
  (`:5378-5419`). It **keeps P388's debug-mode exemption** (a client dropping
  out of the unattended 0-player test arena is a non-event) — and deliberately
  does **not** use the station-elimination `DeactivateSide`/…`AfterDeactivation`
  (which ends even a debug mission). Ship-drone elimination (`:5331-5368`) is
  P455b scope, noted.
- `CheckForVictoryByInactiveSides` also returns early on
  `mission.Stage == MissionStage.NotStarted` (defense-in-depth for the stage
  gate).

No cascade: `DeclareGameOver` sets `Stage = Over` before broadcasting, the check
is idempotent on `Over`, and post-game-over drops land on the rotated
**NotStarted** mission where the gate refuses to fire.

## Fix (bug 2)

`CompleteSideSelection`'s auto-assign branch (`requestedSideId == -1`) now runs
`TryJoinSide(live.Ship, autoSide)` instead of leaving the round-robin `SideId`
un-electable — the same join path native's auto-assign uses
(`RequestPosition → AddPlayerToSide`). The first auto-assigned player onto an
empty side is now elected team leader (+ mission owner when applicable).

## Failing-first (demonstrated)

Ran against `master` (fix stashed), all 4 tests failed with exactly the bugs:

```
NotStarted_lobby_drop_produces_no_game_over:
  Assert.Equal() Failure … Expected: NotStarted  Actual: Over        ← lobby drop ended the mission
Started_last_player_of_side_drops_deactivates_side_and_one_game_over:
  Assert.False() Failure … Expected: False  Actual: True            ← side not deactivated
Four_drops_after_game_over_produce_no_further_game_overs:
  Assert.Equal() … Expected: NotStarted  Actual: Over               ← cascading ×4
Auto_assigned_first_player_on_empty_side_is_leader:
  Assert.Equal() … Expected: 8098  Actual: null                     ← auto-assign never leader
```

With the fix in place, all pass (5 tests incl. the loopback).

## Live loopback (quoted server log, `[gameover]`/"Stage Over" count)

Lobby (4 clients dropped one by one) → **0** `[gameover]` lines; started game
(4 clients dropped one by one, side emptied) → **exactly 1**.

```
[P548-LIVE] loopback: lobby 4 drops -> 0 game-overs (mission 'NotStarted'); started 4 drops -> 1 game-over(s).

[gameover] mission #0 -> Stage Over, iSideWinner=0, 2 peer(s)
[gameover] P439: mission #0 finished -> rebuilt fresh STAGE_NOTSTARTED (13 sectors, 2 sides, 38 warps, 2 stations, 0 asteroids); new joiners land here NotStarted
```

Post-game-over (the ×4 regression): 4 survivor drops after a real game-over →
**no** further `[gameover]` lines; fresh mission stays `NotStarted`:

```
[P548-LIVE] post-game-over: 4 survivor drops → fresh mission stays 'NotStarted', exactly 1 GAME_OVER(s) total.
```

Started single-side-empty: side deactivated + one game-over with native
reason/winner:

```
[sideinactive] side 1 deactivated (last player departed) => SIDE_INACTIVE(68) bActive=false -> 1 peer(s)
[sideinactive] side 1 departure elimination chat "Bravo is no more." -> 1 peer(s)
[gameover] mission #0 -> Stage Over, iSideWinner=0, 1 peer(s)
[P548-LIVE] started: last of side 1 (8099) dropped → side 1 Active=False, side 0 wins, exactly 1 GAME_OVER(s): "Alpha won by outlasting all other sides."
```

## Tests

`tests/Allegiance.Core.Tests/LiveP548GameOverStageGateTests.cs` (new, 5 tests):

- `NotStarted_lobby_drop_produces_no_game_over`
- `Started_last_player_of_side_drops_deactivates_side_and_one_game_over`
- `Four_drops_after_game_over_produce_no_further_game_overs`
- `Auto_assigned_first_player_on_empty_side_is_leader`
- `Live_loopback_four_clients_drop_in_lobby_then_started_game`

### Updated existing tests (P548 consequence, native-faithful)

Routing auto-assign through `TryJoinSide` makes every seated player a qualified
leader/owner (as native's `AddPlayerToSide` does). Four pre-existing tests
encoded the old non-native behavior (auto-assigned players were NOT
leaders/owners) and were updated, with the native rationale in each:

- `LiveTeamLeadershipTests.Multi_mission_leadership_scopes_per_mission` — mission
  B's first auto-assigned peer now legitimately owns mission B (was `null`).
- `LiveTeamLeadershipTests.Leader_last_with_no_other_qualified_side_…` → renamed
  `Owner_side_leader_leaves_migrates_owner_to_auto_elected_leader_and_ends_game`
  (an auto-assigned B is now a qualified leader, so ownership migrates to B and
  the game ends by inactivity, per native RemovePlayerFromSide fsmission.cpp:996-1028).
- `BallotVotingLiveTests.Multi_mission_ballot_scopes_per_mission_and_per_side` —
  the mutiny caster is now a NON-leader (second peer onto the side); the leader
  can't mutiny against itself ("leader cant mutiny", FedSrv.CPP:5186).
- `LiveLogoffTests.An_observing_peer_witnesses_loggedoff_ship_delete` — the
  leaver is moved to the other side so its departure genuinely empties a side
  (old code spuriously ended the game via the mission-wide tally even with both
  peers on one side).

### Full suite — `scripts/run-tests.sh` (P471), 3 consecutive runs on the final code

| run | Lobby.Tests | ClientCore.MonoGame | ClientCore.Tests | Core.Tests |
|-----|-------------|---------------------|------------------|------------|
| 1 | 12/12 | 1295/1295 | 488/488 | 3736/3739 |
| 2 | 12/12 | 1295/1295 | 487/488 | 3738/3739 |
| 3 | 12/12 | 1295/1295 | 488/488 | 3737/3739 |

Every failure is a **pre-existing documented load-flake** in `docs/TEST_FLAKES.md`,
all pass in isolation, none touches P548's changed code paths, and a *different*
random subset failed each run (the classic full-suite live-wire contention
signature P471 documents). Failures by run:

- Run 1: `LiveStationRescueTests.Lifepod_reaching…` (flake, line 156);
  `GameOverPlayersWireTests.Conquest_win_lists…` + `ConquestTerritoryLiveTests.Conquest_win_in_one_mission…` (ledger lines 29-31, "fixed" but re-flagged).
- Run 2: `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival…`;
  `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure…`.
- Run 3: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129…`;
  `AddPartTests.Part_pickup_decline_recreates_treasure…`.

None of P548's own tests, nor `LiveTeamLeadershipTests`, `LiveLogoffTests`,
`BallotVotingLiveTests`, `LiveP388DebugNoGameOverTests`,
`P480RotationKeepsPlayersLiveTests`, or `LiveWinConditionTests` failed in any run.
Whole-solution build: 0 warnings, 0 errors.

## Out of scope (deferred)

- Mission-scoped chat / payday across multiple configured games (multi-game only).
- `nMaxPlayersPerTeam` and `CHAT_ALLIES` (later).
- DeactivateSide's leftover-drone ship-kill for the peer-departure path (P455b).

## Files changed

- `src/Allegiance.Server/Server.Lobby.cs` — stage gate in
  `CheckForVictoryByInactiveSides`; new `DeactivateSideForPeerDeparture`;
  auto-assign via `TryJoinSide`.
- `src/Allegiance.Server/Server.Lifecycle.cs` — `ReleasePeer` gated by native's
  `fDeactivate`.
- `tests/Allegiance.Core.Tests/LiveP548GameOverStageGateTests.cs` (new).
- `tests/Allegiance.Core.Tests/LiveTeamLeadershipTests.cs`,
  `tests/Allegiance.Core.Tests/BallotVotingLiveTests.cs`,
  `tests/Allegiance.Core.Tests/LiveLogoffTests.cs` — updated to the
  native-faithful auto-assign behavior (see "Updated existing tests").
- `docs/reports/P533_SESSION_AUDIT.md` — dated correction note (row 51 was
  wrongly MATCH).
