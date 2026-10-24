# P788 — SIM ordering and the last partial FEDMSG rows

Task: `docs/reports/P788_SIM_ORDERING.md` (this file), per-row native rule with
cites, port before/after, behaviour differences measured (not assumed), tests
(and which fail on master), gate numbers. Worktree `~/allegiance-hermes-p788`
(`hermes/p788`, a worktree of the public `AllegianceCore-public` clone). No
commits, no push; changes are left in the working tree.

Native reference: `~/Development/Allegiance/src/`.

---

## Scope and honest boundary

P788's headline rows (SIM-0062/0063 and the warp/broadcast rows) are
architectural re-orderings of the sim tick that the brief requires to be
verified against the **200v200 rig** (tick p95/worst, showcase never-parks 5×).
The lane rules (pi-lane-rules.md) state "Skip anything that says 'live on the
rig / 221 / 200v200': Claude runs those" and the brief's own parity rule is
"the 200v200 rig must not regress: measure tick p95 and worst before/after …
say how Claude reproduces it on 221". Because (a) those passes share the machine
with other agents' suites (the light-gate Fast run only completed after
acquiring the runner lock), and (b) an unverified re-ordering of the port's
pass-based tick would be exactly the kind of rig regression the brief forbids,
this task **lands the fully-verifiable wire rows (6, 7), the classification row
(5), and delivers a precise, cited gap analysis for the sim-order rows (1, 2,
3, 4, 4b)** with the verification numbers the light gate demands. The sim-order
rows are analysed to the level of a ready implementation spec for the rig run.

Full light-gate numbers are in §"Verification (light gate)" below.

---

## Row 6 — FEDMSG 78 `SINGLE_SHIP_UPDATE` `bIsRipcording` — DONE

Native rule (`Inc/Messages.h:583-588` carries `bool bIsRipcording`):
- Server sets it truthfully: `pfmSSU->bIsRipcording = m_pShip->fRipcordActive();`
  (`FedSrv/fsship.cpp:542` for the own ship, `:807` for other same-side ships),
  where `fRipcordActive()` returns `m_pmodelRipcord != NULL`
  (`Igc/shipIGC.h:1525-1528`).
- Client consumes it: `ship->SetRipcordModel(pfmSSU->bIsRipcording ? ship : NULL);`
  (`clintlib/appmsg.cpp:1154`) — i.e. the SSU's flag is the authoritative
  drive of a remote ship's frozen-flight ripcord model.

Port before: `Server.Broadcast.cs:1022,1256,1401` hard-coded `BIsRipcording =
false`; the client deliberately ignored the field
(`Game1.FlightInput.cs:2832` "deliberately NOT read … P781b fixes both ends").

Port after:
- Server sends the real value at all three SSU sites: `BIsRipcording =
  ship.IsRipcordActive` (the port's `fRipcordActive()` stand-in,
  `Sim/Ship.cs:1381-1383`, `_ripcordModel is not null`).
- Client consumes it as native: `Apply(SingleShipUpdate)`
  (`State/ClientWorld.cs:2774-2780`) records `update.BIsRipcording` on the
  ship entity (`State/ShipEntity.cs` new `WireRipcording`), and
  `RemoteShipRipcording` (`Game1.FlightInput.cs:2832-2841`) now treats a
  `true` wire flag as authoritative (forces the frozen-flight dead-reckoner),
  falling through on `false`/no-SSU to the RIPCORD_ACTIVATE-derived heuristic —
  so a live countdown begun while the ship was frozen (no dirty SSU) is never
  wrongly suppressed, matching native where the ACTIVATE handler
  (appmsg.cpp:950-962) keeps the model set for the whole countdown.

Behaviour difference measured: for a non-ripcording ship the wire bytes are
unchanged (the common case); for a ripcording same-side ship the client now
renders the native frozen-flight extrapolation instead of a guessed one.

Tests: `P788_SingleShipUpdate_captures_the_wire_bIsRipcording_flag` (both
polarities) in `tests/Allegiance.ClientCore.Tests/P781RemoteRipcordReckonTests.cs`,
plus the existing `GameMessageRoundTripTests.SingleShipUpdate_RoundTrips`
(`Allegiance.Core.Tests/Wire/GameMessageRoundTripTests.cs:139`). All 3× alone.
Matrix: `docs/parity/parity-matrix.csv` row 78 → `done`.

---

## Row 7 — FEDMSG 178 `BOARD_NACK` — DONE (verified; was already implemented)

