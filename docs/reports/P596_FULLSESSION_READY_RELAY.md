# P596 — FullSession_ThreePeers ready-relay flake: root cause and fix

Date: 2026-10-10
Branch: master (not pushed)
Test: `Allegiance.ClientCore.Tests.FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`
Ledger: `docs/TEST_FLAKES.md:33` (was 1/3 "live-wire timing, suspect (P472)")

## Summary

The test is a TEST-SEEDING bug, not a product bug. The server's `PLAYER_READY`
relay is native-correct (same-side recipients); the harness merely failed to
guarantee that its own two "teammate" peer objects (peer0, peer1) actually
landed on the same side. When they did not, the (correct) same-side-only relay
skipped peer0 and phase 2 (`ready relay never reached both same-side peers'
rosters`, FullSessionKillIntegrationTests.cs:261) failed.

## Symptom and reproduction

Run alone (`--filter FullyQualifiedName~FullSession_ThreePeers`), measured on
current master:

- Initial reproduction: 2 pass / 3 fail in 5 runs (run 1 and 2 pass, runs 3-5 fail)
- Extended: ~50-60% fail, every failure at line 261 (phase 2), before any kill.

Server console in a failing run showed the relay firing to only **1 peer**:
```
[ready] 2 ship 8100 side 1 fReady=True -> 1 peer(s)
```
i.e. the server relayed peer1's ready to a single peer (itself). peer0's ship
had a *different* `SideId`, so it was not in the relay's recipient set.

## Root cause (mechanism, proven)

Instrumenting both the server relay and the test showed:

