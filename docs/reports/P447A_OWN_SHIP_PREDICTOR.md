# P447a — own-ship client predictor (pure module; server stays authoritative)

Stage: P447a. Landed on `master` (top commit at landing: f3d76563 P448b).
Scope: the pure, testable `OwnShipPredictor` in `Allegiance.ClientCore`, plus the
latency-hiding proof driven by a simulated authoritative server. Wiring into
`Game1` rendering / the camera is P447b; nothing here changes the server or the
wire.

## 1. Why this task exists

The gameplay-parity pass found the biggest single cause of "flying doesn't feel
as good as the original": **the player's own ship isn't simulated on the client.**

- Native is client-authoritative for its own ship: the client runs
  `ExecuteShipMove` every frame and sends the full pose
  (`FM_C_SHIP_UPDATE(24)` + `ClientShipUpdate`); the server accepts the
  orientation outright and the position within 20 m, otherwise `SHIP_RESET`.
  Controls take effect on the same frame, at render frame rate.
- The port is server-authoritative with no prediction: the client sends controls
  only (`ConnectionSession.CommandControls`), and the camera + own hull are
  drawn from the raw last `ServerShipUpdate`. The server runs at 20 upd/s, so a
  turn or throttle change shows up a full round trip plus up to 50 ms late and
  the view steps at 20 Hz.

This stage keeps the server authoritative (no anti-cheat / protocol change) and
adds client-side prediction for rendering. `OwnShipPredictor` is the pure,
testable heart; P447b wires it into the pose/camera path.

Prerequisites read first: `docs/reports/P448A_KINEMATICS_DEAD_RECKONER.md`
(provides `ShipKinematics.Step` and `ShipDeadReckoner`, both reused here — no
physics is duplicated) and `docs/reports/P446_SIDE_GA_FLIGHT_SCALING.md`
(GA-scaled hull stats on the client, consumed through
`ShipDeadReckoner.HullStats.FromHull`).

## 2. What was built

`src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs` — a pure class (no
MonoGame types; it depends only on `Allegiance.Core`):

- **Primary signal = local prediction.** `Step(dt, controls, stateM)` advances
  the predicted position/velocity/orientation/turn-rates with
  `ShipKinematics.Step` using the **same controls the client sends that frame**
  (the P447b wiring passes exactly the `CommandControls` payload), chunked to
  `Mission.TimeIncrement` (0.25 s) exactly like the server, and integrates
  `position += velocity·dt` per chunk. Because the server integrates the same
  controls through the same integrator, the prediction tracks where the ship
  truly is; the received updates are a secondary correction signal.
