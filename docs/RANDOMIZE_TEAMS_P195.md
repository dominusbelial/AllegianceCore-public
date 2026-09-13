# RANDOMIZE_TEAMS (P195) — rank-balanced side reassignment

Branch `master`, on top of P194. Adds the `RANDOMIZE_TEAMS`(21) wire trigger
and its real effect: the mission owner's "balance the teams" action — a
highest-rank-first greedy reassignment of non-leaders to whichever side
currently has the lowest total rank. Reads P193's `PlayerScore.Rank`; clears
P184's pending join requests; reuses P186's `IsMissionOwnerPlayer` gate and
P183's per-player side-switch broadcasts. Deliberately does **not** invent the
neutral `SIDE_TEAMLOBBY` the native algorithm flushes to.

## Relationship to native (all verified against the live source this session)

- **`Inc/Messages.h:201`** — `DEFINE_FEDMSG(C, RANDOMIZE_TEAMS, 21)` is a
  **zero-field** client (`C`) trigger (header comment: "randomly assigns
  everyone to a team"). Confirmed zero-field live; the codec is header-only
  (4 B), the same shape as `START_GAME`/`SUICIDE`. Direction `C` (not `CS`)
  ⇒ the sender is a client, so the server applies the owner gate.
- **`FedSrv/FedSrv.CPP:6964-6997`** — the real dispatch gate, read live:
  ```c
  case FM_C_RANDOMIZE_TEAMS:
  {
    if ((!pfsMission) || (pfsMission->GetOwner() != pfsPlayer)) break;
    if (pfsMission->GetStage() != STAGE_NOTSTARTED) break;
    // TE: Balance or Flush depending on the LockSides parameter mmf MaxBalance
    if (pfsMission->GetMissionDef()->misparms.iMaxImbalance == 0x7ffe)
        pfsMission->RandomizeSides();   // balance
    else
        pfsMission->FlushSides();       // just dump everyone to the lobby, no reassignment
    break;
  }
  ```
  **Who is allowed to trigger it (the real finding):** the mission owner —
  `GetOwner() != pfsPlayer` refuses everyone else, and `GetOwner()` is the
  owning side's leader (this port's `IsMissionOwnerPlayer`, P186's helper,
  reused directly, not re-derived). Plus `Stage == STAGE_NOTSTARTED`. The
  balance-vs-flush branch reads `iMaxImbalance` from the still-missing
  `MissionParams` sentinel `0x7ffe`.
- **`FedSrv/fsmission.cpp:5133-5260`** — `CFSMission::RandomizeSides`, read
  in full: (1) turns auto-accept off on every side (P186 `Side.AutoAccept`);
  (2) moves every non-leader to `SIDE_TEAMLOBBY` via `RemovePlayerFromSide(
  ..., QSR_BalanceSides)`; (3) clears every pending join request
  (`fsmission.cpp:5183-5196`); (4) the balancing loop (`fsmission.cpp:
  5197-5245`) repeatedly picks the highest-`Rank` non-AFK lobby player and
  `AddPlayerToSide`s them onto `PickNewSide`'s lowest-rank-sum side.
- **`FedSrv/fsmission.cpp:4454-4522`** — `PickNewSide`: lowest-`GetSideRankSum`
  active side not in the player's banned mask that `CheckPositionRequest`
  accepts, subject to the `iMaxImbalance` player-count override.
- **`FedSrv/fsmission.cpp:1353-1365`** — `GetSideRankSum`: sum of every
  player's `max(rank,1)`, skipping ghosts unless `bCountGhosts`.

## Stage-1 substitute for `SIDE_TEAMLOBBY` (the real architectural decision)

Native's two-phase "flush everyone to a neutral lobby, then greedily reassign
by rank" cannot be ported as-is because this port has **no lobby side** — every
ship is always seated on a real side. The buildable substitute, implemented in
`Server.RebalanceSides`, rebalances **directly across the existing real sides
in one pass**, without an intermediate flush:

