# P447b — own-ship predictor wired into the MonoGame client

Stage: P447b. Lands on `master` on top of P447a (`3096992c`). Wires the pure
`OwnShipPredictor` (P447a) into the MonoGame client's per-frame flight path so the
player's own hull, camera and HUD-speed react to controls on the same frame, and the
prediction reconciles toward the server's authoritative stream. No server-side or
wire change (still controls-only `ConnectionSession.CommandControls`).

## 1. What was built

A single owned transport, `OwnShipPredictionTransport`
(`src/Allegiance.ClientCore/Flight/OwnShipPredictionTransport.cs`), wraps the P447a
predictor and is the testable seam the client drives. `Game1` owns exactly one
(`_ownPredict`, `Game1.cs`) and drives it from `Game1.FlightInput.Update`:

- **Every frame**, AFTER the command that is sent through `_session.CommandControls`
  (manual *or* autopilot) is chosen, `_ownPredict.Step(frameDt, sentControls,
  sentStateM)` is called with the EXACT (controls, stateM) that went out — captured in
  the `else`/autopilot branches of the send block (`Game1.FlightInput.cs`). A turret
  gunner seats no SHIP_UPDATE, so prediction is held (not stepped) there.
- **Under the world gate** in the pose gather, `UpdateOwnShipPrediction`
  (`Game1.Prediction.cs`) resolves the own hull/afterburner, feeds each newly-arrived
  `ServerShipUpdate` exactly once (detected via `ShipEntity.LastUpdateTick`), and
  applies the hard-transition freeze/resume edges.

### 1.1 Rerouted pose consumers (scope item 2)

The render pose is now the PREDICTED pose when the predictor is active
(`_ownPredict.Active` = enabled ∧ hull known), else the raw last `ServerShipUpdate`:

| Consumer | Where | Notes |
|---|---|---|
| Own hull draw | `_poses[me]` set in `Game1.FlightInput` pose gather | predicted pos+ori; P255 mining-facing override applied on top of the predicted pose |
| Chase / cockpit / external cameras | `TryOwnPose` (`Game1.Camera.cs:40`) + Draw's `_poses[me]` (`Game1.Rendering.cs`) | camera eye/rotation follow the predicted pose |
| Damage debris vent | `GatherDamageEmitter` (`FlightInput`) | predicted pos/ori, wire velocity |
| Engine trail | `UpdateEngineTrail` (`FlightInput`) | predicted pos/ori, wire velocity |
| Nozzle glow | `UpdateNozzleGlow` (`FlightInput`) | predicted pos/ori |
| Nav strobes | `UpdateNavStrobes` (`FlightInput`) | predicted pos/ori |
| Motion trail | `UpdateMotionTrail` (`FlightInput`) | predicted pos/ori |
| Targeting / autopilot steering | `TryOwnPose` callers (T/U/Z/G brackets) | read the predicted pose through the shared seam |
| HUD speed readout | `Game1.Rendering.cs` HudFrame.Speed | predicted VELOCITY; all other HUD values (hull/shield/fuel/ammo/power) stay server truth |

`OwnShotEcho` (`EmitOwnShots`) already reads `_poses[me]`, so own-shot echoes ride the
predicted pose seam (weapon/projectile origin prediction itself is the named
follow-up, out of scope).

The P447a afterburner "known limitation" is refined: the predictor's afterburner max
thrust now comes from the OWN ship's real `Loadout` (resolved via the shop
catalogue), so the prediction boosts realistically under the afterburner.

### 1.2 Hard-reset wiring (scope item 3)

`UpdateOwnShipPrediction` freezes prediction to the held server pose across each
discrete discontinuity and re-seeds from the next server pose once it settles:

| Transition | Client signal wired | Action |
|---|---|---|
| Docked | `ShipEntity.StationId != -1` | freeze; hangar screen overlays |
| Death / gone | `ShipEntity.IsNotVisible` | freeze; pose meaningless until respawn |
| Ripcord | `world.RipcordActive` | freeze (ripcord pins turn rates) |
| Warp / sector change / teleport | `ShipEntity.SectorId` change (latched) | freeze, then resume once the new sector is reached |
| Any missed signal | P447a `OnServerUpdate` >20 m / >30° auto-snap | belt-and-braces resync |

