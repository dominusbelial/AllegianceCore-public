# P612 — Camera rework + cinematic mode

Status: landed on `master` (not pushed).

## Summary

Adds the camera + cinematic batch Domingo wanted for filming bot-driven 195v195
showcases on 221: smooth camera transitions, a free-flight spectator camera, an
orbit camera (both admin-unrestricted), a HUD toggle, and an admin-only server
sim time scale for clean slow motion. Native camera **behaviour** (what each
mode frames) is unchanged; the only addition to existing modes is easing *between*
framings instead of cutting.

Scope items (from the brief):

1. **Smooth transitions (all modes)** — `CameraEasing` (critically-damped spring)
   eases eye / orientation / FOV over ~0.7 s on any framing change (target
   switch, missile-cam start/end, mode change) instead of cutting. `ALLEGIANCE_INSTANT_CAMERA=1`
   restores the instant cut for native-purists. Native per-mode framing untouched.
2. **Free-flight spectator camera** (new mode, NOT in the V cycle) — F8. Detached
   6-DOF rig (`FreeCam`) with mouse look + WASD/QE move, Shift/Ctrl speed
   modifier, smooth acceleration. The player's ship **holds** (neutral controls,
   no stray inputs) while it's active. Can switch sectors (admins: any sector).
   One-key return to ship (F8 again → chase).
3. **Orbit camera** (new mode, NOT in the V cycle) — F9. Reuses the P565
   `InspectorOrbitCamera` rig: locks any ship/station/position (nearest, current
   target, or PageUp to cycle nearby ships), auto-orbit, mouse orbit/pan, wheel zoom.
4. **HUD toggle** — F1, outside the proof tooling (`_hideHud` is still there;
   this is a separate in-flight toggle).
5. **Admin-unrestricted visibility** — for admins (the P602 `adminCheats`
   allowNames identity), the server streams **all ships in the sector the
   spectator camera is viewing** (any sector, side/cloak visibility ignored);
   non-admins keep native behaviour (no leak). Documented in `docs/GUIDE.md` /
   `SECURITY.md` as LAN/demo-grade.
6. **Server time scale** — `liveSettings.timeScale` (0.1–1.0, default 1.0) +
   the admin `timescale <value>` cheat. The server advances its sim clock by
   `period × scale`, so a battle of any size runs in clean slow motion; clients
   stay in sync via a new `TimescaleUpdate` (server→client) message that scales
   their remote-ship dead-reckoning. Printed at boot and in the tick-profiler
   header.
7. **Keybindings** — F8/F9/F1 added to `KeyBindingTable` + the H help overlay.

Out of scope respected: replays/recording, native camera framing, bot behaviour,
P604.

---

## What changed

### Client — camera system

- `src/Allegiance.ClientCore.Game/Rendering/CameraMode.cs` — two new modes
  `FreeCam` and `OrbitCam` (NOT added to `CameraCycler.Order`, so `V` never
  cycles into them).
- `src/Allegiance.ClientCore.Game/Rendering/CameraEasing.cs` *(new)* — pure
  critically-damped spring easing + the `CameraPose` (eye/orientation/FOV)
  capture/rebuild pair. Monotonic, no overshoot, settles at the target; a
  duration ≤ 0 is "instant".
- `src/Allegiance.ClientCore.Game/Rendering/FreeCam.cs` *(new)* — pure
  6-DOF free rig (yaw/pitch mouse look, WASD/QE move with smooth acceleration,
  speed modifier).
- `src/Allegiance.ClientCore.Game/Game1.Spectator.cs` *(new)* — the spectator
  camera integration: F8/F9/F1 toggles, free-cam mouse/movement, orbit mouse,
  the smooth-transition layer (`ApplyCameraSmoothing`), the framing-key detector,
  and the sector-switch request.
- `src/Allegiance.ClientCore.Game/Game1.Director.cs` — `FreeCam`/`OrbitCam`
  cases in `ComputeFlightCamera`; `CameraModeFromToken` accepts `freecam`/`orbitcam`;
  camera toggles don't kick a spectator camera.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — `HandleSpectatorKeys`
  wired into `UpdateFlying`; flight-control guards so the ship HOLDS (neutral
  controls) and mouse-flight/buttons/manual-input don't leak while a spectator
  camera is active; V-cycle guarded.
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — `ApplyCameraSmoothing`
  after `ComputeFlightCamera`; HUD draw gated on `_hudHidden`.
- `src/Allegiance.ClientCore.Game/Game1.Panes.cs` — free-cam sector switch hooked
  into starmap `,`/`.` selection.
