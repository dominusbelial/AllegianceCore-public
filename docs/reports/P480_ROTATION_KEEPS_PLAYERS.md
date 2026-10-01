# P480 — mission rotation keeps connected players seated (native Vacate semantics)

Date: 2026-10-01 · Landed on `master` (not pushed; Domingo pushes) · Branch `master`

## Summary

Native keeps every player **in the same mission** across a game-over: after
sending `GAME_OVER`, `CFSMission::GameOver` calls `Vacate()` then
`SetStage(STAGE_NOTSTARTED)` and resets each side's ready/money, so the players
stay connected, stay on their sides, and their Back button returns them to the
team lobby for the next game. The port's rotation (P439) swapped a freshly
assembled mission object into the same `_missions` index, which **orphaned** the
old mission's still-connected peers: their ships froze on the discarded mission
and they had to reconnect to play again (P439's report named this task as the
follow-up; P455b hit the same missing piece).

P480 **migrates** the finished mission's connected peers into the rotated
mission at rotation time, exactly as native `Vacate` + `Reset(true)` would leave
them, and re-sends each the join-burst a fresh joiner of a `NotStarted` mission
receives. After a game-over, every still-connected Bot stays seated on its
original side in the new world, gets `MISSION_STAGE NotStarted`, receives the new
world's EXPORTs, and can ready up so the next game starts — **without
reconnecting**.

## Native `Vacate` / `Reset` mapping (what we reproduce)

Read from `FedSrv/fsmission.cpp` and `Igc/igc.h` at `~/Development/Allegiance/src/`:

- **`Vacate()`** (`fsmission.cpp:2604-2622`): deletes every **drone**
  (`delete pfsShip->GetDrone()`) and calls **`pfsShip->Reset(true)`** on every
  **player**. Drones are *not* carried over — the port's migration likewise
  mints a fresh ship per player and does not carry old-mission drones.
- **`CFSShip::Reset(true)`** (`fsship.cpp:162-166`): `GetIGCShip()->Reset(bFull)`
  then `ShipStatusExit()`. The player **stays connected**, stays **on the same
  side**, and their ship is reset to a clean docked/lobby state. The port's
  equivalent is a fresh ship (via `SpawnDefaultShip`), fresh
  `PlayerScore`/`PlayerWallet` (wallet reset to 0), re-seated on the **same
  side id** via `TryJoinSide`.
- **Post-game-over side reset** (`fsmission.cpp:3493-3521`): `rgfReady[side] =
  false`, `rgfForceReady[side] = false`, `rgfActive[side] = true`,
  `m_rgMoney[side] = 0`, `SetActiveF(true)`, then `CheckForSideAllReady`. The
  port clears each migrated peer's `_playerReady` entry (so it must ready again
  before the next game) and its fresh `PlayerWallet` starts at 0.
- **Starting money composes with P443** (`missionigc.cpp:2752`; P443 report):
  native seeds a side's money at the real start (`fsmission.cpp:1992-2001`). The
  port's `ApplyStartingMoneyAndInitialPaydown` runs on the real `Started`
  transition (`CompleteStartCountdown`, P443), so a migrated peer's reset-to-0
  wallet is topped up with the P443 seed at the next game start — no extra
  handling needed. Verified: P443 §7 already confirmed a rotated fresh mission
  is seeded at its next real start.
- **Leadership / ownership re-established**: native re-runs the side-join
  mechanics on the same sides. The port's migration re-seats via `TryJoinSide`
  in an order that preserves the OLD leader (first joiner of a side becomes its
  leader, `fsmission.cpp:601-613`) and the OLD mission owner (owner-side peers
  re-seat first, reclaiming ownership).

## Side mapping is verified, not assumed

The new mission is assembled from the **same `GameConfig`**, so sides map 1:1.
Rather than assume it, `MigrateConnectedPeersIntoRotatedMission` verifies every
old side id exists in the fresh mission; a side that is missing is logged and
its peers are left unseated (never guessed). (Assembly order guarantees this in
practice; the check guards the invariant.)

## Implementation

All in `src/Allegiance.Server/` (`Server` is a set of partial classes):

### 1. `RotateMissionAfterGameOver` now migrates (`Server.Lobby.cs`)

After the rotation assembles the fresh mission, runs `SpawnInitialMiners(fresh)`
and the P387 debug auto-start, it calls
`MigrateConnectedPeersIntoRotatedMission(mission, fresh)`. The P439 "keep GAME_OVER
screen, must reconnect" orphan stance is removed; the doc comment is updated to
state peers are now migrated. The ship-id allocator safety P439 established
(`IsShipIdLive` checks `_peerShips` before `_missions`) is **retained** — an
unmigrated/other-peer's id is still protected until its `_peerShips` entry is
replaced.

### 2. `MigrateConnectedPeersIntoRotatedMission` — three phases

