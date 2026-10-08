# P580 — In-Flight Cinematic Cameras

Status: IMPLEMENTED. Branch: `master` (committed, not pushed). Replaces the
P565 note that the in-flight cinematic camera (brief item 8) was not implemented
(`docs/reports/P565_*.md:54,118`).

## Summary

Ported native's seven missing external camera modes for live play with native
placement maths and native default keys; added a clean non-native "director"
b-roll mode (opt-in, `--director` / F2) with the HUD hidden for recording, plus
spectator follow (F3 / `--follow`). Command views, the minimap/starmap and
replays are out of scope as specified.

## Native camera modes ported (CameraMode.cs + Rendering/ExternalCameras.cs)

Each placement is a pure, testable function in `Rendering/ExternalCameras.cs`;
every rule cites its native source.

| Mode | Native token / key | Placement source (WinTrek.cpp) |
|------|--------------------|-------------------------------|
| ExternalFlyBy | `cmExternalFlyBy` / F5 | midpoint aim + 1.25·max(dTarget,dMe), `dSide·3+dBackward` (:7137-7164) |
| ExternalFlyByTarget | `cmExternalFlyByTarget` | no native `case` → generic external (:7315-7320 default) |
| ExternalOrbit | `cmExternalOrbit` / F4 | free camera Pitch(.35)/Yaw(.2)/Roll(.5)·dt, offset 5+2.5·Length (:7187-7196) |
| ExternalTarget | `cmExternalTarget` / F7 | offset 5+2·targetRadius, backward of free camera (:7099-7111) |
| ExternalBase | `cmExternalBase` / F10 | no native `case` → generic external; port frames home base (:7315-7320) |
| ExternalStation | `cmExternalStation` / F11 | TrackCamera at 5+2.5·shipRadius (:7209-7227) |
| ExternalOverride | `cmExternalOverride` | TrackCamera at shipRadius·3 (:7199-7206) |

Helpers transcribed with citations: `TrackCamera` (:6848-6878, including the
`dSide = sqrt(LengthSquared + dBackward²)` fudge in the fly-by distance, kept
verbatim because the brief demands native fidelity), the per-mode F-key toggles
each returning to cockpit (`SetViewToCombatMode((GetCameraMode()!=X)?X:Cockpit)`,
:9796-9868), and the free external camera (`m_orientationExternalCamera`).

The default F-key bindings are the classic Allegiance F-row (F4 external/orbit,
F5 flyby, F7 target, F10 base, F11 station). The native keymap is a
data-driven `keymap.csv` shipped with the game (not present in this source tree;
the command names `CommandViewFlyby`/`CommandViewTarget`/etc. are registered in
`trekinput.cpp:191-197,268-269`). This port uses the documented default F-row
layout.

Own-ship culling per mode: native draws the hull in every external mode and
hides it only in the cockpit (`SetVisibleF(!(InternalCamera && …))`,
:7336-7340). The existing port gate `_cameraMode != Cockpit` already matches, so
the own ship renders in all new external modes and is hidden only in cockpit —
no per-mode change was needed, verified against the culling code paths in
`Game1.Rendering.cs`/`Game1.FlightInput.cs`.

Wiring: the Draw camera switch and the T/G targeting pick both resolve the
active view through the shared `ComputeFlightCamera` seam (Game1.Director.cs),
so targeting projects through the real active camera. The `V` cycle now includes
the seven modes (CameraCycler.cs); `ExternalOverride` is intentionally NOT in
the cycle (it is a scripted, time-boxed camera, native :6480-6534, :7673-7678).

## Director b-roll mode (non-native, opt-in)

`Rendering/Director.cs` — a pure, seeded editorial controller + pose rig:

- Cycles the native modes and the P565 fly-by path (`Chase → Orbit → FlyBy →
  Target → Pass → …`).
- Chooses subjects from live world signals: nearest explosion, engagement
  (opposing ships within ~1500u), the followed ship, aleph transits, stations
  — scored by interest, picked by seeded weighted random (variety).
- Dwell 4-8 s per shot; smooth convergence (exponential, `transitionTau`), no
  snapping.
- Survives warps/docking: a sector/dock change forces an immediate
  re-acquisition; while docked it films the station.
- Seeded: `--director-seed N` / `ALLEGIANCE_DIRECTOR_SEED` reproduces a
  recording shot-for-shot; `Director.Reseed` restarts deterministically.
- "Any flight input returns control instantly": the pure predicate
  `Director.AnyFlightControl(...)` drives `CancelDirectorOnInput` — WASD/QE/
  arrows hand the camera back to chase immediately.

Keys / flags: F2 toggles the director, F3 toggles spectator follow of the
nearest bot; `--director`, `--director-seed N`, `--follow <id>` arm at launch.
HUD is suppressed by reusing P572's `--hide-hud` (no new HUD code).

## Spectator follow

While docked/ghost or via F3/`--follow`, the director centres on the chosen ship
and films battles around it without flying.

## Files changed

- src/Allegiance.ClientCore.Game/Rendering/CameraMode.cs (7 new modes)
- src/Allegiance.ClientCore.Game/Rendering/CameraCycler.cs (extended V cycle)
- src/Allegiance.ClientCore.Game/Rendering/ExternalCameras.cs (NEW, native maths)
- src/Allegiance.ClientCore.Game/Rendering/Director.cs (NEW, director + input rule)
- src/Allegiance.ClientCore.Game/Game1.Director.cs (NEW, game glue)
- src/Allegiance.ClientCore.Game/Game1.Rendering.cs (Draw → ComputeFlightCamera)
- src/Allegiance.ClientCore.Game/Game1.Targeting.cs (pick through active camera)
- src/Allegiance.ClientCore.Game/Game1.FlightInput.cs (F-keys, director/spectator, cancel)
- src/Allegiance.ClientCore.Game/Game1.cs (fields, constructor wiring)
- src/Allegiance.ClientCore.Game/LiveProof/AutoProofPlan.cs (--director/--follow/--seed, --camera modes)
- tests/Allegiance.ClientCore.MonoGame.Tests/ExternalCamerasTests.cs (NEW)
- tests/Allegiance.ClientCore.MonoGame.Tests/DirectorTests.cs (NEW)
- tests/Allegiance.ClientCore.MonoGame.Tests/CameraCyclerTests.cs (extended)

