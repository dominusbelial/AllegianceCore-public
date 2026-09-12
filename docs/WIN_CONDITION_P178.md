# WIN_CONDITION (P178) — win-by-elimination detection + simplified GAME_OVER(62)

Branch `master`, on top of `296b6d9` (P177, deployment polish). Adds the one
native win condition that needs no new foundational subsystem — **win by
elimination**: a mission ends when all but one (or all) of its sides are left
with no players, and the outcome is announced with `FM_S_GAME_OVER(62)`.
Before this task the port's `Mission.Stage` was set to `Started` at boot
(`Server.cs:457`) and never transitioned to the already-defined `Over` value —
the game literally never ended.

## Background / relationship to native

The check is native `CFSMission::CheckForVictoryByInactiveSides`
(`FedSrv/fsmission.cpp:3767-3788`): a side "has players" iff it has at least
one non-ghost, player-piloted ship (`HasPlayers`, `fsmission.cpp:1422-1431`);
more than one qualifying side ⇒ no decision yet, exactly one ⇒ that side wins,
zero ⇒ draw. The trigger site is the "a side just went empty of players"
handler (inside `DeactivateSide`, `fsmission.cpp:5318`), which after emptying a
side calls the check and, on `bAllSidesInactive`, `GameOver(NULL, "All sides
were destroyed")` or, on exactly one side left,
`GameOver(psideWin, "<name> won by outlasting all other sides.")`
(`fsmission.cpp:5381-5419`). `GameOver` itself
(`fsmission.cpp:2691-2711`) only records the decision; the wire broadcast is
`QueueGameoverMessage` (`fsmission.cpp:3059-3142`), building the per-side
`SideEndgameInfo` records, `iSideWinner` (`NA` on a draw), `nNumSides`, the
`szGameoverMessage` reason string, and `bEjectPods`.

This port's hook is `Server.ReleasePeer` — the single path both a graceful
`LOGOFF` and an abrupt transport drop flow through. After the departing peer's
ship is retired, the mission's remaining player-by-side is tallied and the
decision is taken.

## What changed

- **`src/Allegiance.Server/Server.cs`**
  - In `ReleasePeer`, after `RetireShipOnDisconnect(live)` (the real-departure
    path only, never the idempotent early return), when the departing ship has
    a `Mission` and a `SideId`, calls the new `CheckForVictoryByInactiveSides`.
  - New `private void CheckForVictoryByInactiveSides(Mission)` — the port of
    the native check:
    - Returns immediately if the mission is already `MissionStage.Over`
      (idempotency: an ended mission never re-broadcasts on a later disconnect).
    - Tallies, among `_peerShips`, the distinct `SideId`s whose ship's
      `Mission` is *this* mission (scoped per-mission — a server hosts multiple
      missions; peers of other missions are neither counted nor addressed).
    - `>1` qualifying sides ⇒ return (game continues); exactly one ⇒ that side
      wins; zero ⇒ draw (`"All sides were destroyed"`).
    - Sets `mission.Stage = MissionStage.Over` (this is what makes the
      scattered `mission.Stage != MissionStage.Started` gates refuse subsequent
      gameplay automatically), builds the `GAME_OVER` via
      `GameOver.ForMission`, and broadcasts to every peer whose ship belongs to
      this mission (the mission-scoped sibling of the whole-mission audience
      `BroadcastAsteroidDestroyed`/`BroadcastStationDestroyed` use).
- **`src/Allegiance.Core/Wire/GameOver.cs`** (new) — the codec.
  - `SideEndgameInfo` record (60-byte natural-aligned native layout, see the
    Color resolution below) + `GameOver` message (id 62) with the
    `FM_VAR_ITEM(szGameoverMessage)` first, then the fixed
    `rgSides[6]` array, `iSideWinner`, `nNumSides`, `bEjectPods`, then the
    trailing reason-string blob. `Encode`/`Decode` + `ForMission` factory.