The freeze is a `HardReset` (snap to the authoritative pose + hold); the resume edge
fires `Resume()` once the condition clears. Because `OnServerUpdate` still runs while
frozen (snapping on large discontinuities), the predictor re-seeds from the next
server pose after each transition.

### 1.3 Kill switch + periodic diagnostics (scope item 4)

- **Kill switch:** a client-only liveSettings-style flag, default **ON**:
  `LaunchOptions.OwnShipPrediction` (`Game1.Session.cs`), flipped by the `--noPrediction`
  launch flag (`Program.cs`) — the conceptual `liveSettings.ownShipPrediction` from
  P378's section, paired by hand while testing (no wire/server change, mirroring
  P385's `--debugMode` / P448b's `--noDeadReckon`). When off, the transport is inert
  and the hull/camera/HUD read the raw server pose (today's behaviour).
- **Periodic `[predict]` log:** `OwnShipPredictor` now accumulates the per-update
  reconciliation distance (count/mean/max) plus a cumulative snap count
  (`TakeCorrectionStats` / `SnapCount`); `LogOwnPredictionDiagnostics`
  (`Game1.Prediction.cs`) logs them ~every 2 s under the pose-gather gate, mirroring
  the P448b remote-correction log.

## 2. Verification

### 2.1 Whole-solution build

`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` → **0 warnings,
0 errors**.

### 2.2 The must-fail wiring test (loopback session)

`tests/Allegiance.ClientCore.Tests/OwnShipPredictionWiringTests.cs` drives the
PRODUCTION `OwnShipPredictionTransport` (not a test re-implementation) against a
fake/loopback authoritative server — a real server-side `Ship` integrated at 20 Hz
whose updates reach the transport RTT/2 late, exactly like the real client. It
changes the sent controls and measures the local RENDER pose response time for the
two paths:

```
RTT(pred)=66 ms
PREDICTED render-pose response time = 12.5 ms  (reacts on the same/next frame, 80 fps)
RAW/kill-switch render-pose response  = 87.5 ms (waits for the next server update / round trip)
```

- Prediction ON: render pose responds **on the same frame** (assert `<= 1.5·frame`).
- Prediction OFF (the kill switch / today's behaviour): the render pose cannot move
  before the full round trip (assert `>= 2·RTT/2`) and strictly lags the predicted
  pose. A second test asserts that with the kill switch off the render pose is
  bit-for-bit the raw delivered server pose.
- These are the on/off latency numbers recorded: ~7× (and growing with RTT).

Because the same production wiring is exercised, this is the "client-level test with a
fake/loopback session" that fails on unfixed code (without the Step-with-sent-controls
wiring, the render pose would only move on the next server update — the RAW arm above).

### 2.3 Correction statistics

The `[predict]` log reports mean/max per-update reconciliation distance + snap count
live. The underlying magnitudes are already characterised deterministically by the
P447a tests (unchanged by this stage): steady-state predicted-vs-true max
`2.448 m` / `0.483°` at LAN RTT; a sub-20 m server-side disturbance peaks `8.076 m`
then blends back within τ with no oscillation; a >20 m discontinuity snaps to the
server pose in one step. The mean/max/snap values in a live session will sit in that
regime for ordinary flight (reconciliation mostly a few metres, snaps only at genuine
discontinuities like warp/collision-resync).

### 2.4 Suites

- `Allegiance.ClientCore.Tests`: 345 passed. Failures are the two documented
  pre-existing live-wire timing flakes, named:
  `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`. Over four full runs the
  only other flake observed was the intermittent live-wire
  `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`
  (appeared in 1 of 4 runs; passes 3/3 in isolation) — same environmental contention
  family, unrelated to this change.
- `Allegiance.ClientCore.MonoGame.Tests`: 1012 passed, 0 failed (no regression from
  the MonoGame wiring).
- `Allegiance.Lobby.Tests`: 12 passed.
- `Allegiance.Core.Tests`: 3607 passed; 12 live-wire timing flakes — the ten
  documented P446/P447a ledger failures (ConquestTerritoryLiveTests Flag_capture /
  Flags_disabled / Conquest_win_in_one_mission, GameOverPlayersWireTests
  Conquest_win_in_one_mission / Conquest_win_lists_every_connected_player,
  LiveTurretGunnerBindingTests Board_binds_gunner,
  LiveRipcordTests.First_activation, LiveMoneyTests Board_refund,
  LiveChatSectorWireTests Individual_AllSector, AutoReloadOnEmptyTests
  Auto_reload) plus two occasional live-wire siblings seen this run
  (NpcShipFoundationLiveTests.Ownerless_npc_ship_appears_in_peer_heavy_updates_with_position,
  LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown).
  `Allegiance.Core` is untouched by this change (git diff is ClientCore/MonoGame
  only), so every Core failure is environmental, not a regression.

## 3. Live latency proof — honest status

The task's live lab (real server + `tc netem`, prediction ON vs OFF input→screen A/B,
collision observation) could **not be completed in this environment**, and I am
saying so rather than faking numbers.

