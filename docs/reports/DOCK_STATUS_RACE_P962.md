# P962 — stale spawn-docked SHIP_STATUS in `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected`

Base: `48950527` (worktree `allegiance-hermes-p962`, branch `hermes/p962`, of the public
`AllegianceCore` master at dispatch). Changes remain in the working tree; nothing is committed or
pushed. Artwork was loaded from this worktree's `local.env`; the fixed-test runs below show
`Skipped = 0`.

Raw run logs (every isolated, parallel, gate and diagnostic run quoted here) are in
`~/allegiance-evidence/P962/` on this workstation.

**Verdict: a test race, not a server bug.** The spawn-time DOCKED `SHIP_STATUS(90)` of the player's
own ship is put on the wire by the join burst; the test cleared the bot's status accumulator
*before* sending the launch, so whenever the receive thread had not yet decoded that frame the
decode appended it after the clear — item `[0]`, `StationId = 1051`, the ledgered
`Expected: -1 / Actual: 1051`. The server never emits a docked status after the launch: the status
builder reads `StationId` at build time and the undock clears it inside the `BUY_LOADOUT` tick.
The fix moves the clear to the first **post-launch** flying status, which is the first frame that
proves the join-burst docked frame is already decoded.

## 1. Reproduction (unmodified tree)