## Tests

- ExternalCamerasTests: FlyBy midpoint/offset (exact value 187.5), FlyBy eye/
  look, Target offset clamp (5+2r), OrbitOffset (5+2.5L), TrackCamera eye
  (6861-6878), Station/Override lengths, orbit rotation rates stay unit.
- DirectorTests: finite pose, dwell 4-8 s with shot cycling, subject choice
  favours interest with variety, sector-change re-acquisition, docked films the
  station, same-seed determinism, Reseed reproducibility, AnyFlightControl,
  no-snap smoothing.
- CameraCyclerTests: full 10-mode (gunning) / 9-mode (not gunning) cycle,
  turret skip, restart-on-stale, Override excluded from the V cycle.

Whole-solution build: 0 warnings, 0 errors.

## Proof (real-window, P572 tooling + MCP showcase bots)

Captured with `scripts/live-proof.sh` (boots a local `Allegiance.Server` on the
m2xspiderw map, spawns a 5v5 via the MCP `spawn_scenario`, launches a real
2560×1440 windowed client, and saves REAL backbuffer PNGs — not offscreen
renders). All artifacts in `docs/reports/P580_assets/`.

- **Director b-roll, 60 s capture (HUD hidden, seed 581):** 10 real 2560×1440
  frames over ~56 s — `proof_t004 … t056` — assembled into
  `p580_director_60s.mp4` (short H.264 clip) and a contact-strip
  `p580_director_strip.png`. The frames show cinematic external shots that
  change between subjects (the director was verified armed:
  `[monogame] director armed at launch seed=580`); `vision_analyze` confirmed the
  frames are **completely clean of UI** (no minimap, no HUD, no message log).
- **Native modes, real window (each 2 shots):**
  `P580_mode-orbit.png` (own ship filmed externally from the orbiting camera),
  `P580_mode-target.png` (external shot framing the aleph target),
  `P580_mode-flyby.png`, `P580_mode-flybytarget.png`, `P580_mode-base.png`,
  `P580_mode-station.png`.
- Each native-mode run exited clean (client ran the requested `--camera <mode>`
  the whole flight; `camera=target` etc. confirmed in the client log).

Honest limits: bots spawn in Alderan/Plato while the player's ship is in Omega,
so the sparse-sector frames sometimes frame the target (a distant warp/station)
as a small point, and no single still caught a full 5v5 dogfight with multiple
ships. The exact per-mode PLACEMENT MATH is what the unit tests pin down
(ExternalCamerasTests); the screenshots demonstrate each mode rendering live in
the real window.

## Suite (scripts/run-tests.sh, 3 consecutive runs on the final code)

Build: 0 warnings, 0 errors on every run. Sequential per-assembly results below
(the three runs below are on the FINAL committed code, re-run after the small
P580 director-HUD-suppression gate was added; an earlier 3-run pass on the code
before that gate showed the identical all-documented-load-flake pattern).
Any failure is a pre-existing documented load-flake in `docs/TEST_FLAKES.md` —
none involve the P580 camera/director code.

- **Run 1:** Lobby 12/12 · MonoGame 1584/1584 · ClientCore 544/545 ·
  Core 3771/3773. 3 failures, all documented load-flakes:
  `ShipDeleteGoneLiveTests…WatcherLatchesGone` (TF:24/46 family),
  `LiveStationRescueTests…` (TF:156), `LoadoutChangeLiveTests…` (TF:185).
- **Run 2:** Lobby 12/12 · MonoGame 1584/1584 · ClientCore 544/545 ·
  Core 3770/3773. 4 failures, documented load-flakes:
  `ClientWarpApproachSlowHullLiveTests…` (TF:215), `AutoReloadOnEmptyTests…` (TF:52),
  `CargoBayReloadTests…` (TF:53), `LiveChatSectorWireTests…` (TF:216).
- **Run 3:** Lobby 12/12 · MonoGame 1584/1584 · ClientCore 545/545 (clean) ·
  Core 3769/3773. 4 failures, documented load-flakes:
  `AutoReloadOnEmptyTests…` (TF:52), `GameOverPlayersWireTests…does_not_leak…` (TF:30),
  `LiveStationRescueTests…` (TF:156), `MineCrossSideVisibilityLiveTests…` (TF:184).

The MonoGame test assembly — which contains every P580 test (ExternalCameras,
Director, CameraCycler) — passed **1584/1584 on all three runs**. None of the
three runs' failures touch the camera/director/rendering code; each is a
pre-existing load-sensitive live-server test already in `docs/TEST_FLAKES.md`.

## Notes / deviations from native (honest)

- `cmExternalFlyByTarget` and `cmExternalBase` have no dedicated native `case`
  (they fall to the generic external branch); the port reproduces that, framing
  the current target / home base respectively with the generic external view.
- The free external camera has no mouse-look in this port (the mouse is the
  virtual joystick), so it starts aligned to the ship's backward at mode entry
  and is spun by orbit/director, instead of being user-pivoted.
- The director's "engagement" is approximated as opposing ships within ~1500u
  (the wire carries no per-pair firing state).
