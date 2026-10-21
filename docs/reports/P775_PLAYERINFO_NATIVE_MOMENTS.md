# P775 — PLAYERINFO only at the native moments: mission join and drone creation, never on first sighting

Branch `pi/p775`, base master `57b930bd` (P774b); landed as one commit on `fix/p775` (Pi's WIP + the Claude review fixups, see the last section).

## The native rule

Native sends `FM_S_PLAYERINFO` from exactly **three** call sites of `SendPlayerInfo`
(`FedSrv/FedSrv.CPP:2056-2135`):

1. **A player joins the mission → the whole mission, joiner included** (`fsmission.cpp:411`, in
   `CFSMission::AddPlayerToMission`). The joiner is put on `SIDE_TEAMLOBBY` first (`:408`), so the
   client's PLAYERINFO handler announces nothing (`appmsg.cpp:2286-2290` skips the lobby side); the
   `JOIN_SIDE` that follows (`fsmission.cpp:621-624` → `appmsg.cpp:2909-2915`) is the one
   announcement in practice.
2. **The joiner gets one PLAYERINFO per ship already in the mission** (`SendLobbyMissionInfo`,
   `fsmission.cpp:3936-3974`; the loop `:3958-3968` is over `pMission->GetShips()` — every ship,
   humans **and drones**, every sector, no visibility test).
3. **A drone is created → the whole mission** (`CreateStockDrone`, `FedSrv.CPP:2269`; training
   creates the drone's player info when it creates the drone, `trainingmission.cpp:620-623`).

Audience is `GetGroupMission()` (`FedSrv.CPP:2129-2131`): every connection in the mission,
regardless of sector or visibility. No resend on sighting, rank change, re-export, relaunch or side
switch; game over keeps the mission (`fsmission.cpp:3483-3490`), so nothing is re-sent.

## Step 0 — findings

**Send-site grep** (`git grep "BuildPlayerInfo\|SeedMissionPlayerInfo\|_missionPlayerInfoSent\|_teammatePlayerInfoSent"`)
at `57b930bd` matched the brief's list exactly — send sites A–G and the two latches, no extras.

**Client keeps PlayerInfo for the whole mission** (so no per-mission player table is needed):

- `ClientWorld.Apply(PlayerInfo)` (`State/ClientWorld.cs:3749`) upserts the `ShipEntity` and keeps
  `e.Player = info` on it.
- `_ships.Remove` happens only on `QUIT_MISSION` (`ClientWorld.Interface.cs:181`); `_ships.Clear`
  only in `Reset()` (`ClientWorld.cs:1900`), `BeginNextMission()` (`:4776`) and the local observer
  (`ClientWorld.LocalObserver.cs:12`).
- Leaving view only sets `IsGone` / clears `PoseSectorId` (e.g. `ClientWorld.cs:4615-4616`); it
  never drops `Player`. Confirmed — no client table added.

**Render/target audit** — every client will hold PLAYERINFO-only `ShipEntity`s (hull 0, SectorId 0,
no pose) for every ship. All iterating readers checked:

- Draw gate `ShipEntity.CanDrawInSector` (`State/ShipEntity.cs:73-76`) requires `PoseSectorId ==
  sectorId && Latest is not null` — safe. Render pose gather (`Game1.FlightInput.cs:2542`),
  benchmark gathers and autopilot dodge all gate on `Latest`/`CanDrawInSector` — safe.
- Target cycling `Game1.GatherSectorCandidates` (`Game1.Targeting.cs:176`) skips ships with
  `Latest is null` and non-matching `SectorId` — safe even when the viewer sits in sector 0 (the
  `Latest` gate fires first).
- Sector map `SectorMapQueries.KnownEnemySectors` (`:140`) requires `HasRealIdentity` (EXPORT-set)
  and `sector != 0` — safe.
- Radar (`Game1.Rendering.cs:3691,3740`) anchors only ships with no `Latest` **and** a resolvable
  pose — a PLAYERINFO-only ship has neither — safe.
- Teleport pane (`Game1.Panes.cs:785` → `TeleportPaneModel.Build`) requires `StationId != NA` and a
  hull lookup (hull 0 fails) — safe. Drone counts (`Game1.Panes.cs:863`) require `HullId > 0` —
  safe. Investment-funding pilots and ping/roster lists are `IsHuman`/`PilotTypePlayer`-gated
  (humans only — the same population native's own client lists). SpectatorFrame already marks
  `hasPose:false` (`State/SpectatorFrame.cs:73-96`); the MCP BotManager's `ToDroneRow` prints
  sector/pos only when `Latest` is set, and its target scans gate on `Latest`.
- `DecisionSnapshot.FillShipSnapshot` carries a `HasLatest` flag bit (bit 1) — consumers check it.

**Audit result (Pi): no leak found.** **Corrected by the Claude review:** three readers DO leak a
PLAYERINFO-only entity's C# defaults (`SectorId = 0`, `StationId = 0`; 0 is a real cluster id):
the team pane (unseen enemies read DOCKED / cluster 0's name), the transit-swell edge (a first
status reads as a transit out of cluster 0) and allegiance-mcp's bot LOD (an unseen human marks
cluster 0 human-occupied). All three are fixed and pinned — see "Claude review fixups". Test 7
(draw/target) still passes on both sides.

## Send sites: before and after

Before (`57b930bd`), all lazy — every one deleted except F:

| # | before | when | after |
|---|---|---|---|
| A | `Server.Broadcast.cs` serial P63 (`_teammatePlayerInfoSent`) | first tick after the pair's teammate EXPORT | **deleted** (joiner announced at the join moment instead) |
| B | parallel P63 twin (`BroadcastPeerBuild.PlayerInfoSent`) | same | **deleted** |
| C | serial `SeedMissionPlayerInfo` after the same-side NPC EXPORT | first same-side NPC EXPORT | **deleted** (drones announced at creation) |
| D | parallel mirror of C (`MissionPlayerInfoSent`) | same | **deleted** |
| E | `SeedMissionPlayerInfo` in `SweepCrossSideExports` | first cross-side EXPORT (sighting) | **deleted** — no PLAYERINFO on sighting |
| F | own-ship PLAYERINFO in `CompleteJoinAndLaunch` (`Server.Lobby.cs`) | join burst | **kept** (the own-ship side carrier, `Server.Lobby.cs` P31/T17 comment) |
| G | join burst roster loop (`Server.Lobby.cs` P713) | `CompleteJoinAndLaunch` | **kept, extended** to every mission ship — humans and every `Ship { IsNpc: true }` in `lobbyMission.Sectors[*].Models` (the `IsShipIdLive` enumeration, `Server.Docking.cs:1818-1822`; docked ships stay in `Models` — nothing ever removes them) — still one packet, latch-gated |

After, the three native moments:

1. **Joiner → mission**: `AnnounceFreshShip(live.Ship, -2 /* SIDE_TEAMLOBBY */, pend.Request.CharacterName)`
   immediately before each seat-TryJoinSide in the side-select path (`Server.Lobby.cs`, both the
   explicit and the P548 auto-assign branch) and before the rotation reseat `TryJoinSide` in
   `DrainPendingRotation` phase 2. A **catch-all** `AnnouncePlayerToMission(live.Ship,
   live.Ship.SideId ?? -2, req.CharacterName)` runs in `CompleteJoinAndLaunch` right after
   `_peerShips`/`_peerNames` are set — for a seated joiner it sends nothing (every pair already
   latched by the side-select announce); it covers a joiner that is never seated. Not called inside
   `TryJoinSide` — a side switch sends no PLAYERINFO.
2. **Mission → joiner**: G, extended as above — and (Claude review) moved AHEAD of F, in the same
   packet: native sends these in `SendLobbyMissionInfo` (`fsmission.cpp:406`) while the joiner's
   side is still unset, before the joiner's own PLAYERINFO (`:408-411`), so a native joiner
   announces none of the humans already present. The rest of the P713 lobby snapshot (leader /
   ready / wing state) stays where it was.
3. **Drone creation**: `SpawnNpcShip` (`Server.cs`) calls the new `internal AnnounceNewShip(ship)`
   after `PlaceShip`; `TrainingHost.CreateDrone` calls `_server.AnnounceNewShip(ship)` after
   `Mission.PlaceShip` (native `trainingmission.cpp:620-623`). With no peers (boot, rotation miner
   spawn) it sends nothing; later joiners get drones through G. The training `player`/`commander`
   ships (`TrainingHost.cs:192-206`) are untouched — they are `IsNpc=false` player-side ships, not
   drones (noted per the brief).

**One latch**: `_teammatePlayerInfoSent` + `_missionPlayerInfoSent` replaced by a single
`HashSet<(ConnectionId Recipient, short ShipId)> _playerInfoSent` (`Server.cs`, comment cites the
three native moments). Stale-id guard (Claude review — replaces Pi's `_playerInfoAnnouncedShips`
set, which missed a reused id whose id had been "announced" before and never released): the
genuinely-new-ship sites call `AnnounceFreshShip`, which drops the id's stale entries
(`_playerInfoSent.RemoveWhere(e => e.ShipId == id)`) and then announces — `SpawnNpcShip` and
`TrainingHost.CreateDrone` (via `AnnounceNewShip`), the side-select join and the rotation reseat
(phase 2). The `CompleteJoinAndLaunch` catch-all uses the plain `AnnouncePlayerToMission`, which never
clears latches. Prune sites kept on the new set: `ReleasePeerWithReason` (`Server.Lifecycle.cs`),
`ClearPeerJoinLatches` (`Server.Lobby.cs`), dead NPC (`Server.Combat.cs`). Diagnostic count renamed to
`PlayerInfoSent` in `ConnectionScopedCacheCounts` (`Server.cs`).

Two helpers in `Server.Broadcast.cs`: `AnnouncePlayerToMission` (build once, pack once, send to
every `_peerShips` entry whose `Ship.Mission` matches, skipping the ship's own connection and
latched pairs) and `AppendPlayerInfoForRecipient` (latch-gate + append to G's single packet).
Kills/deaths/ejections/rank come from the owning `LiveShip.PlayerScore` via `MissionScoreOf`
(former `ShipMissionScore`), 0 for drones. **All sends stay on the sim thread**, so the P774 Fix 6
ordering (PLAYERINFO never overtakes its KILL_SHIP) holds by construction. No new fedmsg, no client
change. The P62 comment block was rewritten: the EXPORT-before-PLAYERINFO ordering claim was an
AllegianceInterop concern; the port client upserts either way, and native never EXPORTs `OT_ship`
to other clients — it creates the ship *from* PLAYERINFO (`appmsg.cpp:2264`).

`SeedMissionPlayerInfo`, `ShipMissionScore`, `BroadcastPeerBuild.PlayerInfoSent`/`MissionPlayerInfoSent`
and their apply lines are gone. **Final grep is empty**:

```
git grep "SeedMissionPlayerInfo\|_missionPlayerInfoSent\|_teammatePlayerInfoSent\|MissionPlayerInfoSent" -- src tests tools
→ no matches (exit 1)
```

## Tests

New `tests/Allegiance.Core.Tests/P775PlayerInfoMomentsTests.cs` with the **`NativeJoinAnnouncer`**
(replays one recipient's stream with native's client rules: stage from MISSION_STAGE, own ship/side
from JOINED_MISSION/own PLAYERINFO/own JOIN_SIDE, PlayerInfo table, announcements counted for each
other-human PLAYERINFO with `ISide >= 0` and each other-ship JOIN_SIDE while Started and holding a
real side, violation flagged when a JOIN_SIDE arrives for a ship with no PlayerInfo). Because the
port's game-over rotation replays JOIN_SIDEs (phase 2) to migrants whose fresh join burst (phase 3)
has not landed — a situation native never produces — a JOIN_SIDE with no PlayerInfo that arrives
before the recipient's JOINED_MISSION is held and judged at JOINED_MISSION: only the recipient's OWN
fresh ship is exempt (the announce skips the subject's own connection); every other ship's still
counts (Claude review narrowed Pi's blanket "ignore until joined" guard, and dropped MISSIONDEF as an
epoch reset — the port sends MISSIONDEF twice per join burst, which had disarmed the check for the
rest of every run). The table is cleared at MISSION_STAGE(Over), as the port client clears its
ships. An optional `missionDefStage` replays MISSIONDEF with the live stage, as native fills it
(`Inc/Messages.h:301`; the port always sends 0).

New client-side tests: `tests/Allegiance.ClientCore.Tests/P775ClientPlayerInfoMomentsTests.cs`
(test 6 + the draw/audit half of test 7) and
`tests/Allegiance.ClientCore.MonoGame.Tests/P775PlayerInfoOnlyShipTests.cs` (the target-cycling
half of test 7 against the real `Game1.GatherSectorCandidates`, including a viewer in sector 0;
split into MonoGame.Tests because that is where MonoGame.Framework is loadable — the Game assembly
exposes internals only to it).

| test | on unfixed code | after |
|---|---|---|
| 1 `Mid_game_sighting_of_a_joined_enemy_sends_no_playerinfo` | **FAIL** (PLAYERINFO for B came from E at the sighting, after JOIN_SIDE → ordering assert + announcer violation) | pass |
| 2 `Mid_game_joiner_receives_every_ship_playerinfo_including_enemy_drones_in_other_sectors` | **FAIL** (G had no drones) | pass |
| 3 `Drone_created_mid_game_reaches_every_mission_peer_once_at_creation` | **FAIL** (enemy peer got none at creation) | pass |
| 4 `Side_switch_sends_no_playerinfo` | Pi's version **passed on the unfixed code** (the server balances requested sides, so its "side 1" peer sat on side 0 and `TryJoinSide` was a no-op). Claude's fixture reads the real seats, moves the switcher into the pair's sector and waits for the cross-side EXPORT, then switches: **FAIL** on `57b930bd` (`Assert.Empty() Failure: Collection was not empty` — PLAYERINFO resent after the switch) | pass |
| 4b `Native_joiner_announces_none_of_the_humans_already_present` (Claude) | **FAIL** on `57b930bd` (3 PLAYERINFOs for 2 humans) and on Pi's WIP `1956a68c` (both existing humans' PLAYERINFOs after the joiner's own; with the order check removed the announcer counts 2, expected 0) | pass |
| 4c `Reused_drone_id_after_unpruned_removal_still_reaches_existing_peers` (Claude) | **FAIL** on `57b930bd` and on Pi's WIP (`Expected: 2, Actual: 1` — the reused id's second drone never reached the peers) | pass |
| 5 `Rotation_sends_one_playerinfo_per_pair_before_the_match_starts` | **FAIL** (A/B duplicates) | pass |
| 6 `Joiner_salutation_and_roster_are_populated_on_the_port_client` | **FAIL on the salutation** (JOIN_SIDE preceded PLAYERINFO, so no `SalRecruitsArrived`/`SalEnemyJoiners`); the roster/experience/kill-bonus assertions pass on both | pass |
| 7 `Playerinfo_only_ship_is_not_drawn_or_targetable` (+ MonoGame gather half) | pass on both (audit found no leak) | pass |
| rewritten seed tests (`Hud20PlayerInfoSeedTests`) | old pinning tests deleted; new ones fail on unfixed code (cross-side/shipStates PLAYERINFO existed) | pass |

Claude-review fail-first evidence: `~/allegiance-evidence/P775/review-failfirst-*.txt` (the 7 Core
tests on a detached `57b930bd` worktree with only the test file copied in: 7/7 fail; on Pi's WIP
`1956a68c`: 4b and 4c fail; the client gates 3a–3c by mutation: reverting each gate fails its test).
Pi's pre-fix evidence: `~/allegiance-evidence/P775/p775-before-core-tests.log` (5/5 failed),
`p775-before-client-tests.log` (salutation timeout + the leak-check failing only on MonoGame
loading, fixed by the split).

**Fixture changes** (item 9 + one the fast tier caught):

- `ConnectionCacheCleanupTests` — reads the renamed `PlayerInfoSent` counter; the ">= 1 PLAYERINFO
  latch before release" assertion still holds through the join path. Unchanged semantics.
- `Hud20PlayerInfoSeedTests` — **rewritten** (item 8): the seam-driven "crossSideExports step and
  both BroadcastShipStates modes emit no PLAYERINFO" test (serial + parallel), and the content
  checks (`FExperience`, `DtidDrone`, `ISide`, `PilotType`) moved onto the new join burst (live
  join, enemy human) and creation announce (`SpawnNpcShip`). `Observer_encodes_0xFF_and_a_gunner_its_mount`
  kept; file name kept.
- `ShipStatusServerTests.Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status` —
  this test pinned the removed P63 lazy send (not in the brief's item-9 list; found by the fast-tier
  run). Rewritten as
  `Teammate_ship_playerinfo_is_sent_once_at_the_native_join_moment`: the observer holds the
  subject's PLAYERINFO **before** the subject's JOIN_SIDE and first SHIP_STATUS, exactly once, with
  `ISide = -2` (the pre-seat carrier; the JOIN_SIDE sets the real side), real id/name/money 0.
- `Server.BroadcastTestSeams` seam comment updated (no PLAYERINFO seeding in the sweep).
- `tests/_shared`, `TestSeedPeers` users: no test needed a lazy-seed replacement — every affected
  test uses real joins or `AnnounceNewShip`.
- `tools/hud20-proof/Program.cs` — comment updated; its dummy enemy/friendly/observer ships bypass
  join and `SpawnNpcShip`, so they now go through `ServerCall("AnnounceNewShip", ship)` after
  placement (kill bonuses added before the announce so the seed carries the real experience; the
  tool uses reflection because Server internals are not IVT'd to proof exes). Built on the base and
  after the change: both `Build succeeded`.
- `docs/TEST_FLAKES.md` — two fast-tier load flakes recorded (see below).

## Traffic

PLAYERINFO = 80 B (68 fixed + one 12-B persist entry, `Core/Wire/PlayerInfo.cs:113,238`); a
single-message packet adds the 4-B CRC (`FedPacket.cs:14,37`) → 84 B on the socket.

**Arithmetic for 214 peers on 2×107 sides** (label: arithmetic, not measured):

| rotation PLAYERINFO | before | after |
|---|---|---|
| G (each joiner ← every other human, batched) | 214×213 = 45,582 × 80 B = 3.65 MB | 0 for humans (latched by the phase-2 announce), + 214 × initial miners × 80 B |
| A/B teammate duplicates (NotStarted) | 2×107×106 = 22,684 × 84 B = 1.91 MB | 0 |
| phase-2 joiner announce | — | 45,582 × 84 B = 3.83 MB |
| **burst while NotStarted** | **≈ 5.55 MB** (matches the rig's ~5.5 MB) | **≈ 3.83 MB** + miners |
| E after Started (as enemies meet) | up to 214×107 = 22,898 × 84 B = 1.92 MB, announced | 0 |

Mid-game join, before: the joiner got N−1 humans (G) + its teammates again (A/B) + enemies again on
sighting (E), and existing peers learned the joiner lazily. After: the joiner gets one packet with
every ship; each existing peer gets one 84-B PLAYERINFO. Drones, after: N × 84 B per drone at
creation (before: lazy, at most the same).

**Measured** on the test-5-style rotation fixture, 16 peers on 2 sides, 2 initial miners per…
2 miners total (`miners=2`), `_testSendCapture` from just before `DeclareGameOver` to fresh-Started
+100 ticks (`~/allegiance-evidence/P775/traffic-beforeF.log`, `traffic-afterF.log`):

| measured (N=16, 2 miners) | before | after |
|---|---|---|
| PLAYERINFO messages | **384** (24 per peer: 15 G + 7 A/B + 1 F + 1 fresh same-side miner seed; E=0 — no cross-side co-location in the fixture) | **288** (18 per peer: 15 phase-2 announce + 2 G-miners + 1 F) |
| PLAYERINFO message bytes | 30,720 B | 23,040 B |
| per-message socket cost (84 B) | 32,256 B | 24,192 B |
| P661 egress fmid-10 `SentBytes`, rotation window only (Claude re-measure) | **30,720 B** | **23,040 B** |

**The "before" egress figure (Claude review).** Pi's run reported 51,840 B "before" against 30,720 B
of captured PLAYERINFO. The extra 21,120 B (264 messages) is the **seat phase**: the P661 profiler
window is drained only at a profiler report, and in the "before" run no report fell between the 16
seats and `DeclareGameOver`, so that window also held the 16 initial joins' PLAYERINFOs (G 120 +
A/B 112 + F 16 + same-side miner seeds 16 = 264). The "after" 23,040 B was likewise the seat-phase
window (a report landed before the reseat), which happens to equal the rotation figure. Claude
re-measured with a temporary test that calls `TickProfiler.ForceReport()` right before
`DeclareGameOver` and again after fresh-Started + 100 ticks (not committed;
`~/allegiance-evidence/P775/review-measure-*.txt`): before `57b930bd` seat 264 msgs / 21,120 B
(egress 21,120), rotation 384 / 30,720 B (egress 30,720 — so 21,120 + 30,720 = 51,840 exactly);
after (this commit) seat 288 / 23,040 B, rotation 288 / 23,040 B (egress identical to the capture
in every window). Egress counts message bytes only; packet framing goes to the overhead row.

Formula check at N=16: before = 16·15 + 2·8·7 = 352 (+E=0 measured) plus the fresh-mission miner
seeds and the 16 F carriers the formula table does not itemise; after = 16·15 + 16·miners = 272
(+16 F) — the measured 384/288 match exactly.

## Gate numbers (all measured)

- Whole-solution build: **0 errors, 0 warnings** (`dotnet build AllegianceCore.slnx -c Release`).
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained
  -p:PublishAot=true`): **exit 0, 0 IL2026/IL3050, 0 errors**.
- New/affected classes 3× alone: `P775PlayerInfoMomentsTests` 5/5 ×3; `P775ClientPlayerInfoMomentsTests`
  2/2 ×3; `P775PlayerInfoOnlyShipTests` 1/1 ×3; Core batch (`Hud20PlayerInfoSeedTests`,
  `Hud20ObserverIdTests`, `ParallelBroadcastByteIdentityTests`, `BroadcastSnapshotLoopbackTests`,
  `ConnectionCacheCleanupTests`, `P774PlayerInfoSeedLiveTests`, `P774DerivedScoreRosterTests`,
  `Hud20PlayerInfoWireTests`, `KillBonusWireLiveTests`, `P480RotationKeepsPlayersLiveTests`,
  `P655GameOverDeliveryTests`) 27/27 ×3; ClientCore batch (`ScoreRosterLiveTests`,
  `ClientWorldHud20Tests`, `ClientWorldKillBonusTests`, `ClientWorldSoundEventTests`,
  `P765GameOverStallTests`, `TR2TrainingTests`, `TR3TrainingMissions34Tests`,
  `TR4TrainingMissions56Tests`, `TR2BTrainingHostTests` = class `TrainingHostMission2Tests`) 130/130 ×3;
  `P728CommanderTests` 21/21 ×3. Also rerun after the fast-tier findings: `ShipStatusServerTests`
  6/6, `TeammatePositionBroadcastTests` 2/2, `WorldPopulationLiveTests` 1/1,
  `CrossSideExportLiveTests`/P369 11/11, `RetriedConnectLiveTests` 2/2 ×3.
- Fast tier (`tools/run-tests.sh --tier fast`): first run 2 failures, both load flakes (3/3 alone:
  `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` and
  `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`; recorded in
  `docs/TEST_FLAKES.md`); one real find — `ShipStatusServerTests` pinning the removed P63 send,
  rewritten (above). Final run: **PASS, 9,847 tests, 278.6 s, 0 failed**.
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):
  **5/5 pass** at 1 m48 each; one base run also 1 m48 pass — identical rate.
- `git diff --stat docs/parity` → exactly 4 changed lines across the 3 CSVs; one-off parse confirms
  8/6/16 fields per row on every row of the three files.

## Out of scope / not done

- Zero-resend rotation (native `fsmission.cpp:3483-3490`) — appendix; needs P439/P480/P481/P765 to
  keep ship ids and stop the client's `_ships.Clear()` at Over. The N² announce (≈3.83 MB at 214
  peers) remains.
- Rank/score persistence for reconnecting players; drone naming; the training `player`/`commander`
  ships; `fTeamLeader`/`fMissionOwner`/`fReady` in PLAYERINFO; the P369 identity self-heal; any
  EXPORT/STATUS change — all untouched.
- Honest gaps: (a) the rotation still re-sends PLAYERINFO for every fresh pair (by design, see
  above); (b) `TEST_FLAKES.md` gained two load-flake entries (not code changes).

## Claude review fixups

Applied on top of Pi's WIP (`1956a68c`) before landing; every fix is pinned by a test that fails
without it.

1. **Side-switch test was vacuous** (`Side_switch_sends_no_playerinfo`). It passed on `57b930bd`:
   the server balances requested sides, so the peer asked onto side 1 sat on side 0 and the
   `TryJoinSide(…, side0)` was a no-op. The fixture now reads the actual seats, switches the lone
   ship onto the pair's side, and first moves it into the pair's sector and waits for the cross-side
   EXPORT so every pre-P775 lazy path gets its chance. Proven on a detached `57b930bd` worktree with
   only the test file copied in: **FAIL** (`Assert.Empty() Failure: Collection was not empty`).
2. **Stale-id guard.** `_playerInfoAnnouncedShips` deleted (field, the first-announce check and the
   two prune sites in `Server.Lifecycle.cs`/`Server.Lobby.cs`). It latched an id forever once
   announced, so a drone whose id was reused after an unpruned removal reached nobody. Replaced by
   an explicit `_playerInfoSent.RemoveWhere(e => e.ShipId == id)` in `AnnounceFreshShip`, called
   only at the genuinely-new-ship sites: `SpawnNpcShip`/`TrainingHost.CreateDrone` (through
   `AnnounceNewShip`), the side-select join and rotation phase 2; NOT in the `CompleteJoinAndLaunch`
   catch-all. Test `Reused_drone_id_after_unpruned_removal_still_reaches_existing_peers` fails on
   `57b930bd` and on `1956a68c` (`Expected: 2, Actual: 1`). The scan is O(latch) once per new ship,
   never per tick (the WIP ran the same scan on every first announce).
3. **Client leaks of PLAYERINFO-only entities** (`SectorId`/`StationId` = 0 defaults). Approach:
   gate each reader on the existing `ShipEntity.StatusSectorKnown` (HUD-18's "a status has landed"
   flag) rather than changing the defaults — making the defaults -1 would touch every EXPORT-created
   entity and every reader of `SectorId`/`StationId`, which was not cheap to audit safely.
   - (a) Team pane: `TeamPaneLayout.BriefOf` (the brief construction moved out of `Game1.Rendering.cs`)
     reports sector/station -1 and `StatusKnown = false`; `StatusFor` shows "unknown" (native
     `SectorName(NA)`, `teampane.cpp:1577-1583, 1901-1905`; docked only for a `c_ssDocked` last-seen
     state, `:1828`; status reset at `AddPlayerToSide`, `clintlib.cpp:4139`) instead of DOCKED;
     `SortRow`/`SectorNameOf` never resolve cluster 0 for an unknown status. Test
     `Playerinfo_only_enemy_reads_unknown_in_the_team_pane_not_docked`.
   - (b) `DetectTransitSwells` → `Game1.TryTransit`: no baseline until the sector is known. Test
     `Transit_edge_skips_a_ship_until_its_sector_is_known`.
   - (c) allegiance-mcp `BotLodPolicy.HumanSector`: a human counts only with `Latest` and a known
     sector. Test `Bot_lod_ignores_an_unseen_human_until_its_sector_is_known`.
   Fail-first by mutation (the helpers are new, so the pre-fix code cannot compile the tests):
   reverting each gate fails its test (`DoesNotContain() Failure: Sub-string found` — DOCKED;
   `Assert.False() Failure` — transit out of 0; `Expected: null, Actual: 0`).
4. **Join-burst order.** G (every mission ship's PLAYERINFO) now precedes F (the joiner's own
   real-side PLAYERINFO), in the same packet — native `fsmission.cpp:406` (SendLobbyMissionInfo,
   loop `:3958-3968`) before `:408-411`, so a native joiner announces none of the humans already
   present. The test-4 baseline comment ("native announces every already-seated human to a joiner")
   was wrong and is fixed. New test `Native_joiner_announces_none_of_the_humans_already_present`
   (announcer replaying MISSIONDEF with the live stage): fails on `57b930bd` and on `1956a68c`
   (order assert; with it removed the announcer counts 2, expected 0).
5. **Matrix.** SIM-0464/0465: the P775 sentence moved from column 16 (notes) into column 8
   (`port_cite_or_reason`); every row of the three CSVs parses to 16/6/8 fields, LF.
6. **`NativeJoinAnnouncer` guard narrowed.** Only the recipient's own fresh ship's JOIN_SIDE before
   JOINED_MISSION is exempt (held and judged at JOINED_MISSION); MISSIONDEF no longer resets the
   epoch (the port sends it twice per join burst, which had disarmed the check for the rest of every
   run); the PlayerInfo table clears at Over.
7. **This report**: the egress "before" figure explained and re-measured (Traffic), the side-switch
   fail-first claim corrected, the audit result corrected.

**Gate after the fixups (measured):** `AllegianceCore.slnx` build 0 warnings / 0 errors (Debug and
Release); AOT publish of `Allegiance.Server` exit 0, 0 IL2026/IL3050; `tools/hud20-proof` builds;
targeted (`P775*`, `P774*`, `P773*`, `Hud20*`, `ScoreRoster*`, `TeamPane*`, `FedMsgDispatchCoverage`,
`ParallelBroadcastByteIdentity`, `ConnectionCacheCleanup`, `ShipStatusServerTests`,
`P730BotEfficiency`) 123/123 ×3 (Core 35, ClientCore 36, MonoGame 52); fast tier **PASS, 9,871
tests, 278.0 s, 0 failed**; showcase never-parks **5/5 pass** (1 m 50 s – 1 m 52 s each).