- **`tests/Allegiance.Core.Tests/LiveWinConditionTests.cs`** (new) — 10 tests:
  2 codec (round-trip + the layout/Color-width pin), 5 live elimination
  scenarios, 2 live Stage-gate confirmations (with controls).
- **5 pre-existing NPC-AI test harnesses** — the P178 zero-player draw
  collided with a common pattern where a `Boot(...)` helper seats one FltBot to
  bootstrap a mission and then *disposes* it before running NPC-AI assertions.
  Under the now-correct end-of-mission semantics, disposing the only player
  empties the mission → draw → `Over` → the sim freezes for the NPC assertions.
  Fixed by keeping the bot seated for the test's lifetime (mission stays
  Started) in `NpcWingmanRangeMaintenanceLiveTests`,
  `NpcWingmanLeadLiveTests`, `NpcWingmanDefendLiveTests`,
  `NpcWingmanAttackLiveTests`, `NpcPilotTypeGateLiveTests` — and parking that
  boot bot in the *last* sector in `NpcPilotTypeGateLiveTests` so it stays a
  live player without becoming an enemy target in the sector under test (the
  same separate-sector pattern the other Wingman tests already use for their
  negative control).

## The open question: how `Color`'s width was actually resolved (verified)

The task prompt flagged `SideEndgameInfo.color`'s byte width as unresolvable and
guessed "3 bytes RGB or 4 bytes RGBA". It is **neither — it is a 16-byte RGBA
float color.** Resolution, all from the native tree:

1. `Messages.h` (which defines `SideEndgameInfo` at `:453-462`) includes
   `<igc.h>` (`Messages.h:14`).
2. `Igc/igc.h` includes `<color.h>` (`igc.h:17`) — the *single* `class Color`
   in the tree, at `zlib/color.h:16`, holding four `float` members
   (`m_r,m_g,m_b,m_a`, `color.h:18`).
3. Consumers confirm this is the wire type, not a packed form: the server
   writes `pside->GetColor()` — a zlib `Color` — straight into the field
   (`fsmission.cpp:3073`), and the client returns the field directly as a
   render color, `GetEndgameSideColor` returning
   `m_vsideEndgameInfo[sideId].color` as a zlib `Color` (`trekigc.cpp:5438`).

So `Color` = 16 bytes. Combined with **natural (non-packed) alignment** — the
only `#pragma pack` regions in `Messages.h` are `:636-642` and `:917-925`,
neither enclosing GAME_OVER at `:465-472`, and the record is memcpy'd verbatim
(`trekigc.cpp:5365` memcpy `sizeof(SideEndgameInfo) * c_cSidesMax`) — the record
layout under MSVC default alignment (char 1 / short 2 / float 4) is:

```
  offset 0:  char  sideName[25]
  offset 25: — pad (1) — align the civID short to even
  offset 26: short civID
  offset 28: Color — 4 × float (r@28, g@32, b@36, a@40)
  offset 44: short cKills
  offset 46: short cDeaths
  offset 48: short cEjections
  offset 50: short cBaseKills
  offset 52: short cBaseCaptures
  offset 54: short cFlags
  offset 56: short cArtifacts
  offset 58: — tail pad (2) — struct align 4 ⇒ sizeof = 60
```

This natural-alignment replication (explicit pad bytes) is exactly the
convention the port already uses for `DataShipIGC` in `NewShipInstance.cs`
(char[25] then a pad before a misaligned short), so no new convention is being
introduced. The layout test in `LiveWinConditionTests` pins `SideEndgameInfo`
= 60 bytes, the pad at record-offset 25, the 16-byte Color, and the message
`FixedSize` = 372.

## Divergences from the prompt's assumptions (verified, not guessed)

- **`Color` is 16 bytes (4× float RGBA), not 3/4 bytes** — see above. This is
  the prompt's open question, now closed with citations rather than a guess.
