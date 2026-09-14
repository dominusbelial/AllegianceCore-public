# P207 — Allegiance.ClientCore.MonoGame: a playable MonoGame POC client

## Summary

`src/Allegiance.ClientCore.MonoGame/` is the first real, playable graphical client
for the C# Allegiance port. It connects to a real `Allegiance.Server` over the real
`ConnectionSession` wire path, renders the local ship (and, in principle, teammates)
with the real Bios Scout model from a chase camera, flies via the keyboard, and
smooths remote ship motion through a `NetworkPrediction`-derived
`RemoteShipPredictor`. P207 stood up the whole pipeline and confirmed single-ship
rendering end-to-end against a live server; P208 (this cleanup task) verified the
two-peer case, exercised the predictor over a real run, wrote this report, and ran
the final regression pass.

## Architecture — what connects to what

- `src/Allegiance.Server/Server.cs` hosts real missions. Ships live in a `Sector`,
  ticked by `Mission.Update` at `UpdatesPerSecond` (default 20/s); movement is
  `Ship.OnUpdate → ExecuteMove` (`Ship.cs:4897/2890`).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` is the client's TCP
  transport speaking the real wire messages: logon + `SET_CLUSTER(7)` assignment,
  `SINGLE_SHIP_UPDATE(78)` positions, `SHIP_UPDATE(24)`/`CommandControls` input,
  `PLAYER_READY(45)`, `FM_C_DOCKED(48)` undock.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` is the pure domain model built
  only from inbound wire: `Ships` (`Dictionary<short, ShipEntity>`),
  `MyShipId`, `Mission`. Each `ShipEntity.Latest` holds the newest
  `ServerShipUpdate` (position/velocity/orientation); `ShipEntity.LastUpdateTick`
  is the server tick it came from.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` ties it together each frame:
  samples the keyboard → `FlightInput.Translate` (`Flight/FlightInput.cs`) →
  `_session.CommandControls`; for every remote (non-self) ship feeds each new
  `LastUpdateTick` into a per-id `RemoteShipPredictor` (`Flight/RemoteShipPredictor.cs`),
  then renders the own ship at its raw last-known position and every remote at its
  **predicted** pose.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ChaseCamera.cs` builds the
  camera-relative view/projection; `Rendering/PoseConverter.cs` converts sim
  `Vector3`/`Orientation` ↔ MonoGame types (camera-relative `ToXnaWorldRelative`);
  `Rendering/ShipRenderer.cs` draws the Bios Scout `Model` once per visible ship.

## Two real bugs found and fixed (P207, commit `814570f`) — not re-derived here