- **Phase 1 — mint + register + clear.** For each old-mission peer: allocate a
  fresh ship id; create a fresh ship in the new mission via `AssignMigratedShip`
  (which **forces `SpawnDefaultShip`**, deliberately ignoring any injected
  `ShipSpawner` — see below); clear the per-peer latches keyed to the old
  mission (`ClearPeerJoinLatches`); clear the peer's `_playerReady`; clear its
  pending side-join requests; and register the fresh ship in `_peerShips` so
  later phases' whole-mission broadcasts reach it.
- **Phase 2 — re-seat on the same side, preserving leadership.** Each peer is
  `TryJoinSide`'d onto its **original** side id, in an order that re-establishes
  the old leader and the old mission owner (owner-side leader first, then that
  side's teammates, then other sides leader-first).
- **Phase 3 — re-send the fresh-joiner join burst.** Each migrated peer is
  re-driven through the **existing** join-burst path `CompleteJoinAndLaunch`
  (the exact burst a fresh joiner of a `NotStarted` mission gets):
  `MISSIONDEF` + `JOINED_MISSION` (static-core load), `MISSIONDEF` sides,
  own-ship `EXPORT`, `PLAYERINFO`, `SET_MONEY`/`CREATE_BUCKETS`/`BUCKET_STATUS`,
  `SET_BRIEFING_TEXT`, `RANK_INFO`, `TREASURE_SETS`, `CLUSTERINFO`, the new
  world's cluster/warp/station/asteroid `EXPORT`s, `SET_CLUSTER`,
  `BUY_LOADOUT_ACK`, **`MISSION_STAGE NotStarted`** (the real stage, P309),
  `SHIP_STATUS`, `GAIN_FLAG` join-sync, and `ENTER_GAME` (suppressed while
  NotStarted — P180). No parallel burst is hand-built.

### 3. `AssignMigratedShip` — fresh docked/lobby ship without the placement hook

Mirrors `AssignShip` but **forces `SpawnDefaultShip`** and does not consult the
injected `ShipSpawner`. The spawner is a join-time placement policy for **new**
joiners (and, in tests/Explorer, drives test-specific placement / station
stripping); a migrated peer is an **existing** player being re-seated after a
game-over, so re-running a test spawner on its behalf would repoint harness
state and could even index out of bounds. It returns a `LiveShip` with a fresh
`PlayerScore` and a fresh `PlayerWallet` (money reset to 0, composed with P443
at the next start), parked in the side's home sector.

### 4. `ClearPeerJoinLatches` — the latch audit (task scope point 2)

Clears every per-peer one-shot/time-windowed latch keyed to the old mission so
the new world's identity messages actually get re-sent (mirrors
`ReleasePeer`'s per-peer cleanup):

- `_teammateExportSent` — same-side ship `EXPORT(26)` latch
- `_teammatePlayerInfoSent` — same-side `PLAYERINFO(10)` latch
- `_crossSideExportSent` — cross-side ship `EXPORT` latch (P349)
- `_crossSideMineExportSent` — cross-side mine `EXPORT` latch (P453; recipient dim)
- `_sameSideNpcExportSent` + `_lastSameSideNpcStatusTick` — same-side NPC identity/status (P366)
- `_teammateLoadoutVersionSent` + `_teammateLoadoutCache` — teammate loadout sync
- `_lastTeammateStatusTick` — teammate `SHIP_STATUS` cadence
- `_lastStatusSector` / `_lastStatusTick` / `_lastClusterInfoTick` — per-peer
  `SHIP_STATUS`/`CLUSTERINFO` cadence stamps (re-stamped by the re-join burst)

These are keyed `(recipient connection, subject ship id)`; clearing both
dimensions guarantees no stale key can suppress a new-world identity send. The
fresh ships also carry fresh ids the latches never saw, so this is
belt-and-suspenders mandated by the task.

### 5. `_peerRequests` — retain the LogonRequest for the re-join burst

`CompleteJoinAndLaunch` needs the peer's `LogonRequest` (CharacterName,
DwCookie). A `Dictionary<ConnectionId, LogonRequest> _peerRequests` is populated
at first seat in `CompleteJoinAndLaunch` and freed in `ReleasePeer`, so the
migration can re-drive the exact burst.

### 6. `ReleasePeer` ordering fix (needed for migration to compose with P183/P178)

Original order ran `CheckForVictoryByInactiveSides` **before**
`HandleLeaderSuccession` and `BroadcastQuitMission`. With migration, the victory
check's rotation re-seats the surviving peers into the fresh mission *first*, so
the succession and QUIT_MISSION broadcasts then reached **zero** peers (the
survivors were no longer on the old mission). Native does removal (succession +
QUIT_MISSION) before the win check (`RemovePlayerFromSide` at
`fsmission.cpp:848-1099`, `QUIT_MISSION` during `RemovePlayerFromMission`, the
win-by-inactive-sides check later in the sim loop), so `ReleasePeer` was
reordered: `HandleLeaderSuccession` → pending-join cleanup → `BroadcastQuitMission`
→ `CheckForVictoryByInactiveSides`. This is more faithful to native and makes
the succession/QUIT announcements reach the original-mission peers before any
rotation+migration moves them.

