# P580 — in-flight cinematic cameras: port native's external camera modes (fly-by, fly-by-target, orbit, target, base, station, override) for live play, plus a clean "director" b-roll mode with the HUD hidden for recording

## Why this task exists

Domingo records showcase videos (OBS, 2560×1440) and asked for an in-flight
cinematic camera. **P565 did not implement it** (`docs/reports/P565_*.md:54,118`,
"in-flight cinematic camera (brief item 8) not implemented"). It built the
offline inspector's fly-by (`Rendering/InspectorFlyBy.cs`,
`InspectorOrbitCamera.cs`).

**Native already has most of this.** `WinTrek/Treki.h:108-126` defines 14
camera modes: `cmCockpit`, `cmExternal`, `cmExternalChase`,
`cmExternalTarget`, `cmExternalBase`, `cmExternalMissile`, `cmExternalFlyBy`,
`cmExternalFlyByTarget`, `cmExternalOrbit`, `cmExternalStation`,
`cmExternalOverride`, `cmExternalTurret`, plus two command views. The port
(`Rendering/CameraMode.cs`) has Chase, Cockpit, ExternalTurret and the missile
camera; its doc lists **the ten missing ones** (Target, Base, Orbit, Station,
FlyBy, FlyByTarget, Override, …).

Native implementation: the per-mode camera placement switch in
`WinTrek/WinTrek.cpp:~7099-7215` (`case cmExternalTarget` :7099, `case
cmExternalFlyBy` :7137: aim at the midpoint of you and the target, back off
along the camera's backward axis by `1.25 × max(dTarget, dMe)` with `dSide·3 +
dBackward`, falling back to chase without a target; `cmExternalOrbit` :7187;
`cmExternalOverride` :7199 with `m_timeOverrideStop` :7673, also used by P578's
eject swing to the killer; `cmExternalStation` :7209), and the key toggles
`WinTrek.cpp:~9798-9842` (`TK_ViewExternalOrbit`, `TK_ViewExternalStation`,
`TK_ViewFlyby`, `TK_ViewMissile`, `TK_ViewTarget`, `TK_ViewChase`, …, each
toggling back to cockpit). Find the native default key bindings for these TK_
ids (the input map in `trekinput.cpp`/the keymap file) and use them.

## Scope

1. **Port the missing native external modes** with native placement maths
   and native keys: FlyBy, FlyByTarget, Orbit, Target, Base, Station,
   Override (the override the eject camera of P578 needs: if P578 already ported
   it, reuse it). Each is a pure, testable placement function plus wiring into
   `CameraCycler`/`CameraMode`, the targeting and HUD code paths that check
   `_cameraMode` (e.g. `Game1.Targeting.cs:91`, `Game1.FlightInput.cs:~1651`),
   and the culling of your own ship per mode.
2. **"Director" b-roll mode (non-native, opt-in, a key + `--director` flag):**
   for recording. It hides the HUD (reuse P572 `--hide-hud`) and cycles
   automatically between native modes and the P565 fly-by path, choosing
   **interesting subjects**: the nearest explosion or engagement, the ship you
   follow, an aleph transit, a station. Smooth transitions (no snapping), dwell
   times of ~4–8 s, it **survives warps and docking** (re-acquires a subject),
   and any flight input returns control instantly. Seeded, so a recording can
   be repeated.
3. **Spectator follow:** while docked/ghost or via a key, follow a chosen ship
   (e.g. a bot in the showcase) with the director, to film battles without
   flying.

## Out of scope

Command views (cmExternalCommandView34/TD), the minimap/starmap, replays.

## Proof (real window, P572 tooling + MCP showcase bots)

- Real-window screenshots of each native mode (FlyBy, FlyByTarget, Orbit, Target,
  Base, Station) in a 5v5 fight, and a 60 s **director** capture (frame strip +
  a short video if cheap: ffmpeg is available) showing subject changes, an
  aleph transit and an explosion, HUD hidden.
- Tests: native placement maths for FlyBy (the midpoint/offset formula) and
  Orbit, toggling back to cockpit per native, the director's subject choice and
  dwell, input-cancels.
- Report `docs/reports/P580_CINEMATIC_CAMERAS.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