### What was attempted / found

- The MonoGame client demonstrably runs here (headless `--capture` initialises the
  full pipeline, loads the 447-hull shop catalogue, renders offscreen).
- The local Docker server container `allegiancecore-server-1` (bridge net,
  `172.20.0.2`) is up on `:6073`, but has **no `NET_ADMIN` capability and no `tc`
  binary** — applying the wiki's documented netem-inside-container recipe would
  require rebuilding/restarting the shared container with `cap_add: [NET_ADMIN]` and
  `iproute2`, which I chose not to do to a 17 h-up, possibly-in-use server for a
  partial datapoint.
- **No passwordless `sudo`** on the host (`sudo: interactive authentication is
  required`) — so host-side `tc qdisc add dev lo …` is blocked non-interactively.
- **No input-injection tooling** (`xdotool`/`ydotool`/`wtype` all absent; can't be
  installed without `sudo`) — so a scripted yaw-step into the interactive client, and
  reading "when the render orientation first moved" from live logs, is not achievable
  here.

### The netem setup (for an operator with sudo / a cap-add container)

Follow the wiki page `allegiance-monogame-remote-latency-test`. Concretely: bring the
server up so the container has `NET_ADMIN` (a test-only `docker-compose.override.yml`
service with `cap_add: [NET_ADMIN]` and `iproute2` in the image), then:

```
docker exec <server-container> tc qdisc add dev eth0 root netem delay 100ms            # 100 ms RTT run
docker exec <server-container> tc qdisc del dev eth0 root                              # clear
docker exec <server-container> tc qdisc add dev eth0 root netem delay 100ms 40ms distribution normal   # +±20 ms jitter
```

(netem does not survive a container restart — reapply after any restart.) Launch the
client with `--noPrediction` off (prediction ON) vs on (raw) for the A/B; watch the
client's `[predict]` line for correction stats and the ship for the collision
behaviour.

### What the deterministic proof shows instead

The on/off latency contrast is already proven with real output from the production
transport (Section 2.2): predicted `12.5 ms` vs raw `87.5 ms` at a 66 ms round trip —
the input→screen lag the prediction removes. The correction magnitudes and the
"brief penetration then correction" collision behaviour are characterised by the
P447a deterministic tests (Section 2.3; prediction adds no client collision, so a
predicted ship can briefly fly into a rock until the server response snaps/blends it
back — native's own-ship catch-up does the same). The live-correction-statistics and
live-collision observations remain to be captured in a netem-capable environment.

## 4. Kill-switch name

`--noPrediction` (launch flag) ↔ `LaunchOptions.OwnShipPrediction` (default `true`)
↔ the conceptual `liveSettings.ownShipPrediction` (P378 section, client-only pairing;
no wire/server change). When off, `[client] --noPrediction: OWN ship rendered on RAW
pose (P447b prediction OFF)` is logged.

## 5. Out of scope / follow-ups

- Weapon / projectile ORIGIN prediction from the predicted pose (named follow-up).
- Remote ships (P448b — separate).
- Any server-side or wire change.
- P438–P440, P445, P446, P449.