- **Per-side `Color` is a neutral gray for every real side.** Native writes
  `pside->GetColor()` (a real per-team color). This port has no `Side.Color`
  concept (a `Side` is `Id/Name/Civilization/Buckets/Money`), and no color
  subsystem is invented for this message — so every side record carries the
  native absent-side fallback `Color(0.5,0.5,0.5)` (`fsmission.cpp:3073`).
- **`bEjectPods` is always `false`** — the port has no mission-param config
  field for it (native reads `misparms.bEjectPods`). Stated plainly, not a
  silent default.
- **Per-side stat counters are all zero, honestly** — the port tracks no
  kills/deaths/ejections/base-captures/flags/artifacts; this continues the
  established honest-zeroing pattern in `ExportShip`/`NewShipInstance`/
  `PlayerInfo`. `GAME_OVER_PLAYERS`(63) is untouched (needs per-player
  `PlayerScoreObject` stats — a whole subsystem not built).
- **The zero-player draw now (correctly) ends a mission**, which required the
  5 NPC-AI test-harness adjustments above. This is a genuine behavioural change
  the prompt's spec (test #3: last player leaves → draw → `Over`) mandates and
  that native itself exhibits (`DeactivateSide` → `bAllSidesInactive` →
  `GameOver("All sides were destroyed")`); the pre-existing harnesses
  incidentally violated it by tearing down their only player mid-test.
- **Stage gates verified live, not assumed.** Two representative gates were
  confirmed with real control/refusal tests: `HandleSuicide` (server
  refuses when `Stage != Started`; control proves the same lifepod ship *does*
  die while Started) and `HandleBoardShip` (refusal produces `BOARD_NACK`;
  control proves the same docked pair boards while Started). Grep confirms nine
  `mission.Stage != MissionStage.Started` gates in `Server.cs` (suicide, board,
  order-change, swap-part, wing-id, turret, ripcord, perform-leave, etc.); no
  gameplay action that *should* refuse post-game-over was found to lack one.
- **Reason/winner strings** follow native (`<name> won by outlasting all other
  sides.` / `All sides were destroyed`); the allied-name-list branch
  (`fsmission.cpp:5398-5418`) is N/A (no alliance concept).

## fmid coverage

Distinct `Fmid` constants in `Wire/` rose **80 → 81 / 136** (verified by
counting; `GameOver.cs` supplies fmid 62).

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Tests, 0 W, 0 E).
- New `LiveWinConditionTests` green in isolation repeatedly (10/10; final isolated
  rerun after all fixes: 10/10).
- Must-fail-if-removed proven: temporarily disabling the
  `CheckForVictoryByInactiveSides(mission)` call in `ReleasePeer` makes the four
  positive elimination scenario tests fail (`Second_player_disconnect`,
  `Last_player_disconnect`, `No_second_game_over`, `Multi_mission_disconnect`);
  the negative `One_of_three_disconnect` holds under both (it asserts *no* game
  over, which is also the disabled behaviour) — the discriminating positives
  genuinely fail without the wiring. Restored → all pass.
- **Full suite** (3 consecutive runs, 2831 tests each):
  - run 1 = **clean 2831/2831**;
  - run 2 = 2 failures — `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
    and `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
    **both** from the five documented pre-existing flakes; both confirmed green
    in isolation (2/2);
  - run 3 = **clean 2831/2831**.
  Zero novel failure modes. (During the earlier unfixed-server probe, runs showed
  the 6 deterministic NPC-AI failures — the blast radius of the now-correct
  zero-player draw — plus the documented flakes; those 6 were the harness
  collisions fixed above and are now green.)

## What I actually ran

Root build; new `LiveWinConditionTests` isolated repeatedly; must-fail-if-removed
revert/restore probe; the 6 pre-existing NPC-AI tests isolated; the 2 run-2
flakes isolated (both green); 3 consecutive full-suite runs (2 clean, 1 with the
2 documented flakes each confirmed green in isolation); fmid count; git diff review.