The unmodified test reproduces on this workstation in a single test process (the shared host was also carrying other lanes' work, load average ~5-6):

```
Assert.All() Failure: 1 out of 4 items in the collection did not pass.
[0]: Item:  ShipStatusMessage { Detected = False, HullId = 13, ParentId = -1, SectorId = 1053, ShipId = 8098, ··· }
     Error: Assert.Equal() Failure: Values differ  Expected: -1  Actual: 1051
   at DockDockingTests.Ship_nowhere_near_a_station_is_unaffected() DockDockingTests.cs:line 353
```

| Unmodified test, alone | Runs | Passed | Failed |
|---|---:|---:|---:|
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | 10 | 4 | 6 |
| Same method driven by a diagnostic replica (30 iterations in one process) | 30 | 18 | 12 |

The ledgered count (P910 three failures; P904/P906/P872/P890 entries) matches this rate: it is not
a rare flake, it is a ~40 % coin flip on this box.

The earlier ledger diagnosis ("the all-status assertion includes another ship docked at station
1051") was **wrong**: the assertion is already scoped to `s.ShipId == shipId`
(`DockDockingTests.cs:372-373`), and the failing item is the probe's *own* ship
(`ShipId = 8098`), docked at its spawn station. The two claims in the test's doc comment were also
wrong ("default spawn — the first sector of this map, Arrokoth, has zero stations" and
`// default spawn: first sector (no stations)`); since P653 H-06 the default spawn is **docked at
the side's base hangar**, which is sector 1053 / station 1051 on `XXV_simple.igc`. Both comments
are corrected.

## 2. Diagnosis — receipt order relative to the reset and the launch

A temporary diagnostic test (never committed; deleted before this report was written) booted the
same server/bot pair 30 times and recorded, for each iteration:

* whether the probe's own docked status was already in `bot.ShipStatuses` at the instant
  `ResetShipStatuses()` ran;
* the pump at which the first post-reset own status of each kind was decoded;
* the server-side `Ship.StationId` at that instant;
* the final contents of the accumulator.

| Iteration class | Count | Observation |
|---|---:|---|
| Own docked status already decoded at the reset | 18 / 30 | reset clears it; final list = 3 flying frames, no docked item |
| Own docked status **not** decoded at the reset | 12 / 30 | the frame lands immediately **after** the reset as item `[0]`: `StationId = 1051`, `State = StateDocked`; the 3 flying frames follow; `Assert.All` fails |
| ShipStatusServerTests-shaped replica (extra "join burst flushed" wait) | 24 / 24 | own docked status present at the reset; window closed in practice |
| MiningTests-shaped replica (extra observation wait) | 24 / 24 | same |

Sample lines from the run (first diagnostic, 30 iterations):

```
P962DIAG iter 2: statusesBeforeReset=1 ownDockedBeforeReset=True  revAtReset=3 postReset=3 flying=3 docked=False ...
P962DIAG iter 3: statusesBeforeReset=0 ownDockedBeforeReset=False revAtReset=2 postReset=4 flying=3 docked=True  dockedIsItem0=True firstDockedPump=1 serverStationWhenDockedDecoded=1051 ...
P962DIAG iter 25: statusesBeforeReset=0 ownDockedBeforeReset=False revAtReset=2 postReset=4 flying=3 docked=True dockedIsItem0=True firstDockedPump=2 serverStationWhenDockedDecoded=-1 ...
P962DIAG SUMMARY iters=30 presentAtReset=18 absentAtReset=12 dockedAfterReset=12 dockedIsItem0=12 assertAllWouldFail=12
```

Two facts pin the classification:

1. **Docked-first, then flying**: TCP delivers a connection's frames in order and the bot appends
   in decode order (`Bot.cs:2002-2005`), so a docked item *before* every flying item means the
   docked frame was **sent** before the launch was processed — i.e. it is the join-burst frame, not
   a late send.
2. **`firstDockedPump=2, serverStationWhenDockedDecoded=-1`** (iterations 25 and 29): the docked
   frame was decoded only *after* the server had already undocked the ship, which is exactly the
   in-flight window that a pre-launch clear races.

`bot.ShipStatuses` is a snapshot-on-read list (`Bot.cs:389-390`) and `ResetShipStatuses` clears it
under the same lock (`Bot.cs:393`); the reset therefore only removes frames that have already been
decoded, never frames still on the socket.

## 3. Server-order check — the port cannot emit a docked status after the launch

* The probe's spawn status is sent once, in the join burst, **before** any inbound message from the
  peer is drained: `Server.Lobby.cs:5451-5457` (`MakeOwnShipStatus(shipId, live)` →
  `SendPeer(from, …)`, logged as `[shipstatus] peer … -> SHIP_STATUS(90)`). Inbound dispatch drains
  in FIFO order at the top of `SimPass` (`Server.Broadcast.cs:120-125`), so this send precedes the
  `BUY_LOADOUT(8)` handler by construction.
* The status's docked/flying choice is derived from **live state at build time**:
  `Server.Broadcast.cs:1677-1712`, specifically `bool docked = ship.StationId is not null`
  (`:1680`) → `StationId`/`State` (`:1706-1708`). The pooled twin reads the same field
  (`Server.BroadcastBuffers.cs:137-166`), and the NPC/teammate/cross-side builders likewise
  (`Server.Broadcast.cs:1733-1748, 2543-2553`).
* The own-ship status is then resent only on a sector change or the `StatusRefreshTicks = 40`
  periodic refresh (`Server.cs:958`; `Server.Broadcast.cs:846-861`), at the **end** of the tick
  (`Server.Broadcast.cs:421`), after the mission update.
* The launch path mutates the state inside the tick that processes `BUY_LOADOUT`:
  `Server.ShipCommands.cs:2950-2953` (`if (buy.FLaunch && live.Ship.StationId != null)
  PerformUndock(...)`) → `UndockShipState` clears `StationId` and applies the launch pose
  (`Server.Docking.cs:2286-2320`, clear at `:2307`).
* `PerformUndock` itself sends only `FM_S_SET_CLUSTER(7)` (`Server.Docking.cs:2369-2394`); it emits
  **no** `SHIP_STATUS`. The first flying status is therefore a *later* periodic frame, built from
  the already-cleared `StationId`.
* With `broadcast.parallelBuild`, the per-peer builders run on workers but still read world state
  at broadcast time (`Server.Broadcast.cs:1236-1319`, snapshot phase then build phase), which is
  after the drain and after the mission update — the launch is never "after" the status in send
  order. Nothing in the coalescing/publisher path reorders per-peer frames (`SendPeer` is key 0,
  guaranteed, `Server.BroadcastSnapshot.cs:198-217`).

So: the server's only docked status for this ship is the join-burst frame, sent strictly before the
launch; there is no site that can send a docked status after the launch. **No server change is
needed.**

## 4. Server-order check — native `FedSrv`

* **Spawn is docked**: `FedSrv/fsmission.cpp:791-796` — `GetBase(pside)` →
  `GetIGCShip()->SetStation(pstation); pfsPlayer->ShipStatusStart(pstation);`. `ShipStatusStart`
  latches `c_ssDocked` + the station/sector (`FedSrv/fsship.cpp:437-445`).
* **Launch clears the station**: `FedSrv/FedSrv.CPP:6282-6292` — `if (bLaunch) { … SetStation(NULL); }`
  after the guaranteed `BUY_LOADOUT_ACK` flush (`:6247-6261`). The station transition reaches
  `ChangeStation(pship, pstationOld, NULL)` → `RepairAndRefuel` + `pfsShip->Launch(pstationOld)`
  (`FedSrv.CPP:13184-13191`).
* **Launch latches flying**: `CFSShip::Launch` (`FedSrv/fsship.cpp:175-184`) calls
  `ShipStatusLaunched()`, which sets `c_ssFlying` on the same per-side latch
  (`FedSrv/fsship.cpp:427-435`).
* **The wire frame is the periodic export of that latch**: `FedSrv/fsmission.cpp:4303-4322` walks
  every ship and copies the current `ShipStatus` into a `SHIP_STATUS`. Since the launch overwrites
  the latch before any later export, native cannot export a docked status after the launch either.
  `ShipStatusDocked` (`FedSrv/fsship.cpp:353-379`) latches the docked copy the same way.

Native's only ordering hazard is identical to this port's: the frames are queued in FIFO order, so
a docked frame that the *client* has not read yet can arrive after the state changed. Nothing in
native sends the docked frame after the launch was processed.

## 5. The fix

`tests/Allegiance.Core.Tests/DockDockingTests.cs:346-374`:

1. Spawn docked (unchanged), then send `SendBuyLoadout(hull, fLaunch: true)` **first**;
2. `PumpUntil` for the **first post-launch flying own status** (`StationId == -1 &&
   State == StateFlying`) **while the server-side latch is already null** — a frame that can only
   have been built after the undock cleared `StationId`; because frames are appended in connection
   order, its arrival also proves the join-burst docked frame is already decoded;
3. only then `ResetShipStatuses()`, and pump for **≥ 3** flying own statuses;
4. assert `≥ 3` flying frames, `Assert.All` over the probe's own post-launch statuses
   (`StationId == -1`, `StateFlying`), and the server-side `Ship.StationId` is null.

What the test still proves (nothing weakened): ≥ 3 genuine flying `SHIP_STATUS(90)` frames are
broadcast after the launch, every own status in the assertion set is flying/NA, and the server-side
docked latch stays null. The only thing removed from the assertion set is the pre-launch docked
spawn frame, which was never part of the claim. The stale comments are corrected (the default spawn
is the docked base hangar, not a station-less first sector).

## 6. Sibling tests sharing the reset-then-launch pattern

| Test | Shares the race? | Disposition |
|---|---|---|
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | Yes — clear before launch, `Assert.All` on the own statuses; measured 12/30 stale | **Fixed** |
| `MiningTests.Loopback_default_spawn_is_unaffected_and_statuses_stay_normal` (`MiningTests.cs:520-547`) | Yes — same clear-before-launch, same `Assert.All` over the own statuses; its observation wait is not a receipt barrier for `SHIP_STATUS` | **Fixed** (same barrier) |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` (`ShipStatusServerTests.cs:85-116`) | Yes — clear before launch, then `First(own)`; a stale docked frame makes the `StateFlying` / `StationId == -1` asserts fail. This is the entry P960 ledgered as "state-3/state-2 join receipt intermittency" (flying=3, docked=2); the unmodified test failed 1/20 alone here | **Fixed** (same barrier) |
| `DockDockingTests.FriendlyDock_sets_StationId_and_StateDocked_then_Undock_clears` (`:206-216`) | No — it clears only after a docked own status was already received, and asserts `Contains` (a late docked frame cannot fail a `Contains`) | Unchanged |
| `LiveTeleportDockTests.Undock_stationID_NA_still_undocks_with_no_teleport_ack` (`:473-489`) | Partly — `AwaitDocked` waits for the server-side latch, not the wire frame, then clears; but its assertions are `Contains`/`Assert.Empty` | Unchanged |
| `Hud17DetectionLiveTests`, `P493EnemySpottingLiveTests` | No — different pattern (no reset-then-launch); detection-bit assertions | Unchanged |

## 7. Verification

| Check | Result |
|---|---|
| Unmodified `Ship_nowhere_near_a_station_is_unaffected`, alone | 4/10 → **6 failures** (reproduction) |
| Fixed `Ship_nowhere_near_a_station_is_unaffected`, alone, final source | **20/20 pass, 0 skipped** |
| Fixed `PeerReceivesItsOwnShipStatus_InJoinBurst`, alone, final source | **20/20 pass** |
| Fixed `Loopback_default_spawn_is_unaffected_and_statuses_stay_normal`, alone, final source | **20/20 pass** |
| Two concurrent test processes, 2 rounds (target in both processes) | **4/4 pass** (load average 6.2–6.4) |
| Two concurrent test processes, 2 rounds (target + mining sibling), final source | **4/4 pass** (load average 8.9 → 8.5) |
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 121 tool projects built**, 0 warnings/0 errors |
| One full `Allegiance.Core.Tests` project run | **5557 passed / 0 failed / 0 skipped** in 18 m 27 s, then **aborted: test host out of memory** inside the pre-existing Perf-tier `P694MassEventTickTests` (see below). Not a docking/status failure. |
| One `tools/run-tests.sh --tier fast` gate run | **13,518 tests in 533.0 s — FAIL**: `Allegiance.Core.Tests` **6152 passed / 2 failed / 0 skipped**, `ClientCore.Tests` **2294 / 1 / 0**, `ClientCore.MonoGame` **4899 / 0 / 3 NotExecuted**, Lobby **139**, Update **28**; one Fast execution over the 10.244 s limit. All four offending methods were re-run **3× alone** and pass (load flakes). The three fixed tests live in `Allegiance.Core.Tests` (both failures there are non-task), so they passed in-suite. |

(`dotnet test … --no-build` per iteration; the two-process rounds are the bounded-parallel-load
reproduction required by the lane rules — no synthetic CPU load was generated; the host already
carried load average ≈ 6–9 from other lanes.)

### Full-project run abort (non-task)

`dotnet test tests/Allegiance.Core.Tests` on the whole project (no filter) ran 18 m 27 s and then
died: `The active test run was aborted. Reason: Test host process crashed : Out of memory.`
A Perf-tier-only diagnostic (`--filter "Tier=Perf"`) reproduced it: at 195 ships with
`coincident: true` the failure is `System.OutOfMemoryException` at
`CollisionQueue.Add` → `List<T>.AddWithResize` (`CollisionQueue.cs:18`) inside
`CollisionDetector.Detect` → `Sector.ResolveShipShipCollisions` → `Sector.Update` →
`P694MassEventTickTests.RunWorld` (`P694MassEventTickTests.cs:221`); ten P694 cases had already
missed their budgets under the concurrent lanes' load (`Assert.Equal() Expected 0 / Actual 7` is
the tick-budget assertion at `:226`). This is pre-existing Perf/collision work — production
collision code and other flakes are explicitly out of this task's scope — and every test that ran
before it (5557) passed with zero failures. It was not repeated three times because each repeat
crashes the shared host process; it is ledgered as open in `docs/TEST_FLAKES.md`.

### Fast-gate non-task failures (3× alone each)

| Failure from the Fast gate | Three isolated runs | Classification |
|---|---|---|
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | pass / pass / pass | Load flake (empty receipt collection) |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | pass / pass / pass | Load flake (tick-78 trajectory Δ) |
| `P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable` (10 civ rows) | pass / pass / pass (10/10 each) | Timing offender under load (10.338 s vs 10.244 s limit); no row failed |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (ClientCore) | pass / pass / pass | Already-ledgered loopback death-latch timing flake |


## 8. Files changed

* `tests/Allegiance.Core.Tests/DockDockingTests.cs` — barrier + corrected comments + explicit
  ≥ 3 flying-frame assertion.
* `tests/Allegiance.Core.Tests/MiningTests.cs` — same barrier.
* `tests/Allegiance.Core.Tests/ShipStatusServerTests.cs` — same barrier.
* `docs/TEST_FLAKES.md` — current-state entry with the real root cause and resolution.
* This report.

No production code changed (`src/` untouched).

## Native intent

Native spawns a fresh ship docked and latches `c_ssDocked` with the base station
(`FedSrv/fsmission.cpp:791-796` → `FedSrv/fsship.cpp:437-445`). A launch — the Buy-Loadout
`fLaunch` path or the undock path — clears the station (`FedSrv/FedSrv.CPP:6282-6292`) and the
station transition calls `CFSShip::Launch` → `ShipStatusLaunched`
(`FedSrv.CPP:13184-13191`, `FedSrv/fsship.cpp:175-184`), which latches `c_ssFlying`
(`FedSrv/fsship.cpp:427-435`). `SHIP_STATUS` frames are the periodic per-side export of that latch
(`FedSrv/fsmission.cpp:4303-4322`), so a client that has not yet read the docked frame can still see
it after the launch; the server never sends one after the state change. The test now models exactly
that: the launch is a wire event, and the assertion set starts after the first frame that could only
have been built post-launch.

## Modern approach

Wait on a **receipt that establishes ordering**, not on a wall clock: the first flying own-ship
status is a causal barrier (it can only be built after `StationId` is cleared, and connection order
guarantees every earlier frame is already decoded). Both server and client sides stay unchanged —
the shared status model already derives the docked bit from live state — and the same barrier is
applied to every sibling fixture that clears the accumulator before launching, so the classification
is uniform. The diagnostic harness was temporary, is deleted, and no scratch data was committed.