### 1. Uniformly black frame at real (large) sim world coordinates
This machine's GPU/driver produced a blank frame whenever the view/World matrices
were built from the sim's true absolute world position (routinely hundreds to
thousands of units), even though every matrix was independently verified valid.
Fixed with **camera-relative ("floating origin") rendering**:
`ChaseCamera.Matrices(...)` always builds the view looking from `Vector3.Zero` and
returns the true world-space eye via an `out Vector3 cameraEye`; every drawn
object's World matrix is built from `PoseConverter.ToXnaWorldRelative(xnaPosition,
orientation, cameraEye)` (position **minus** `cameraEye`), never the absolute
`PoseConverter.ToXnaWorld`. Both methods' XML remarks carry the full reasoning.
The relative geometry between camera and objects — the only thing that shapes the
image — is unchanged, but every value reaching the vertex shader stays small.

### 2. Extreme, disorienting close-up even after fix #1
The Blender/FBX export bakes a real, non-identity `mesh.ParentBone.Transform` (~100x
scale + 90° axis swap; confirmed via a diagnostic print of the matrix). `ShipRenderer.Draw`
had combined it in the wrong order (`world * mesh.ParentBone.Transform`), which in
MonoGame's row-vector convention applied the object's translation *before* the 100x
scale/rotate, corrupting any non-origin placement. Fixed to
`mesh.ParentBone.Transform * world` (bone transform first, then world placement — the
standard MonoGame pattern), and `Content/Content.mgcb`'s `/processorParam:Scale`
dropped from 20 to **1** so the baked bone transform alone supplies the ship's real
size (no double-counting). `ChaseCamera` in `Game1.LoadContent` is tuned to
`distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: 3000f` for this
real scale. Verified via a real screenshot showing the full, correctly-framed ship.

Both fixes are real, hard-won session results; this task deliberately does not
touch them.

## Two-peer verification (P208)

### Setup
Because the server's teammate position relay (`BroadcastShipStates`, same-side
`SINGLE_SHIP_UPDATE` only — `Server.cs:833`) is **same-side**, both peers had to be
on the same side for the client to see the bot move. A temporary 1-team config
(`server.p208.json`, not committed) put both peers on side `Alpha`. Stack:
fresh `Allegiance.Server` (port 6090) + `Allegiance.Bot` (a real second-peer client,
`src/Allegiance.Bot/Program.cs`) + the MonoGame client, launched in that order so
the client anchors the chase camera. The mission crossed `NotStarted → Starting →
Started` through the real 15s `BriefingCountdownSeconds` countdown
(`TryAutoStart`/`StartCountdown`/`AdvanceStartCountdowns`), and the server's
`[ctrl]`/`[start]` log lines confirmed the started transition and per-tick control
acceptance. The bot lacked a ready-up, so `Allegiance.Bot` now sends
`SendPlayerReady(true, shipId)` after ship assignment — the one source change this
task keeps (a bot that flies should participate in a live mission rather than pin it
in `NotStarted`).

### Methodology
Screenshots were captured to an **offscreen `RenderTarget2D`** (never the on-screen
backbuffer — `GetBackBufferData()` is confirmed unreliable/black on this Wayland
box), persisted via `Texture2D.SaveAsPng`, then **read back with vision** (not
inferred from console logs). Two peers cruising forward together produced a stable,
in-frame composition in the chase camera.

### What was seen
The client's own (Bios Scout) ship renders centered in the chase camera. A second,
distinct Bios Scout — the **bot's predicted position** — renders beside it:
offscreen-captured frames show **two spacecraft flying in formation** (one center-
left, one right), confirming the remote ship's real position flowed through
`ClientWorld.Ships` and `RemoteShipPredictor` into the render path.

### RemoteShipPredictor is genuinely exercised
Per-second counters logged over the run: `predicts=27902 observes=4575
extrapolatedFrames=23327`. Both halves of the predictor's contract occur over a real
run: `observe` (feeding a fresh server observation when `LastUpdateTick` changes)
and `predict` (extrapolating between observations). The bot's real velocity ramped
`(0,0,0) → (0,0,-41.6) → (0,0,-60)` as it accelerated to cruise, confirming real
velocity data reaches the predictor.

### Honest limitation found by this end-to-end run
At the server's native 20 Hz cadence the `RemoteShipPredictor` does **not**
stabilise on a live peer. Root cause: the correction blend completes only when
`k = 1 − e^(−(t−t₀)/interval) ≥ 0.995`, i.e. after ≈ 5.3 expected intervals (0.26 s
at `interval = 1/20`), but fresh observations arrive every 1 interval (0.05 s), so a
new observation restarts the blend before it ever completes. Because
`Observe` only writes `_anchorVel` on the first observation (and `Predict` only
adopts the live velocity when a blend completes), the extrapolation anchor velocity
gets stuck at the peer's initial ≈0. Consequences observed live: the predicted
remote position lags the real one by hundreds-to-thousands of units and can
oscillate wildly per frame (e.g. predicted z stepping `−213 / 5522 / 2146` between
consecutive frames). Early after `Started` — while the peer is still near spawn and
the divergence is small — the predicted position is sensible and both ships are
cleanly in frame (the two-ship capture above). Later, once the peer pulls far ahead,
the predicted position diverges out of the chase camera's forward view. This is a
genuine finding from verifying the POC end-to-end, not something rewritten here
(the predictor's algorithm and its unit tests are untouched and still pass); it is
the clearest candidate for a future hardening pass.

## Explicitly deferred (out of scope, not bugs)

Combat/VFX, skybox, gamepad input, camera toggle, dock/loadout UI, audio, and
per-hull/per-civ model selection were always outside the POC's scope and remain so.

## Verification checklist

- [x] Fresh server + Bot + MonoGame client, same side, real run; mission reached `Started`.
- [x] Saved offscreen `RenderTarget2D` screenshots read back and visually confirmed
      to show the local ship **and** the real predicted position of the bot's ship
      (two distinct Bios Scouts in the chase camera).
- [x] Log evidence that `RemoteShipPredictor` both extrapolates and corrects against
      real server updates during the run.
- [x] This doc written.
- [x] `dotnet build AllegianceCore.slnx` clean (0 warnings/errors).
- [x] `dotnet test` ×3; `Allegiance.ClientCore.MonoGame.Tests` (24) clean each run.
      Pre-existing unrelated flakiness (LiveWinConditionTests / LiveCombatWireTests)
      noted, not chased.

## Files changed (P208 commit)

- `src/Allegiance.Bot/Program.cs` — console bot now sends `PLAYER_READY` after ship
  assignment so it can participate in a real mission (all other P208 scaffolding,
  including the temporary 1-team config, temporary offscreen-capture/diagnostic code
  in `Game1.cs`, and temporary spawn/flight tweaks for the verification run, was
  removed before commit).
- `docs/CLIENTCORE_MONOGAME_POC_P207.md` — this report.