1. Collect every non-leader seated on any real side of the mission — the
   rebalance pool (analogous to native's lobby population, just never
   physically moved anywhere first).
2. Repeatedly: pick the highest-`Rank` player remaining in the pool (ties
   broken by stable connection/iteration order via LINQ's stable `OrderBy`),
   assign them to whichever real side currently has the lowest rank sum
   **among the running per-side sums** (a side just assigned into immediately
   counts its new member for the next pick — matching native's live
   recomputation via `AddPlayerToSide` before the next loop iteration).
   Team leaders are never moved; their side's rank contribution is anchored in
   the running sum from the start (`GetLeaderRankSum`, a single-side
   restriction of native `GetSideRankSum`).
3. Continue until every non-leader has been reassigned.

The **end state** — players redistributed to minimize per-side rank-sum
imbalance — is equivalent to native; the **mechanism** — no visible "everyone
stripped to a lobby" intermediate state — is not. This is a deliberate,
permanent simplification (not a TODO), stated plainly in code and here.

## What changed

1. **`src/Allegiance.Core/Wire/RandomizeTeams.cs`** (new) — the zero-field
   `C`-direction codec: `Fmid=21`, `FixedSize=4`, modeled on `StartGame.cs`.
   The `FedMessageDumper` already labelled `[21]="RANDOMIZE_TEAMS"`, so no
   dumper change was needed.
2. **`src/Allegiance.Server/Server.cs`**
   - Dispatch case `fmid == RandomizeTeams.Fmid`, inserted with the other
     lobby-era owner triggers: refuse unless the sender is the mission owner
     (`IsMissionOwnerPlayer`) and `Mission.Stage == NotStarted`, else call
     `RebalanceSides`. The real native dispatch also reads `iMaxImbalance` to
     choose balance-vs-flush; since `MissionParams` is missing this port
     **always balances (never flushes)** — `FlushSides` (dump everyone to a
     neutral lobby with no reassignment) has no meaningful analogue anyway,
     and "balance" is what the message's own name promises.
   - **`RebalanceSides(Mission)`** (new) — the Stage-1 substitute above:
     clear the mission's pending join requests first (native
     `fsmission.cpp:5183-5196`), seed per-side running sums from the leaders'
     `max(Rank,1)`, then highest-rank-first greedy placement into the
     lowest-sum side, moving each player via the existing `TryJoinSide`
     (P183) — which already broadcasts `QUIT_SIDE`/`JOIN_SIDE`/
     `SET_TEAM_LEADER`/`SET_MISSION_OWNER` per player. **No new broadcast
     shape is invented** for the reassignment.
   - Helpers: `IsSideLeader` (non-leader pool filter; native `GetLeader(sideID)
     != pfsPlayer`), `GetLeaderRankSum` (single-side `GetSideRankSum` port,
     leaders only, floor 1), `ClearPendingJoinRequestsForMission` (mission-
     scoped P184 clear; pending entries are connection-keyed, so the mission
     filter maps each key to its live ship).
3. **`src/Allegiance.Bot/Bot.cs`** — `SendRandomizeTeams()` mirroring
   `SendStartGame()` (the wire path the tests drive).

## The `LockSides` decision (explicit)

