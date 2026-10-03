# P508 — native own-ship model, part 2 (client): fly the ship locally, report pose every 0.15 s, apply SHIP_RESET; retire the predictor

**Stage:** P508 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Goal & outcome

Follow native's own-ship model on the **client** side (P507 did the server side):
the client **flies its own ship locally every frame** and **tells the server where it
is** every `c_dsecUpdateClient` (0.15 s) as a native `FM_C_SHIP_UPDATE(24)`; the server
sanity-checks and answers `SHIP_RESET(183)` on rejection (P507). The rendered own-ship
pose *is* that local simulation, at full frame rate, with **no reconciliation, blending
or snap** — the P447/P502/P505 `OwnShipPredictor` blend/snap is **retired**. Divergence
is corrected only by the server's real `SHIP_RESET` (snap to server pose + new cookie).

The port previously threw the client's pose away (`CommandControls` sent `Time=0`,
controls-only, zero pose) and relied on a bolted-on server-authoritative predictor that
sat ~15 m off with constant snaps (P502/P505 live logs). P508 replaces that with the
faithful native pattern.

## Native model followed

- **Client send** (`clintlib/clintlib.cpp:1861-1863`): every `c_dsecUpdateClient`
  (0.15 s, `Messages.h:226`, about 6.7 Hz), non-guaranteed, sends
  `FM_C_SHIP_UPDATE(24)` with `timeUpdate` (server-converted), the current `cookie`,
  and the full `ClientShipUpdate` pose (position / orientation / velocity / turn rates /
  power / controls / stateM), via `m_ship->ExportShipUpdate` (`shipIGC.h:1173+`).
- **Server receive** (P507, `FedSrv.CPP:4851-4864` → `ProcessShipUpdate`,
  `shipIGC.cpp:3139-3210`): ignores an update whose cookie mismatches; out-of-date if
  `|SimNow − timeUpdate/1000| ≥ 1.5 s`; legal-position check; replays the client pose
  to server time with the real flight model; orientation always accepted; position
  accepted within 20 m (`d < 400`) else rejected → **`SHIP_RESET`** with a `NewCookie()`.
- Native does **not** send on control change (`smSendUpdateNow` is commented out
  natively) — only on the cadence, each emission carrying the controls then in effect.
  P508 matches that.

## Key design decisions (with the why)

### 1. Local simulation replaces the predictor; no blend/snap reconciliation
`OwnShipPredictor` (blend the predicted pose toward the authoritative target on each
server update; snap on >20 m / >30°) and its transport are **deleted**. Their job is
taken by a new pure `OwnShipSimulator` (`src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs`)
which integrates the same `ShipKinematics.Step` (P448a) per frame with the same
controls the client sends, seeded from server poses and **never reconciled on the
periodic pose stream** — exactly native (the client *is* the authority; the server
only sanity-checks and resets).