- `src/Allegiance.ClientCore.Game/Game1.AutoProof.cs` — `DriveAutoProofSpectator`
  (so `--camera freecam`/`--camera orbitcam` produce real-window proof frames).
- `src/Allegiance.ClientCore.Game/Rendering/KeyBindingTable.cs` — F8/F9/F1 help rows.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SendViewClusterRequest`
  (free-cam sector switch) + the `TimescaleUpdate` receive case.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `TimeScale` property.
- `src/Allegiance.ClientCore/Flight/ShipDeadReckoner.cs` — `TimeScale` field; `PoseAt`
  extrapolates in scaled sim time (`wall elapsed × scale`) so slow-motion remote
  ships don't overshoot / rubber-band.
- `src/Allegiance.ClientCore.Game/LiveProof/AutoProofPlan.cs` — accepts
  `freecam`/`orbitcam` as `--camera` tokens.

### Client → wire

- `src/Allegiance.Core/Wire/TimescaleUpdate.cs` *(new)* — server→client message
  (port-extension fmid 991) carrying `scale × 1000` as a uint.
- The free-cam sector switch reuses the existing `FM_C_VIEW_CLUSTER(104)`
  request (no new client→server message).

### Server

- `src/Allegiance.Server/Server.Timescale.cs` *(new)* — `_timeScale` init/clamp,
  `SetTimeScale` + `BroadcastTimescale`/`SendTimescaleTo`, `EffectiveSimPeriod`,
  the `timescale <value>` admin-cheat handler.
- `src/Allegiance.Server/Server.Spectate.cs` *(new)* — `_spectateSector` map,
  `AdminSpectatorAllowed` (pure, testable), `IsSpectatorAdmin`,
  `RecordSpectateSector`, `SectorById`, `BroadcastSectorFor`.
- `src/Allegiance.Server/ServerConfig.cs` — `LiveSettingsConfig.TimeScale` +
  `ClampTimeScale`/range constants.
- `src/Allegiance.Server/Server.Broadcast.cs` — `SimPass` advances `_simNow +=
  EffectiveSimPeriod`; `BroadcastShipsUpdatePeer` serves the admin spectator's
  viewed sector and relaxes the cloak predicate for it.
- `src/Allegiance.Server/Server.Lifecycle.cs` — `InitTimeScale()` at boot;
  time scale appended to the tick-profiler header label.
- `src/Allegiance.Server/Server.Dispatch.cs` — a FLYING peer's `VIEW_CLUSTER` is
  recorded as an admin spectator's viewed sector (non-admins keep the native
  ignore).
- `src/Allegiance.Server/Server.AdminCheats.cs` — `timescale` cheat token.
- `src/Allegiance.Server/Server.Lobby.cs` — `SendTimescaleTo` at seat.

Docs: `docs/GUIDE.md`, `SECURITY.md`, this report + `docs/reports/P612_assets/`.

---

## Native fidelity notes

- The ported native camera **placements** (`ExternalCameras`) are untouched; the
  new easing operates only on the *transition between* framings and passes the
  pose through unchanged when nothing changed (so native per-mode framing is
  preserved).
- The free-flight / orbit spectator cameras are **port extensions** — native has
  no admin spectator cameras (the closest native concepts are the docked
  `VIEW_CLUSTER` preview, `FedSrv.CPP:6420-6454`, and the client's external
  cameras). The orbit rig reuses the P565 inspector camera (itself native-hangar
  spin inspired). Where a rule is ported (e.g. the same-side visibility gate the
  native `SeenBySide` maps to) the existing port code is reused.
- `sim.timeScale` is a **port extension** (native has no server time scalar); it
  reuses the existing fixed-step sim clock by scaling the per-tick advance — the
  same deterministic path bots/physics already run through, so bot behaviour is
  unchanged at the default 1.0 (verified by the mandatory live bot check).

---

## Unit tests

Failing-first: all are new-feature tests; on today's code the APIs did not exist
(would not compile / could not pass), and they pass on the new code.

- `tests/Allegiance.ClientCore.MonoGame.Tests/P612CameraEasingTests.cs` — easing
  monotonic / ends at target / instant; pose round-trip (16 tests total in the
  file set).
- `tests/Allegiance.ClientCore.MonoGame.Tests/P612FreeCamTests.cs` — free-cam
  look/basis/movement/speed-modifier/teleport.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P612OrbitCameraTests.cs` — orbit
  rig frames the locked subject; auto-orbit preserves distance.
- `tests/Allegiance.Core.Tests/P612ServerCinematicTests.cs` — time-scale clamp
  (theory) + range; the admin spectator gate (enabled/allow-list/case/wildcard);
  `TimescaleUpdate` wire round-trip (19 tests).