- **`OnServerUpdate(update, hull, afterburnerMaxThrust, arrivalTime, now)`** builds
  the authoritative target by replaying the update to `now` with a
  `ShipDeadReckoner` (P448a) anchored at `arrivalTime`, then reconciles:
  - if the position error exceeds **`SnapDistance` = 20 m** or the orientation
    error exceeds **`SnapOrientationDegrees` = 30°**, it **snaps** to the target
    outright (equivalent to native's `SHIP_RESET`);
  - otherwise it applies a **one-shot exponential blend**: each update moves the
    pose a fraction `α = 1 − exp(−UpdateInterval/τ)` of the way to the target,
    so the *error offset decays geometrically* across successive updates
    (vanishing in ~τ) — this is the task's "decaying the error offset", not a
    lerp toward stale poses.
  - **velocity** is adopted from the update directly; **turn rates** are blended
    by `α`. Orientation consequently converges through the turn-rate blend (a
    physically-consistent re-orientation), never a pose lerp.
- **Hard transitions.** `HardReset(pos, vel, ori, turnRates)` snaps to a server
  pose and **freezes** prediction (`Frozen = true`); `Resume()` re-arms it.
  `HardReset` is the transport P447b drives for the discrete states below. The
  snap inside `OnServerUpdate` (an ordinary resync) does *not* freeze — prediction
  resumes on the next `Step`.

### 2.1 Correction design detail and why

Early iterations corrected by continuously "bleeding" a stored residual into the
integration state every frame. For a pure velocity disturbance that **fought the
client's cruise controller** — the throttle-cruise branch actively kills lateral
velocity each frame, so continuously re-injecting the server's lateral velocity
produced a frame-rate limit cycle (observed as a ~40 Hz swing between 0 and
2.5 m error). The one-shot per-update blend avoids that: it corrects at the 20 Hz
update boundary and lets the controller run uninterrupted between updates. The
residual itself (between-updates prediction "racing ahead" by ≈ speed·Δt before
the next re-sync) is inherent to predicting at render rate against a 20 Hz
authoritative stream; it is bounded by the update cadence, is not divergence,
and is far smaller than the raw pose error (Section 4).

Setting **velocity = update.Velocity** outright (rather than blending it) is
deliberate: the cruise controller would otherwise erode a weakly-blended lateral
velocity faster than the blend imparted it, so the predictor would never track
the knocked server's velocity. Adopting velocity directly makes the prediction
follow the server's authoritative flight state.

### 2.2 Constants (all configurable) and justification

| Constant | Value | Justification |
|---|---|---|
| `SnapDistance` | 20 m | Native's own-ship acceptance threshold — `d² < 400²` (`shipIGC.cpp:3176`); a snap fires exactly where native would reject the pose / `SHIP_RESET`. |
| `SnapOrientationDegrees` | 30° | Native accepts the own orientation outright (no validation, `shipIGC.cpp:3139-3183`), so this is port-local: beyond ~30° of facing disagreement a blend reads as a visible ghost-spin of the player's own hull; below it the blend is imperceptible. |
| `CorrectionTimeConstant` τ | 0.15 s | Inside the brief's 100–200 ms window: hides the correction within a few 20 Hz updates while never jolting the camera. |
| `UpdateInterval` | 0.05 s | The server's 20 Hz tick (`server.json:5`); sizes `α = 1 − exp(−Δt/τ) ≈ 0.28` per update. |

## 3. Hard-reset states — client signals

Each of the following forces an immediate hard reset to the server pose with no
prediction (P447b observes the signal and calls `HardReset` + `Resume` once the
transition settles). The signals are how `ClientWorld` / `Game1` already detect
them:

| State | Client signal |
|---|---|
| **Docked** | Own ship leaves the flight-update stream: `SHIP_DELETE(SDR_DOCKED=3)` for the own id (`ClientWorld.Apply(ShipDelete)`, and `Apply(DockedNotify)`, `ClientWorld.cs:1915,2059-2071`); the MonoGame hangar/loadout screen (P268) takes over while docked. Freeze on dock, resume on launch/undock. |
| **Ripcording** | `ClientWorld.RipcordActive` set by `RIPCORD_ACTIVATE(185)` (`ClientWorld.cs:752-770`, `Apply` at :1161). A ripcord pins turn rates and moves the ship to the target sector; hold the server pose through the countdown and the crossing. |
| **Dead** | `ShipEntity.IsDead` latched by `SHIP_DELETE(SDR_KILLED=1)` / `KILL_SHIP`; `ShipEntity.IsNotVisible` (IsDead ‖ IsGone). Freeze on death; the pose is meaningless until respawn. |
| **Lifepod** | On death the own hull id swaps to the lifepod hull (endurance via `Ship.LifepodEnduranceFraction`, P377). The eject is a pose discontinuity; freeze + reset on the hull swap. |
| **Warp / sector change** | The own ship's `ShipEntity.SectorId` changes (server `OnShipWarped` → client reflects the own `SectorId`; `SET_CLUSTER` / ship-status set `e.SectorId`, `ClientWorld.cs:1129,1233`). Warping moves the ship across sectors — the sharpest edge; hold the new sector's server pose. |
| **Teleport** | A probe-teleport (`RIPCORD_ACTIVATE` to a same-sector probe) produces a large pose discontinuity. Two-layer protection: the > 20 m snap inside `OnServerUpdate` covers the discontinuity even if the wiring misses the signal, and the wiring freezes on the teleport signal. |

## 4. Latency-hiding proof (pure, deterministic)

The tests (`tests/Allegiance.ClientCore.Tests/OwnShipPredictorTests.cs`) drive
the predictor with a **simulated authoritative server**: a real server-side
`Ship` running the same controls, whose updates reach the predictor delayed by a
configurable RTT/2 and sampled at 20 Hz — exactly how the real client receives
its own `ServerShipUpdate`.

### 4.1 Control change reacts on the same step; the raw baseline waits for the round trip

RTT = 70 ms (RTT/2 = 35 ms), hard-right yaw commanded mid-cruise:

```
PREDICTED pose response time = 12.5 ms   (reacts on the same/next frame, 80 fps)
RAW baseline response time   = 87.5 ms   (waits for RTT/2 up + tick + RTT/2 back)
```

- The **must-fail** claim: today's raw-server-pose behaviour cannot react before
  the full round trip. The test asserts predicted responds within ~a frame
  (`<= 1.5·frame`) **and** raw cannot respond before `RTT` (`>= 2·RTT/2`). It
  fails against the current model, passes with the predictor — recorded above.
- Factor: the predictor is ~7× quicker at this RTT, and the gap grows with RTT.

### 4.2 Steady state (constant controls, no loss) stays small

Turning-climb at ~60 % throttle, RTT 36 ms, measured over 1 s against the true
server pose:

```
steady-state predicted-vs-true: maxPosErr = 2.448 m   maxOriErr = 0.483°
```

The residual position error is dominated by the **reporting lag** — the received
update is dead-reckoned from its local arrival time (P448a's anchor), so the
target trails the true pose by ≈ RTT/2·speed — plus wire quantization. It is
small at realistic LAN RTT and scales with RTT; the facing stays well under a
degree. Contrast: the raw pose trails the full RTT·speed (P448a showed ~98 m at
1 s at 120 u/s).

### 4.3 Server-side-only disturbance blends back within τ, no oscillation

A sub-20 m collision-like recoil (8 m sideways + lateral velocity) applied to the
server only, unseen by the predictor; the **reconciled** error (sampled at each
update boundary, excluding the inherent speed·Δt sawtooth) is:

```
disturbance @1.250s (+8 m knock): baseline≈1.261 m   peak=8.076 m   final=1.234 m
```

- Peak (8.08 m) stayed under the 20 m snap ⇒ it **blended**, not snapped.
- It converged back to the pre-disturbance steady state within the window
  (several τ), and the settled tail never rose above baseline ⇒ **no oscillation /
  ringing** (early iterations that bled velocity into the state did oscillate —
  see §2.1; the one-shot blend fixed it).
- The paired test **snaps** when the error is over 20 m: a +140 m server-side
  displacement collapses the predicted-vs-true error to ~0.000 m in a single
  step (snap), instead of blending over several τ.

### 4.4 Freeze / resume

`HardReset` freezes the pose (`Step` is inert even under full-yaw controls);
`Resume` re-arms prediction from the current pose.

## 5. Known limitations (documented)

- **Collisions are server-side only.** A predicted ship will briefly fly through
  a rock until the server's response moves it and the correction snaps/blends it
  back. No client collision is added (native's own-ship catch-up does not collide
  either).
- **Reporting-lag bias.** Because `ServerShipUpdate.Time` is a tick index the
  client can't map to seconds (P448a), the authoritative target is anchored at
  the update's local arrival time, so the reconciled pose trails the true pose by
  ≈ RTT/2·speed (Section 4.2). At the port's realistic low RTT this is a couple
  of metres; it is far smaller than the raw pose's full-RTT trail.
- **Inter-update sawtooth.** Predicting at render rate against a 20 Hz stream
  produces a bounded ≈ speed·Δt oscillation between updates (re-synced each
  update). It is the designed trade-off of prediction (immediacy) vs 20 Hz
  authority, not divergence.
- **Afterburner power** in the prediction is refreshed from the last update's
  `Power`; there is no local power ramp in this stage (the server's P412 ramp is
  authoritative). P447b may refine this.
- **Weapon / projectile origin** prediction (firing from the predicted pose) is a
  follow-up, out of scope here. Own-shot echoes currently read the predicted
  pose seam but the ballistics resolution is unchanged.

## 6. Verification

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors**.
- New tests: `OwnShipPredictorTests` (5) — control-change-same-step vs raw-RTT,
  steady-state bound, disturbance blend-back + no-oscillation, over-20 m snap,
  freeze/resume. All 5 pass **3 consecutive runs** (deterministic, no flake).
  The control-change test records the must-fail against the raw baseline (§4.1).
- Full `Allegiance.ClientCore.Tests` suite: 343 passed; 2 failures
  (`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`) — both **verified to
  fail identically on the pristine tree** (`git stash -u`), i.e. the documented
  pre-existing live-wire timing flakes from P448a/P446, not P447a regressions.
  Neither references `OwnShipPredictor`.
- Full `Allegiance.Core.Tests` suite: 3609 passed; 10 failures — every one a
  documented pre-existing live-wire timing flake from the P446 ledger
  (`ConquestTerritoryLiveTests.Flag_capture… / .Flags_disabled… /
  .Conquest_win_in_one_mission…`,
  `GameOverPlayersWireTests.Conquest_win_in_one_mission… /
  .Conquest_win_lists_every_connected_player…`,
  `LiveTurretGunnerBindingTests.Board_binds_gunner…`,
  `LiveRipcordTests.First_activation…`,
  `LiveMoneyTests.Board_refund…`,
  `LiveChatSectorWireTests.Individual_AllSector…`,
  `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`). P447a adds
  nothing to Core (module and tests are ClientCore-only), so Core is untouched;
  these flakes are environmental, not a P447a regression.
- Other suites clean: `Allegiance.ClientCore.MonoGame.Tests` 1012 passed,
  `Allegiance.Lobby.Tests` 12 passed.

## 7. Out of scope / follow-ups

- Wiring into `Game1` rendering, the camera and `_poses[me]` (P447b).
- Sending client poses / changing server authority — the `FM_C_SHIP_UPDATE`
  payload stays controls-only.
- Weapon / projectile origin prediction from the predicted pose.
- Remote ships (P448b — already landed separately).
- P438–P440, P445, P449.