### 7. Dead orphaned-peer handling removed (task scope point 3)

P439 added no separate functional orphan handler — its "orphan" handling was the
documented stance that connected peers keep GAME_OVER and stay orphaned. That
stance is now dead: P480 migrates them. The stance/documentation is removed; the
**ship-id allocator safety** P439 established (`IsShipIdLive` checking
`_peerShips` before `_missions`) is retained. The defensive `idx < 0` guard in
`RotateMissionAfterGameOver` stands.

## Pre-fix failing behavior (recorded as required)

The new live test was written and run against the unfixed code (the migration
call commented out): the first assertion fails —

```
Collection: [MissionStageMessage { Stage = 3 }]   // Stage 3 == Over
Assert.Contains() Failure: Filter not matched in collection (wanted NotStarted)
```

i.e. the orphaned bot received only the `MISSION_STAGE Over` broadcast and never
the fresh-joiner `NotStarted` re-burst — it stayed frozen in the old mission,
exactly the "today the Bots stay orphaned" behavior the brief describes.

## Test changes

- **New** `tests/Allegiance.Core.Tests/P480RotationKeepsPlayersLiveTests.cs` —
  `Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect`: 2
  sides × 2 Bots, a real `#draw` → OfferDraw → AcceptDraw → game-over chain (all
  four voting yes), then asserts every Bot is still connected, on its original
  side id in the NEW mission, re-received `MISSION_STAGE NotStarted` and the new
  world's EXPORTs, then all four ready up and the new game reaches Started
  **without reconnecting**.
- **Updated** `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`:
  the old peers are now migrated (not orphaned), so the test readies the two
  migrated peers **alongside** the new joiner; its fresh-joiner path is unchanged.
- **Updated** `LiveTeamLeadershipTests.Non_leader_departure_broadcasts_only_quit_mission`
  and `Leader_last_with_no_other_qualified_side_leaves_owner_null_and_no_broadcast`:
  succession/owner assertions retargeted to the ORIGINAL mission object; the
  survivor's leadership broadcasts are acknowledged as its own P480 re-seat (new
  ship id), not a succession.
- **Updated** `LiveLogoffTests.An_observing_peer_witnesses_loggedoff_ship_delete`:
  the observer is migrated when the leaver's departure ends the game, so the
  "observer still commanding" assertion uses the observer's fresh re-seated ship.

## Verification

- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: **0 warnings,
  0 errors** (full solution build confirmed).
- `P480RotationKeepsPlayersLiveTests` — green in isolation 5/5.
- Rotation-sensitive classes green in isolation (P480, MissionRotation,
  SideElimination/P455/P455b, LiveSideInactive, BallotVoting, ConquestTerritory,
  GameOverPlayers, LiveWinCondition, ProsperityArtifact, LiveTeamLeadership,
  LiveLogoff, LiveP388, and the disconnect-elimination paths).
- Full `Allegiance.Core.Tests` suite, **three consecutive runs**:
  - Run 1: `3659 passed / 3 failed / 3662` — the 3 were the P480 test's own
    async-burst load-flake (fixed by hardening the per-bot pump), plus the
    documented load-flakes `LiveRipcordTests.Re_target_activates_to_requester_only`
    and `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`.
  - Run 2: `3661 passed / 1 failed / 3662` — sole failure
    `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`, a
    **documented load-flake** (docs/TEST_FLAKES.md:52 "load-flake (P472)"),
    green in isolation.
  - Run 3: `3657 passed / 5 failed / 3662` — five load-flakes, all green in
    isolation and none touching the game-over/rotation/migration path:
    `Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_not_dispatched_others_keep_ticking`
    (docs/TEST_FLAKES.md:55, "load-flake (P472)"),
    `LiveRipcordTests.{Cancel_while_active_aborts_sector_wide, Re_target_activates_to_requester_only}`
    (LiveRipcord named a load-flake family at TEST_FLAKES.md:113; the P439/P443
    reports list the Cancel/Re_target variants),
    `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`
    (documented in the P443 report's flake ledger), and
    `AddPartTests.Part_pickup_destroys_then_accept_merges_amount_into_existing_mount`
    (a cargo/part-pickup test with zero references to rotation or migration).
- The failing set ROTATES across the three runs (3 → 1 → 5 distinct tests) — the
  signature of ephemeral-port/socket contention under full-suite load
  (docs/TEST_FLAKES.md; the skill's "rapid back-to-back runs amplify flakiness"
  note). Every one passes in isolation, and the P480 test itself is green in all
  three runs after hardening (plus 5/5 isolated).

## Out of scope (unchanged)

- Client UI: the game-over screen's Back button and team-lobby screen (**P481**).
- The side-only game-over for an eliminated side (P455b) — the mission isn't
  rotating there, so no migration.
- Enabling goal-based win modes by config.
