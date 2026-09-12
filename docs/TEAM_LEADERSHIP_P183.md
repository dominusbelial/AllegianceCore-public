# TEAM_LEADERSHIP (P183) — team/mission leadership + real side join/quit

Branch `master`, on top of P182. Ports native's team-leader / mission-owner
mechanism and its real join/quit broadcasts into the in-mission
`Mission`/`Server`/`FM_S/CS` protocol: per-side `LeaderShipId`, per-mission
`MissionOwnerSideId`, a directly-callable `Server.TryJoinSide` side-switch
capability (the `AddPlayerToSide` port), leader succession on disconnect (the
`RemovePlayerFromSide` port), and the previously-completely-missing
`QUIT_MISSION(124)` broadcast on every disconnect. This is Stage 2 of the
team-lobby cluster — the foundation `POSITIONREQ`/`FORCE_TEAM_READY` (P184)
lean on — so no client-initiated (`C`-direction) uses of the four leadership
messages are built, exactly as scoped.

## Background / relationship to native (verified against the live source)

- **`Inc/Messages.h:767-829`** — the five structs (all re-confirmed this pass):
  `SET_TEAM_LEADER`(121)/`SET_MISSION_OWNER`(123) = `{SideID, ShipID}`;
  `JOIN_SIDE`(125, `S`-only) = `{SideID, ShipID}`; `QUIT_MISSION`(124)/
  `QUIT_SIDE`(126) = `{FM_VAR_ITEM(szMessageParam), ShipID, QuitSideReason}` —
  i.e. a variable-length trailing string plus a `QuitSideReason` C++ int enum,
  **not** the `{shipID, sideID, fAccepted}` shape an earlier draft guessed.
- **`FedSrv/fsmission.cpp:539-661`** `AddPlayerToSide` — the first player onto
  an empty side becomes its team leader (`!HasPlayers(pside,true)`); a new
  mission owner arises iff no side owns it (`iSideMissionOwner < 0`) or the
  owning side has no players, and only ever alongside a new leader
  (`assert(IMPLIES(fMissionOwner, fTeamLeader))`). Broadcasts `JOIN_SIDE`
  always; `SET_TEAM_LEADER`/`SET_MISSION_OWNER` when applicable.
- **`fsmission.cpp:848-1099`** `RemovePlayerFromSide` — leader succession when
  the leader leaves: teammates remain → promote a successor (broadcasting
  `SET_TEAM_LEADER`, and `SET_MISSION_OWNER` if this side owned the mission);
  side empties → clear leader, and if it owned the mission migrate the owner
  role to another occupied side's existing leader (broadcasting both), or clear
  it (native then unlocks lobby/sides — not ported, deliberate no-op).
- **`fsmission.cpp:460-525`** `RemovePlayerFromMission` — the disconnect
  superset: `RemovePlayerFromSide` first, then `QUIT_MISSION`. In this port the
  disconnect path is `Server.ReleasePeer`, which **previously sent no fedmsg at
  all** telling remaining peers the ship left — the real gap `QUIT_MISSION`
  closes.
- **`QuitSideReason`** (`Messages.h:787-808`, ~18 values) — only the two this
  port's logic can reach are modeled (`QSR_LinkDead=9`, `QSR_SwitchingSides=13`);
  the rest (boot/kick/rank/squad limits) have no triggering logic here yet.

## What changed

- **`src/Allegiance.Core/Model/Side.cs`** — added `public short? LeaderShipId`
  (nullable, starts `null`), following the exact direct-field-on-Side doc-comment
  convention `Ready`/`Money` establish.
- **`src/Allegiance.Core/Model/Mission.cs`** — added `public short?
  MissionOwnerSideId` (nullable, starts `null`) beside `Stage` — per-mission,
  not per-side, matching native `m_misdef.iSideMissionOwner`.
- **Wire codecs** (new, all byte-pinned in tests):
  - `SetTeamLeader`(121), `SetMissionOwner`(123), `JoinSide`(125) — `{SideID
    short@4, ShipID short@6}` = 8 bytes fixed (modeled on the sibling
    `TeamReady` codec).
  - `QuitMission`(124), `QuitSide`(126) — identical variable-length shape
    (modeled on the chat-family codecs, using the shared `ZtsBlob`/`VarItem`
    helpers): header(4) + VarItem ib/cb@4-7 + `ShipID` short@8 + 2 pad@10-11 +
    `QuitSideReason` int@12 = **16 bytes fixed**, then the CB_ZTS message blob.
    `QuitSideReason` is a `Wire` enum holding only `LinkDead`/`SwitchingSides`;
    the raw int round-trips any value if ever received.