### 2. Server-converted time — the P464-estimator units trap (a real finding)
The P507 server gate is `|SimNow − timeUpdate/1000| < 1.5 s`, i.e. `timeUpdate` must be
server sim-time in **milliseconds**. The own ship's `ServerShipUpdate.Time` slot is the
server **tick index** (`Server.cs` `TickIndex = _loopIterations`, once per sim tick),
*not* seconds — confirmed at `Server.Broadcast.cs:472` and noted at
`ShipDeadReckoner.cs:128`. The P464 `ServerClockEstimator` treats that slot as seconds
(its test feeds seconds and expects seconds), so stamping the SHIP_UPDATE with its raw
output would be off by the update rate (~20×) and always rejected. This is a latent
unit bug in the turret path (the live MonoGame gunner's ACTIVE timestamp — out of
P508's scope to fix, but documented). **P508 therefore feeds the new
`OwnShipServerClock` from `FM_CS_PING(35)` echoes** instead: the server stamps
`timeServer = (uint)(SimNow * 1000)` (`Server.Dispatch.cs`, `FedSrv.CPP:4897-4911`),
giving server sim-**ms** directly, independent of the tick rate. A least-squares fit of
`serverMs = a·local + c` (same jitter-robust shape as P464, but in ms) yields
`timeUpdate = ServerTimeMs(localNow)`.
- The client now sends `FM_CS_PING` ~1 Hz while connected (`PumpServerClock`, called
  every frame in `Game1.Update`) and feeds the clock from the echoes.

### 3. Cookie from SET_CLUSTER / SHIP_RESET
The server drops a native update whose cookie mismatches the per-player cookie it
delivered in `SET_CLUSTER(7)` (join + warp) and renews on each `SHIP_RESET` (P507).
`ShipEntity` now carries `Cookie`; `ClientWorld.Apply(SetCluster)` stamps it and a new
`Apply(ShipReset)` stamps the renewal + latches `HasPendingReset` for the own ship.
`Game1` adopts it under the gate and echoes it on every native send.

### 4. `SHIP_RESET` handling & transition re-seed
`ClientWorld.Apply(ShipReset)` updates the entity's authoritative pose + cookie and
flags the own ship; `Game1.UpdateOwnShipSimulation` snaps the local sim to the reset
pose, adopts the new cookie, clears the flag, and counts it. The predictor's
freeze/resume edges (dock, death, ripcord, warp / sector change) are kept and re-used:
on a transition the sim is seeded to the server pose and held; on resume it continues
from that pose.

### 5. `--noPrediction` is now a debug **visualization** toggle only
The local sim ALWAYS runs and always drives the native SHIP_UPDATE (it is the
authority). `--noPrediction` only switches the rendered own-ship pose to the raw last
`ServerShipUpdate` pose (the old 20 Hz stepped view) for an A/B. Documented in
`Game1.cs` / `Game1.Session.cs` and the opts log line.

## Implementation

- **`src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs`** (new): pure local own-ship
  simulation — `Step` integrates `ShipKinematics` (sub-step ≤ one server tick, the P502
  anti-over-integration guarantee), `Seed` snaps to a server pose + resumes, `Freeze` /
  `Resume` for transitions, exposes pose/velocity/orientation/turn-rates/controls/
  stateM/power for rendering and the wire.
- **`src/Allegiance.ClientCore/Flight/OwnShipServerClock.cs`** (new): server-time in ms
  from PING echoes (LSQ fit), unit-independent — see decision 2.
- **`src/Allegiance.ClientCore/Flight/ShipUpdateCadence.cs`** (new): the 0.15 s native
  send cadence (fires immediately after Reset/resume).
- **`src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs` / `OwnShipPredictionTransport.cs`**: **deleted** (retired).
- **`src/Allegiance.ClientCore/State/ShipEntity.cs`**: add `Cookie` + `HasPendingReset`.
- **`src/Allegiance.ClientCore/State/ClientWorld.cs`**: stamp cookie in `Apply(SetCluster)`;
  add `Apply(ShipReset)` + dispatch case + `ClearOwnShipResetFlag`.
- **`src/Allegiance.ClientCore/Session/ConnectionSession.cs`**: add `SendShipPose`
  (native SHIP_UPDATE with real pose/cookie/time), `SendPing`, PING-echo collection
  (`TakePingEchoes`), and the `ShipReset` receive case.
- **`src/Allegiance.ClientCore.Game/Game1.Prediction.cs`**: rewritten to drive
  `OwnShipSimulator` (hull reconfigure, first-sight seed, SHIP_RESET re-seed,
  freeze/resume) + `MaybeSendOwnShipUpdate` (cadence native send) + `PumpServerClock`.
- **`src/Allegiance.ClientCore.Game/Game1.cs` / `Game1.FlightInput.cs` / `Game1.Camera.cs` /
  `Game1.Rendering.cs` / `Game1.Session.cs`**: replace `_ownPredict` with `_ownSim`;
  the flight send becomes the cadence `SendShipPose` (was per-frame `CommandControls`);
  render/camera/HUD read the local sim pose; docs updated.
- **Tests**: deleted the 4 retired-predictor files; added `OwnShipSimulatorTests`,
  `OwnShipServerClockTests`, `ShipUpdateCadenceTests`, `ClientWorldShipResetTests`,
  `NativeShipUpdateWireTests`, and the **required live** `NativeOwnShipFlightLiveTests`.

## Out of scope (confirmed not touched)
- Server acceptance rules (P507).
- Remote-ship dead reckoning (P448b).
- The P464 turret `ACTIVE_TURRET_UPDATE` timestamp unit bug (tick-vs-seconds in its
  estimator feed) — documented as a finding; P508 routes around it via PING; fixing the
  turret path is a separate task.
- Afterburner engagement (no flight input exists; power reported flat 0, which the
  server ignores in its replay — `BuildKinematicsInputs` uses its own state).

## Verification

### Failing-first
The discriminating **live** test asserts the server **accepted > 0** native poses in
normal flight. A client that fell back to the pre-P508 behavior (legacy `Time=0`,
zero-pose, controls-only) would never produce an `ACCEPTED` — the server logs LEGACY
instead — so `accepted > 0` (and `rejected == 0`, `client SHIP_RESETs == 0`) red when
the native send is removed. The pure `OwnShipSimulatorTests` (same-frame response, seed
snap + resume, freeze) pin the local-simulation semantics that replace the predictor's.

### New tests, all green
- `OwnShipSimulatorTests` (6) — same-frame control response, cruise translation,
  no-hull inertness, freeze/resume, seed-snap-and-continue, reported controls/stateM.
- `OwnShipServerClockTests` (4) — stays within the ±1.5 s gate under jittered RTT,
  ready gating, single-echo slope-1, consistent-RTT offset absorption.
- `ShipUpdateCadenceTests` (3) — fires immediately + every 0.15 s, reset, long-pause.
- `ClientWorldShipResetTests` (3) — SET_CLUSTER cookie, SHIP_RESET cookie+pose+flag+clear,
  non-own-ship no-flag.
- `NativeShipUpdateWireTests` (1) — the native SHIP_UPDATE round-trips time/cookie/
  full pose.
- **`NativeOwnShipFlightLiveTests` (1, REQUIRED)** — a REAL `ConnectionSession` drives the
  REAL `OwnShipSimulator` + `OwnShipServerClock` (from REAL PING echoes) + `SendShipPose`
  over a REAL TCP wire to a REAL in-process `Allegiance.Server` (P507, paced to real time
  like production), flying **cruise → hard turn → throttle cut to −0.8 then decel**
  (the P505 case). Server's P507 `[ctrlak]` log is captured for the acceptance stats.

### Live measurement (the number Domingo asked for)
From the real wire against the real P507 server, `NativeOwnShipFlightLiveTests`:

    [P508-LIVE] server [ctrlak] over normal flight:
      accepted=110 rejected=0 out_of_date=0 invalid=0; client SHIP_RESETs=0
    [P508-LIVE] SHIP_RESET #1 — re-synced (induced divergence)
    [P508-LIVE] induced-divergence SHIP_RESETs: 1

**~0 rejections in normal flight** (target met): **110 accepted, 0 rejected,
0 out-of-date, 0 invalid, 0 client SHIP_RESETs** across cruise + hard turn + the −0.8
throttle-cut decel. The time gate, cookie gate, and the 20 m pose-accept all pass
end-to-end on the production stack. An **induced 500 m divergence** produced exactly
**1 SHIP_RESET**, and the sim re-synced to the server pose (< 5 m) — the SHIP_RESET
recovery path works live. Undock/warp transitions are covered by the pure
freeze/seed/reset unit tests plus this same re-seed path (the live harness spawns the
ship flying free; driving a full undock→warp in the wire harness was judged
disproportionately brittle, so the transitions are proven at the unit + re-seed level).

### Whole-solution build
`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` → **0 warnings,
0 errors**.

### Full suite — `scripts/run-tests.sh` (P471), three consecutive runs
Grand total per run: **5343** = Lobby 12 + MonoGame 1207 + ClientCore 418 + Core 3706.

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1207/1207 | 417/418 | **3706/3706** (clean) | ClientCore `FullSessionKillIntegration…CrossSideKill` |
| 2 | 12/12 | 1207/1207 | 417/418 | 3701/3706 | ClientCore `FullSessionKillIntegration…CrossSideKill`; Core `WarpBombTests…`, `NpcShipFoundationLiveTests…`, `LoadoutChangeLiveTests…`, `LiveTreasurePickupTests…`, `LiveCountdownStartPingTests…` |
| 3 | 12/12 | 1207/1207 | 417/418 | 3702/3706 | ClientCore `CrossSideCombatVisibilityLiveTests…`; Core `ShipUpdateBudgetLiveTests…`, `LiveStationRescueTests…`, `LiveCountdownStartPingTests…`, `AddPartTests.Second_part_touch…` |

Every failure is a **pre-existing live-wire timing flake in an assembly or test this
task did not touch**. P508 changed no code in `Allegiance.Core`, `Allegiance.Server`,
or `Allegiance.Core.Tests` (git confirms), so every Core failure is definitionally
pre-existing — and they rotate run-to-run (run 1 had a fully clean Core 3706/3706;
runs 2–3 show a different 4–5 each). The ClientCore failures rotate across three
*different* tests (`FullSessionKillIntegration` ×2, `CrossSideCombatVisibility` ×1);
grep confirms those two files reference **zero** P508 symbols (they use Bot peers and
cross-side visibility relays, not the own-ship native path). `docs/TEST_FLAKES.md`
documents this rotating class: `WarpBombTests` (:183), `LiveRipcordTests`,
`SideEliminationLiveTests`, `AddPartTests` (:247, and P507's runs),
`LiveWinConditionTests`, `LiveCountdownStartPingTests` (:37), `LiveTreasurePickupTests`
(:157), `LoadoutChangeLiveTests` (:185), `LiveStationRescueTests` (:156),
`ShipUpdateBudgetLiveTests` (:158), `FullSessionKillIntegration…CrossSideKill` (:33),
and the general P487/P498/P499/P500 observation sections list the same rotating
live-socket set. **No failure reproduced on the P508 area** — the 18 new P508 tests
(all of `OwnShipSimulatorTests`, `OwnShipServerClockTests`, `ShipUpdateCadenceTests`,
`ClientWorldShipResetTests`, `NativeShipUpdateWireTests`, and the required live
`NativeOwnShipFlightLiveTests`) passed in every run and in isolation.

## Files changed
- `src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs` — **new**.
- `src/Allegiance.ClientCore/Flight/OwnShipServerClock.cs` — **new**.
- `src/Allegiance.ClientCore/Flight/ShipUpdateCadence.cs` — **new**.
- `src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs`, `OwnShipPredictionTransport.cs` — **deleted** (retired).
- `src/Allegiance.ClientCore/State/ShipEntity.cs`, `ClientWorld.cs` — cookie + ShipReset.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SendShipPose`, `SendPing`, PING echoes, ShipReset case.
- `src/Allegiance.ClientCore.Game/Game1.Prediction.cs`, `Game1.cs`, `Game1.FlightInput.cs`,
  `Game1.Camera.cs`, `Game1.Rendering.cs`, `Game1.Session.cs` — rewire to OwnShipSimulator.
- `tests/Allegiance.ClientCore.Tests/…` — new + retired test files (see Verification).
- `docs/reports/P508_NATIVE_OWN_SHIP_CLIENT.md` — this report.

## Commit
`master` only, **not pushed** (branch verified with `git branch --show-current`).
Working tree has only pre-existing untracked files plus this task's new files,
committed.