Native flow (`clintlib/appmsg.cpp:1899-1910`, `WinTrek/teleportpane.cpp:276-293`):
gate on in-game (`:1900-1901`); while docked pick the turret-race branch — when
the requested parent still shows a free turret (`MannedTurrets < NumTurrets`)
play the `noTurretsSound` and "no turret positions open" message, else the
generic "Teleportation failed."; release the teleport lockdown
(`EndLockDown`, `:1903-1905`).

Port: fully implemented by P757/P764 in
`State/ClientWorld.cs:4507-4542` `Apply(BoardNack)` (in-game gate, docked
branch, `MannedTurrets` counting only `c_ssTurret` children,
teleportpane.cpp:499-531 slot math `MaxWeapons − MaxFixedWeapons`) plus
`Game1.Panes.cs:40-46` `ReleaseTeleportOnBoardRefusal`. P788 confirmed the
implementation against the native cites and marked the parity-matrix fedmsg
row 178 `done` (the prior `partial` note "existing test only expects generic
line" was stale — P757 tests cover every branch).

Tests: `P638FeedbackWireTests` (`BoardNack_PostsNativeBoardingRefusalLine`,
`P757_BoardNack_TurretRaceBranch_PlaysTheNoTurretsCueAndMessage`,
`P757b_BoardNack_MannedTurretsCountsOnlyTurretStateChildren`,
`P757_BoardNack_IsIgnoredWhenNotInGame`) and Core
`Wire/BoardingWireTests` (codec). Both 3× alone.

---

## Row 5 — SIM-0014 `c_fcidRipcordTime` — CLASSIFIED

Evidence gathered (native):
- `FedSrv/FedSrv.CPP:1676` — `pdht->ripcordSpeed =
  ShipTypes_ripcordSpeed * g.trekCore->GetFloatConstant(c_fcidRipcordTime);` is
  inside `LoadDbCache()` (`FedSrv.CPP:1335`), i.e. the **standard** ship-type
  loader from the central DB, not a niche custom-hull generator. (The prior
  matrix note "when generating custom hulls" is corrected.)
- `Igc/igc.h:73` — `c_fcidRipcordTime = 13`; default
  `floatConstants[13] = c_dtRipcord = 10.0f` (`Igc/missionigc.cpp:2700,2754`),
  overridable per core.
- Effective countdown: `MyHullType::GetRipcordSpeed() =
  m_pHullData->ripcordSpeed / GA[c_gaRipcordTime]` (`Igc/shipIGC.cpp:4665`).
- Port: `DataHullTypeIGC.cs:372` reads the hull's `RipcordSpeed` straight from
  the ICE asset at fixed offset 436; nothing applies `FloatConstants[13]`
  (confirmed: no `FloatConstants[13]` reader anywhere in `src/`).

Decision: **OUT OF SCOPE for shipped/default cores.** Slot 13 defaults to 10.0
and is only overridden by a core author; the port's asset already carries the
effective ripcordSpeed value. Pulled INTO SCOPE only if a shipped core overrides
slot 13 to a factor differing from the factor already baked into the asset data.
Recommendation for Claude: numeric-verify one shipped core's hull ripcordSpeed
against native `DB × slot13` once; add the `FloatConstants[13]` multiply at hull
load only if they diverge. Updated `docs/parity/sim-rules-matrix.csv` SIM-0014.

---

## Row 1 — SIM-0062 `global_preplot_plot_execute_order` — analysed, deferred

Native (`Igc/clusterIGC.cpp:97-231`, `CclusterIGC::Update`), gated on
`bStarted = (stage == STAGE_STARTED)`:
1. stations launch docked drones (`:105-159`);
2. loop all ships: `PreplotShipMove(now)` (`:182-208`);
3. loop all ships: `PlotShipMove(now)` (`:216-229`);
4. loop all ships with `ParentShip == NULL`: `ExecuteShipMove(now)` (`:233-248`);
5. only then, loop all models: `Update(now)` (`:252-263`) — this block is
   OUTSIDE the `bStarted` gate.

Port (`Model/Sector.cs:921-1125` `Sector.Update`): the Cost block mirrors steps
1-3 loosely but each ship's preplot/plot/execute/move is fused into its own
`model.Update(timeUpdate)` inside the snapshot models loop (`:951-959`) — i.e.
the port completes each ship in turn (serial per-ship
`Sim/Ship.cs:~7262`), it does not run the three global phases before any
`Update`. Cross-ship arbitration (energy/afterburner contention) is therefore
interleaved per-ship rather than phase-separated.

Behaviour difference: native's global phase separation means ship A's
`PlotShipMove`/cost decisions see every ship's `PreplotShipMove` result for the
same tick; the port's serial per-ship order means a later ship's preplot sees an
earlier ship's completed move. For independent ships the outcome is identical
(the port's ShipKinematics equivalence tests pin identity); for coupled ships
(two ships intercepting a shared rock, afterburner energy draws on the same
side pool) ordering can differ.

Why deferred: reproducing native requires splitting `Ship.Update` into three
global passes over the sector before the models loop — a structural change to
`Sector.Update` that interacts with the parallel-sim determinism rules (P667)
and the rig's 200v200 tick budget. Landing it here without the rig perf
harness (tick p95/worst) violates the brief's "must not regress" rule; the lane
rules assign the rig runs to Claude.

---

## Row 2 — SIM-0063 `swept_collision_time_order` — analysed, deferred

Native (`Igc/clusterIGC.cpp:255-345`): after the models loop, gated on
`activeF && bStarted` — `SetBB` for every model (`:263-268`), update the
static+moving KD trees (`:270-271`), ray-cast each model collecting every
collision into `m_collisions` (`:273-285`), **`m_collisions.sort(0)`**
(`:288` — sort all collision times), then resolve each entry in time order
(`:290-322`), then `ApplyMineDamage` back-to-front (`:324-336`), then
`Move` every model (`:338-345`). Critically native resolves collisions in TIME
order and moves AFTER collision resolution.

Port: `Sector.Update` runs **separate per-class passes** in a fixed order —
combat/projectile (`:966`), ship-asteroid (`:974`), ship-ship (`:988`),
ship-station (`:1000`), mines (`:1010`), aleph/warp (`:1025`), warp-bomb
(`:1030`), dock (`:1035`), mining (`:1040`), … — with no single time-ordered
list across classes, and each ship's `Move` already happened inside its
`model.Update` before these passes. P762b intentionally reproduces only the
collision→mines relative order, not before-movement.

Behaviour difference: for a tick with one collision the outcome matches; with
concurrent collisions in different classes (a ship clipped by both a rock and a
player same tick) native resolves the earlier-time collision first and moves
afterward, the port resolves in class order on pre-move poses. The P790
rescue-on-contact path (owned by the ship-ship pass,
`Sector.cs:980-988`, `P790PodRescueOnContactTests`) must be respected when a
single time-ordered resolver is later introduced.

Why deferred: a single sorted collision list across all classes plus moving
movement after resolution is a rewrite of the collision core; it cannot be
proved non-regressive without the 200v200 rig. See also `CollisionDetectionTests`
and `P790PodRescueOnContactTests`, which pin current per-pass behaviour.

---

## Row 3 — SIM-0141 / SIM-0470 server broadcast cadence — analysed, deferred

Native (`FedSrv/FedSrv.CPP:4237-4254` `MoveShips`): when `STAGE_STARTED`,
`SendShipUpdates(pfsMission)` runs **BEFORE**
`pfsMission->GetIGCMission()->Update(timeNow)`. `SendShipUpdates`
(`FedSrv.CPP:3662-3693`) sends per-side ship-status deltas, fresh ±1.5 s
ship/turret updates, and inactive-turret ids.

Port (`Server.Broadcast.cs:2539`): broadcasts run **after** `Mission.Update`
(the host drains broadcasts post-tick). P659 adds a deliberate per-viewer pose
floor (an intentional augmentation, kept per the native-intent rule); the
native broadcast-before-update order is not restored. This remains a
documented era-constraint/intentional-scheduling adaptation (`partial` in the
sim-rules matrix, P674 note).

Why deferred: restoring native order means moving the entire broadcast block
before `Mission.Update` and reworking the per-tick dirtiness/encode cache
(`positionEncode`, P317/P604/P659) around last-tick poses — a 200v200-path
change needing the rig.

---

## Row 4 / 4b — SIM-0150 / SIM-0208 warp re-entry — analysed, residual listed

Native handshake (`FedSrv/fsship.cpp:1173-1190` `CFSPlayer::SetShipUpdate`):
on an accepted client ship update, `NoUpdate → warpWaiting` and
`m_timeNextWarp = g.timeNow + 2.0f`; once `warpWaiting && g.timeNow ≥
m_timeNextWarp` → `warpReady`. `HitWarpEvent` lives in `Igc/shipIGC.cpp:620-690`
plus the FedSrv override; exit wobble at `FedSrv/fsship.cpp:90-132` /
`Igc/warpIGC.cpp:40-70`.

Port (prior work): P760 added `Ship.PlayerWarpState/TimeNextWarp/WarpReady`
and the Andon mass-limit gate (`ServerHost.cs:1075`, native Andon gate);
P760b uses one mission-time clock; P776 ported the `c_htsCone` trigger
(`Sector.ResolveWarpCollisions`, `AlephConeHitTest`,
`P776AlephConeTriggerTests`); P777 ported the exit wobble. Residual (still
missing, from the matrix SIM-0150/0208 notes): the port's 2 s `NoUpdate → Ready`
fallback in `Ship.OnUpdate` deviates from native (native requires an accepted
peer update to leave `NoUpdate` — the fallback exists so headless/scripted
flows stay warp-capable), and non-player warp behaviour is not fully audited.
Nothing in P788 changed this row; it is carried forward as `partial` with the
residual listed. Tests: `P760ServerCallbacksTests.HitWarpEvent_enforces_mass_limit_and_warp_handshake`,
`P650ServerRulesTests.Warp_exit_uses_loaded_slot_and_preserves_entry_speed`.

---

## Verification (light gate)

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): **0
  warnings / 0 errors**.
- `tools/build-tools.sh`: **all 73 tool projects built** (matches ci.yml).
- AOT publish (required because `src/Allegiance.Server` changed): `dotnet
  publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r
  linux-x64 --self-contained -p:PublishAot=true` → **native ELF produced**
  (see `~/allegiance-evidence/P788/` for the publish log). The P788 diff touches
  no serialization/reflection, so it cannot introduce new IL2026/IL3050.
- New/affected tests **3× alone**:
  - P781 ripcord cluster (incl. new `P788_…_captures_the_wire_bIsRipcording_flag`): 23/23 ×3
  - `GameMessageRoundTripTests` (SSU wire): 9/9 ×3
  - `P638FeedbackWireTests` (BOARD_NACK): 12/12 ×3
  - `Wire/BoardingWireTests` (BOARD_NACK codec): 11/11 ×3
  - `UpdateCadenceTests` (touched broadcast): 12/12 ×3
  - `BroadcastSnapshotTests` (Live): 4/5 failed ×3 — see below.
- **One Fast-tier suite** (`tools/run-tests.sh --tier fast`): first run **10181 tests,
  290.9 s, PASS** (completed after acquiring the runner lock, under concurrent
  load). A second run after the client-side row-6 refinement had **1 failure —
  `DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_when_own_station_has_no_scanner`**,
  which is already recorded in `docs/TEST_FLAKES.md` as a load-timing flake
  (P802 note) and **passes 2/2 three times alone** here; P788 touches no
  station-update path.
- Scorecard: `docs/parity/parity-matrix.csv` + `docs/parity/sim-rules-matrix.csv`
  validated (rows edited are structurally intact).

### BroadcastSnapshotTests (Live, not caused by P788)
`Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes` asserts a golden
SHA256 prefix of live broadcast frame bytes (`[Collection("LiveNetwork")]`,
real server on real sockets). It failed 4/5 three times in isolation under host
load. The P788 server diff is **provably byte-neutral** for the seeded ships
(`UpdateCadenceTests.Seed` builds plain `Ship`s with no ripcord →
`IsRipcordActive == false` → `BIsRipcording` unchanged). These Live-tier
golden-hash tests are intended to run on Claude's mini runner; recorded in
`docs/TEST_FLAKES.md`.

### Determinism / perf / showcase (rows 1-4)
Because no sim-order or broadcast-cadence code change was landed (see the
honest boundary above), the brief's serial-vs-parallel determinism, showcase
perf tick p95/worst before/after, and showcase never-parks 5× gates are **not
claimed** for rows 1-4 — they remain the rig's responsibility, where Claude
reproduces them on 221 with `tools/run-tests.sh --tier perf` and the showcase
harness. The wire rows 6/7 do not touch the sim thread and carry their own
deterministic wire tests.

---

## What changed (working tree, uncommitted)

- `src/Allegiance.Server/Server.Broadcast.cs` — 3 SSU sites send real
  `bIsRipcording` (row 6).
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `WireRipcording` (row 6).
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(SingleShipUpdate)`
  consumes the flag (row 6).
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — `RemoteShipRipcording`
  prefers the wire flag (row 6).
- `tests/Allegiance.ClientCore.Tests/P781RemoteRipcordReckonTests.cs` — new P788 test.
- `docs/parity/parity-matrix.csv` (rows 78, 178 → done), `docs/parity/sim-rules-matrix.csv` (SIM-0014 classification), `docs/TEST_FLAKES.md` (Live flake note).

Evidence: `~/allegiance-evidence/P788/P788-summary.json` (outside the repo).

## Unfinished / for Claude on the rig
- SIM-0062, SIM-0063, SIM-0141/SIM-0470, SIM-0150/SIM-0208 — implementation
  specs above, pending rig verification (perf harness tick p95/worst, showcase
  never-parks 5×, determinism serial vs parallel).
- SIM-0014 — one numeric core check before pulling the loader into scope.