- Side assignment is fully deterministic in terms of SHIP ID: ships A,B -> side0,
  ship C -> side1, in every run (the spawner's `idx < 2` split).
- WHAT VARIES RUN-TO-RUN is WHICH of the test's three `ConnectionSession`
  objects receives which ship id.

`tests/.../FullSessionKillIntegrationTests.cs` `SeedThreeFlying` assigns the
first-two-spawned ships to side0 and the third to side1 (`idx < 2 ? Sides[0].Id :
Sides[1].Id`), implicitly assuming spawn order == the test's peer-object order
(peer0, peer1, peer2). P281's post-connect async side-select severed that:

- Ship ids are allocated at LOGON time (`AllocateShipId()`,
  `src/Allegiance.Server/Server.Dispatch.cs`, the `LogonRequest` handler) in
  **logon-dispatch order**, which is racy across the three sockets.
- Sides are assigned at AUTO-SEAT time in **JoinSeq (logon-order) order**
  (`Server.Lobby.cs` `AdvanceSideSelection`, drains `_pendingSideSelect`
  ordered by `PendingSideSelect.JoinSeq`).

Because the test fires all three logon requests back-to-back, the order the
server dispatches them (hence the ship id each peer gets, hence which ship is
side0 vs side1) is a coin-flip. Whenever peer0 happened to draw the "loner"
side1 ship, peer0 and peer1 ended up on opposite sides and the same-side ready
relay (correctly) skipped peer0 -> phase-2 assertion failed.

Empirical confirmation (diagnostics, since removed): side assignment per ship id
was stable across runs (`8098/8099->side0, 8100->side1`) while the peer->ship
binding varied run-to-run (peer0 was on side1 in several failing runs).

## Why it got worse — bisect

The FullSession test predates the join change that broke it:

- Test authored: `76b70751` (2026-09-06).
- `P281` "post-connect side/civilization select screen": `583a57ee` (2026-09-19).

Bisect probes (temporary worktrees, 10 isolated runs each, same filter):

| probe commit | context | isolated pass/fail |
|---|---|---|
| `b1de69ee` (parent of P281, P280) | synchronous seat-at-logon | 9 pass / 1 fail (~10%) |
| `583a57ee` (P281) | async pending-side-select join | 8 pass / 2 fail (~20%) |
| current master (HEAD 8dd70021) | matured join path | 2 pass / 3 fail .. 1 pass / 4 fail (~50-80%) |

**Named commit: `583a57ee` (P281, post-connect side/civilization select
screen).** Its change introduced the pending-side-select join
(`_pendingSideSelect` + `AdvanceSideSelection`), which decouples ship-id
allocation (logon dispatch order) from side assignment (JoinSeq order), turning
the test's spawn-order==connection-order assumption into a coin-flip. The rate
climbed from ~10% before P281 to ~20% at P281 and 50-80% by current master as
the join path matured (later refinements made drain order deterministic *by
JoinSeq*, but determinism by JoinSeq is not determinism by the test's connection
order).

## Native citations (why the product is NOT wrong)

The ready message and its relay, native:

- `Inc/Messages.h:380-384` — `DEFINE_FEDMSG(CS, PLAYER_READY, 45) // client is
  ready or not` ... `// Sent to everyone on team` with `bool fReady; ShipID shipID;`.
- Server relay — `FedSrv/FedSrv.CPP:6987-7032`, `case FM_CS_PLAYER_READY`:
  `pfsPlayer->SetReady(pfmPlayerReady->fReady);` then
  `g.fm.ForwardMessage(pfsMission->GetGroupMission(), pfm, FM_GUARANTEED)`.
  Players are all members of the mission group (`FedSrv/fsmission.cpp:405`),
  so native forwards the raw ready to every peer in the mission (both teams —
  the team lobby shows both sides' readiness), then recomputes side aggregate
  readiness.
- Aggregate ready + TEAM_READY broadcast — `FedSrv/fsmission.cpp:5485-5489`
  `PlayerReadyChange` -> `CheckForSideAllReady` -> `SetReady` (`5461-5473`,
  broadcasts `TEAM_READY(46)` to the whole mission).
- Client handler — `clintlib/appmsg.cpp:2297-2312`, `case FM_CS_PLAYER_READY`:
  `FindPlayer(pfmPlayerReady->shipID)` -> `PlayerInfo::Update` sets
  `m_fmPlayerInfo.fReady`, fires `OnPlayerStatusChange`, marks the side member
  list changed.

Observation (fidelity note, OUT OF SCOPE here): native forwards the ready to the
whole mission group (both teams), whereas the port's P84 relay
(`src/Allegiance.Server/Server.Dispatch.cs`, `fmid == PlayerReady.Fmid`)
selects same-side peers only (`_peerShips.Where(SideId == senderSideId)`). The
port is therefore NARROWER than native. Teammates (the case that matters for
"players' ready state reaching teammates") are covered either way; the
mission-wide breadth is a pre-existing documented (P84) divergence and is not
the cause of this flake. Left for a future fidelity task.

## Fix (test only, and why)

The product is correct, so the fix lives in the test's seeding. Restore the
determinism the test assumed by seating each peer sequentially on an explicit
side:

1. Read the two real side ids from `server.Missions[0].Sides[...]`.
2. `Seat(peer0, sideA)`, `Seat(peer1, sideA)`, `Seat(peer2, sideB)` — each
   connects, sends `LogonRequest` + `SendPositionReq(side)`, and is pumped
   until `MyShipId` is non-null BEFORE the next peer connects.

Two properties must hold together:
- The explicit `POSITIONREQ` side-pick makes the split independent of logon-
  dispatch order (same proven pattern as `ReadyCheckLiveTests`).
- Seating fully before connecting the next peer makes the spawner counter run
  peer0->0, peer1->1, peer2->2, so the picks MATCH the spawner's `idx<2`
  assignment. That prevents P390's side reconcile
  (`Server.Lobby.cs` `AssignShip` -> `RepositionShipToSideHome`) from firing,
  which would otherwise relocate peer2 to sideB's HOME SECTOR and break phase 6
  (the kill needs peer2 and peer1 in the same sector for projectile collision).
  All three peers stay in Sectors[0]: peer0/peer1 same side (phase-2 relay) and
  peer2 cross-side (phase-6 kill).

No product code changed. Server diagnostics added during investigation were
removed (`git diff src/Allegiance.Server/Server.Dispatch.cs` is empty).

## Before / after pass rates

`FullSession_ThreePeers` isolated (`--filter FullyQualifiedName~FullSession_ThreePeers`):

- BEFORE (current master, unpatched): 2/5, 3/5, 2/5, 3/5 ... ~50-60% pass
  (worst measured 1/5). Reported 2026-10-10 at 4/5 (1 pass / 4 fail).
- AFTER (patched): **32 consecutive isolated runs, 32/32 pass** (loop below).

After, full `Allegiance.ClientCore.Tests` assembly runs (FullSession passes in
every one; the failures in 7 of 10 runs are OTHER pre-existing ledgered flakes,
e.g. `JoinFlowLiveTests.EntityTable...`, `CrossSideCombatVisibilityLiveTests`,
`ShipDeleteGoneLiveTests`, `ReadyCheckLiveTests.Only_the_mission_owner`,
`RetriedConnectLiveTests`, `ClientWarpApproachSlowHullLiveTests` — all in
`docs/TEST_FLAKES.md`, out of scope for P596):

```
run 1: 579/579 (0 fail)          FullSession pass
run 2: 577/579 (2 unrelated)     FullSession pass
run 3: 578/579 (1 unrelated)     FullSession pass
run 4: 578/579 (1 unrelated)     FullSession pass
run 5: 577/579 (2 unrelated)     FullSession pass
run 6: 578/579 (1 unrelated)     FullSession pass
run 7: 577/579 (2 unrelated)     FullSession pass
run 8: 579/579 (0 fail)          FullSession pass
run 9: 579/579 (0 fail)          FullSession pass
run10: 579/579 (0 fail)          FullSession pass
```

32 isolated passes loop (patched master):
```
for i in $(seq 1 32); do dotnet test tests/Allegiance.ClientCore.Tests \
  --no-build --nologo -v:q --filter "FullyQualifiedName~FullSession_ThreePeers" \
  | grep -cE "Passed!"; done   # -> 32 x "1"  =>  pass=32 fail=0
```

Whole-solution build: 0 warnings / 0 errors.
Final suite: 2x `scripts/run-tests.sh` on the committed code (commit a031de17):

- Run 1: Lobby 12/12, MonoGame 1697/1697, ClientCore **578/579**, Core **3869/3871**.
  Failures all known ledgered flakes, NOT FullSession: ClientCore
  `RetriedConnectLiveTests.RepeatedConnects...`; Core
  `GameOverPlayersWireTests.Win_by_elimination...`,
  `CloakVisibilityGateLiveTests.Detectable_enemy_still_receives_the_cloaked...`.
- Run 2: Lobby 12/12, MonoGame 1697/1697, ClientCore **577/579**, Core **3867/3871**.
  Failures all known ledgered flakes, NOT FullSession: ClientCore
  `ShipDeleteGoneLiveTests.CrossSide...` + `DockOut...`; Core
  `StationsUpdateLiveTests.Visible_station...`, `P590PodRescueTests.Pod_pickup...`,
  `LiveRelaunchTests.Carrier_without_launch_slots...`,
  `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector...`.

`FullSession_ThreePeers` passed in both suite runs; every failure in both runs is
a pre-existing live-wire flake documented in `docs/TEST_FLAKES.md` (out of scope
for P596). Neither run introduced a regression attributable to this change.