- **`src/Allegiance.Server/Server.cs`**
  - New `internal bool TryJoinSide(Ship ship, short newSideId)` — the
    `AddPlayerToSide` port as a directly-callable side-switch capability: quits
    the old side first (running leader succession + broadcasting `QUIT_SIDE`
    `QSR_SwitchingSides`), then seats the ship on the target side, computing the
    new leader (first onto empty side) and mission owner (iff none, or the
    owner side is empty, always alongside a new leader), updating
    `Side.LeaderShipId`/`Mission.MissionOwnerSideId`, and broadcasting
    `JOIN_SIDE` always plus the leadership messages when applicable — all
    whole-mission. Returns `false` (no change) if the ship has no mission or the
    target side doesn't exist in it. **Not wired to any live client message**
    (P184's position-request flow triggers it later) — capability-first, per the
    session's established pattern.
  - New `HandleLeaderSuccession(Ship)` — the `RemovePlayerFromSide` leader
    branch: teammate(s) remain → promote the deterministic **first other player
    on the side (lowest ship id)** and broadcast; side empties → clear the
    leader and migrate/clear the mission-owner role. Invoked on the disconnect
    path and on the quit-half of `TryJoinSide`.
  - New whole-mission broadcast helpers `BroadcastJoinSide` /
    `BroadcastSetTeamLeader` / `BroadcastSetMissionOwner` / `BroadcastQuitMission`
    / `BroadcastQuitSide`, all routing through a single mission-scoped
    `SendToMissionPeers` sweep (the P180/P178 fixed multi-mission bug class —
    never an unscoped `_peerShips` sweep) — matching native's
    `GetGroupMission()` audience.
  - New `CountPlayersOnSideExcluding` — per-side live count computed on demand
    by filtering `_peerShips` (the P180 `RecomputeSideReady` style), no running
    per-side counter added.
  - **`ReleasePeer`** — after `RetireShipOnDisconnect` and the P178 victory
    check, before the `_peerNames`/`_playerReady` cleanup: run
    `HandleLeaderSuccession(live.Ship)` (fires only if the departed ship was its
    side's leader) and broadcast `BroadcastQuitMission(mission, shipId,
    QSR_LinkDead)` to the whole mission — the real, previously-missing signal.
- **`src/Allegiance.Bot/Bot.cs`** — captured `JOIN_SIDE`/`SET_TEAM_LEADER`/
  `SET_MISSION_OWNER`/`QUIT_MISSION`/`QUIT_SIDE` receipts
  (`JoinSides`/`SetTeamLeaders`/`SetMissionOwners`/`QuitMissions`/`QuitSides` +
  `ResetLeadershipSignals` + decode cases) so loopback tests can observe the
  new broadcasts.

## Stated simplifications (per scope, never silent)

- **Leader succession** always uses "first other player on the side" (ordered
  by ship id for determinism), dropping native's donation-based
  (`PickNewLeader`, `STAGE_STARTED`) and squad-capability (`GetCanLeadSquad`)
  tie-breaks — this port has neither donations (P119) nor squads.
- **Initial spawn does not establish leadership.** `SpawnDefaultShip` seats a
  peer on a side without running the `AddPlayerToSide` mechanics, so a
  spawn-seated player has no leader until a join/switching event goes through
  `TryJoinSide`. This is the task's documented "capability on join/switch, not
  at spawn" boundary; native always funnels joins through `AddPlayerToSide`.
- **Disconnect broadcasts `QUIT_MISSION` only**, not `QUIT_SIDE`. Native's
  `RemovePlayerFromMission` calls `RemovePlayerFromSide` (→ `QUIT_SIDE`) then
  broadcasts `QUIT_MISSION`; this task deliberately scopes the disconnect path
  to `QUIT_MISSION` (the gone-entirely signal) and reserves `QUIT_SIDE` for the
  stay-in-mission side-switch, per the tests' "only QuitMission" expectation.
- **Lobby/side-lock side effect** native performs when the owner vanishes
  (`LOCK_LOBBY`/`LOCK_SIDES`, fsmission.cpp:1031-1062) is a deliberate no-op —
  neither message is ported (separate, unscoped item).
- **`QuitSideReason`** models only the two reachable values (see above).

## Divergences from the prompt's assumptions (verified against live source)

- **`QUIT_SIDE`'s exact struct confirmed** at `Messages.h:825-829`: it is the
  `{FM_VAR_ITEM(szMessageParam), ShipID, QuitSideReason}` shape (identical to
  `QUIT_MISSION`), matching the prompt's corrected draft — NOT the `{shipID,
  sideID, fAccepted}` shape an earlier draft guessed. Verified, coded to, and
  byte-pinned in `Quit_codecs_round_trip_exact_bytes_with_message_blob`.
- **Blast radius is zero.** Unlike P180 (145 tests / 66 files) and P178/P181,
  the new `QUIT_MISSION`/leader-succession broadcasts on disconnect broke **no
  pre-existing test** — 2872/2872 in the clean runs, with only the documented
  live-network flakes flapping intermittently. No existing assertion assumed the
  old silent-disconnect behavior.

## New tests (tests/Allegiance.Core.Tests/LiveTeamLeadershipTests.cs, 11)

1. `Leadership_pair_codecs_round_trip_exact_bytes` — the three `{sideID,shipID}`
   codecs byte-pinned.
2. `Quit_codecs_round_trip_exact_bytes_with_message_blob` — the two
   variable-length quit codecs, incl. a non-empty trailing message.
3. `First_on_empty_side_becomes_leader_and_mission_owner` — joining an empty
   side makes the joiner that side's leader and (no owner yet) the mission
   owner; emits `QUIT_SIDE(SwitchingSides)` + `JOIN_SIDE` + both leadership
   broadcasts.
4. `Second_on_occupied_side_does_not_become_leader` — the existing leader stays.
5. `Multi_mission_leadership_scopes_per_mission` — a mission A leadership
   broadcast never reaches a mission B peer sharing the same numeric side id
   (must-not-regress P180/P178 bug class).
6. `Leader_disconnect_promotes_first_other_player_exactly` — asserts the exact
   successor (lowest remaining ship id), not just "someone".
7. `Leader_last_on_side_migrates_mission_owner_to_other_occupied_side` — owner
   role migrates to the other occupied side's leader (broadcasting
   `SET_MISSION_OWNER`).
8. `Leader_last_with_no_other_qualified_side_leaves_owner_null_and_no_broadcast`
   — no other occupied side has a leader → both roles clear, surviving peer hears
   only `QUIT_MISSION`.
9. `Sole_leader_owner_disconnect_clears_both_roles` — state-only: both roles null
   when the last player on the only occupied side leaves.
10. `Any_disconnect_broadcasts_quit_mission_to_remaining_peers` — **the
    must-fail-if-removed proof** for the new `QUIT_MISSION` broadcast.
11. `Non_leader_departure_broadcasts_only_quit_mission` — a non-leader departure
    triggers no succession broadcasts at all.

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Tests,
  0 W, 0 E).
- New `LiveTeamLeadershipTests` green in isolation (11/11).
- **Must-fail-if-removed proven**: temporarily disabling the two new
  `ReleasePeer` lines (`HandleLeaderSuccession` + `BroadcastQuitMission`) makes
  **6** of the disconnect tests fail (succession, owner migration, owner
  clearing, non-leader-only-QuitMission, and both QUIT_MISSION proofs) — the
  new disconnect behavior is genuinely discriminating, not an always-pass.
  Restored → green.
- **Full suite, 4 consecutive runs (2872 tests each; baseline 2861 + 11 new):**
  - run 1 = **2871/2872**, 1 failure =
    `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
    (a documented flake; **green in isolation**, and its file has zero references
    to any symbol this task changed → environmental, not mine);
  - run 2 = **2871/2872** (single intermittent live-network flake, same class);
  - run 3 = **2872/2872 clean**;
  - run 4 = **2872/2872 clean**.
  No pre-existing test needed adjustment for the new broadcasts.

## What I actually ran

Verified native sources (`Messages.h`, `fsmission.cpp`) first; implemented model
fields + 5 codecs + enum; built Core/Server; added Bot captures; built;
implemented `Server.TryJoinSide`/`HandleLeaderSuccession`/broadcasts and wired
`ReleasePeer`; built; wrote 11 tests; iterated on a codec-helper bug and a
broadcast-timing race in the tests; 3 full-suite runs + isolated rerun of the
documented flake; must-fail-if-removed probe (6 red when wiring removed,
restored); 2 further clean full runs; git diff review.