**Decision: `RANDOMIZE_TEAMS` BYPASSES `Mission.LockSides` — it is not refused
while sides are locked.** Justification, grounded in native: `RandomizeSides`
never consults `bLockSides` — it does not even touch `LockSides` (`SetLockSides
(true)` is *commented out* there with the note "Randomize should not touch
sides!", `fsmission.cpp:5258-5259`), and the dispatch gate (FedSrv.CPP:6964)
reads only owner + stage. The lock guards ordinary player-initiated
side-switches (`CheckPositionRequest`'s `bLockSides` gate); a privileged,
owner-only, NotStarted-only mass reassignment is a different class of action
and native treats it as such. Proven in
`Randomize_teams_bypasses_lock_sides`.

## Divergences from this prompt's assumptions (verified, not guessed)

- The prompt hedged the sender/direction ("if the sender/direction is `C` not
  `CS`, decide the owner-gate question"). Live `Messages.h:201` confirms
  direction **`C`**, and native's dispatch owner-gates it anyway — so the
  port owner-gates on `IsMissionOwnerPlayer`, the natural and native-faithful
  reading. (No divergence; the direction question is now answered from the
  source.)
- Everything else in the prompt's "out of scope" list held against the live
  source: no `SIDE_TEAMLOBBY`, no `iMaxImbalance` override, no banned-side
  mask, no AFK/ready concept (every non-leader is rebalanced), no
  ghost/spectator concept — each stated as a deliberate simplification in the
  code docs. The brief's Stage-1 substitute was followed as specified; no
  positive/negative claim in the brief needed correcting this session.

## Tests — `tests/Allegiance.Core.Tests/RandomizeTeamsLiveTests.cs` (8 new)

- `RandomizeTeams_round_trips_exact_bytes` — codec: fmid 21, 4 B, exact bytes.
- `Rebalance_distributes_non_leaders_to_balance_rank_sums` — ⭐ the
  quantitative must-fail-if-removed proof. Two sides, uneven populations,
  varied ranks. Leaders A(5)/B(1) anchor their sides; pool C(10), D(9), E(8)
  placed greedily → side0 = 5+9 = 14, side1 = 1+10+8 = 19 (mean 16.5, each
  within one player's worth; imbalance 5 < largest rank 10), versus the
  pre-rebalance 24/9. Removing the rebalance leaves 24/9 and every assertion
  fails (verified: 5 tests red with the feature neutralized).
- `Team_leaders_are_never_reassigned_during_rebalance` — high-rank leaders
  stay put while non-leaders split around them (asserts the non-leaders
  actually moved too, so it is non-vacuous).
- `Rebalance_clears_pending_join_requests` — a real pending request (P184,
  created via a wire `POSITIONREQ` to a led side) is gone after the rebalance
  (inspected via reflection on `_pendingJoinRequests`).
- `Randomize_teams_refused_when_not_mission_owner` — a side leader who is not
  the owner is refused (nothing moves); the owner's send is then honoured.
- `Randomize_teams_refused_when_mission_started` — `Stage != NotStarted`
  refuses.
- `Randomize_teams_bypasses_lock_sides` — with `LockSides=true`, the owner's
  rebalance still reaches the exact 14/19 split (proves the bypass decision).
- `Multi_mission_rebalance_scopes_per_mission` — a rebalance in mission A
  never touches mission B's sides/leaders/rank sums (reuses the established
  per-mission-scoping proof pattern).

## Verification

- `/home/domingo/.dotnet/dotnet build` clean (0 warnings, 0 errors) across the
  solution.
- New tests pass in isolation: 8/8; with the feature neutralized (probe
  `RebalanceSides` to an early `return`), 5 of the 8 correctly go red, then
  reverted.
- Full-suite baseline moved **2965 → 2973** (P194 baseline 2965 + the 8 above).
- 30 full-suite runs total; 16 fully clean. Every failure in the other runs was
  a documented environmental live-network flake (`LiveCombatWireTests.*`,
  `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`,
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `LiveWinConditionTests.*`, `GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side`).
  Each was confirmed environmental the skill's way: it passes in isolation and
  its file has zero references to any P195 symbol. The final three runs (28, 29,
  30) were **fully clean consecutively at 2973 (0 failures each)**.

## Simplifications restated for the reader

This task implements "balance, never flush" because this port has no lobby
side and no `MissionParams.iMaxImbalance` sentinel; rebalances every non-leader
(no AFK concept); treats every real side as eligible (no banned-side mask);
anchors leaders' `max(Rank,1)` in the sums (no ghost concept); and relies on
P193's neutral `Rank=0` seed until something else ever differentiates ranks —
per P193's documented limitation. `LockSides` is bypassed per the decision
above. None of these is a silent workaround; each is a named simplification.