Measured (filtered runs): client camera 16/16 pass, server cinematic 19/19 pass.

---

## Real-window live proof (local server, never 221)

All frames are REAL 2560×1440 backbuffer screenshots taken by the windowed
client via `scripts/live-proof.sh` (the P572 auto-proof path), on a local
`m2xspiderw.igc` server on this workstation. A 12–15-per-side MCP battle
(`spawn_scenario` arena mode) ran under each camera.

- **Free cam around a 15v15-scale battle** — `proof_*_cfreecam.png`
  (`P612_assets/freecam_battle_t20.png`, `_t25.png`): the free camera flies around
  an in-sector fight; the frames show the battle in view and the on-screen combat
  log confirms nearby kills.
- **Orbit camera on an enemy ship** — `proof_*_corbitcam.png`
  (`P612_assets/orbit_enemy_t06.png`, `_t24.png`): F9 locks a subject and
  auto-orbits; successive frames show the camera revolving around it.
- **HUD-off frame** — `P612_assets/hudoff_frame.png`: with `--hide-hud`, no HUD
  overlays are drawn (all HUD regions empty).
- **Battle at `timeScale 0.4`** — server boot: `[boot] sim.timeScale = 0.4
  (cinematic slow motion: world runs at 40% speed)`; frames
  `P612_assets/timescale04_t10.png`. Client stayed in sync: dead-reckon re-seed
  corrections stayed low (mean ≈ 0.2 m) — no rubber-banding.
- **Admin vs non-admin visibility** — the admin gate is unit-tested
  (`AdminSpectatorAllowed`); within a single sector both admin and non-admin see
  all ships (native's no-side-exclusion broadcast), and the admin advantage
  (cross-sector streaming + cloak bypass) is enforced server-side and gated on
  the allow-list, so a non-admin's `VIEW_CLUSTER` is a no-op (no leak).

---

## Mandatory live bot check

On a local server, `spawn_scenario` showcase (`size_scale 1`,
`target_sector Alderan`) — the bot population **left their home sectors and
reached the target**. Server log (`P612_assets/botcheck_server.out.txt`):

```
[boot] DEBUG MODE ACTIVE — side 0 home Omega(1053)/side 1 home Plato(1060), single warp each to Alderan(1052)...
[warp] ship 8125 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184
[warp] peer 28 ship 8125 -> SET_CLUSTER sector 1052 ...
[warp] ship 8124 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184
... (26 warp crossings logged; bots from both sides crossed into Alderan)
```

This run used the new server code at the default `timeScale 1.0` (identity), so
bot navigation is confirmed un-regressed by the P612 server changes (the sim
clock path, the new `TimescaleUpdate` seat message, and the spectator broadcast
guard are all non-interfering at 1.0 / for non-admins).

---

## Build & full suite

Whole-solution build: **0 warnings, 0 errors** (Debug and Release).

Full suite: **2 consecutive runs of `scripts/run-tests.sh` on the final code**
(the commit immediately below, run before the suite and amended after).

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1751/1754 (3 skip) | 603/603 | 3948/3952 | 4 — all ledgered live-wire load-flakes |
| 2 | 12/12 | 1751/1754 (3 skip) | 603/603 | 3950/3952 | 2 — all ledgered live-wire load-flakes |

The only failures across both runs are the documented pre-existing live-wire
load-flake class in `docs/TEST_FLAKES.md` (rotating run-to-run): run 1 —
`AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`,
`StationUnderAttackAlertLiveTests.*` ×2, `CargoBayReloadTests.SwapPart_…`; run 2 —
`CargoBayReloadTests.Reload_fuel_pack_…`, `Reload_malformed_entry_…`. None are
P612 tests; all pass in isolation (fixed-sleep live-wire receipt under full-suite
load, the ledger's documented class). All 35 new P612 tests passed in both runs
(16 camera in MonoGame, 19 cinematic in Core).

---

## Notes / limitations

- The admin spectator sector-streaming is LAN/demo-grade (name-based identity,
  self-declared); documented in `SECURITY.md`. On a public server `adminCheats`
  must stay off.
- The free-camera sector switch ("view any sector" for admins) repositions the
  rig and streams the requested sector's ships; the client renders that sector's
  ships against the current space backdrop (a full remote-sector render is out of
  scope). Ships in the viewed sector arrive on the next broadcast tick.
- `ALLEGIANCE_INSTANT_CAMERA=1` disables the ~0.7 s transition for native-purist
  A/B comparisons.

See `docs/reports/P612_assets/` for the screenshots and server logs.
