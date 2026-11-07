# P950 — Relative mouse flight

Implemented in the `codex/p950` working tree, based on dispatch commit `9e51530a`. No commits, pushes, stash operations, deployment or remote configuration changes. The initial report was written before gating. Qualification results and remaining non-task failures are recorded below.

## SDL access and implementation

The pinned MonoGame **DesktopGL 3.8.5.1** ships SDL2 **2.32.10**. Inspection of the installed assembly confirms its SDL wrapper (`Sdl`) and loader (`FuncLoader`) are internal, with no public relative-mouse API. `Mouse.PlatformGetState` uses `SDL_GetGlobalMouseState`; `Mouse.SetPosition` uses `SDL_WarpMouseInWindow`. MonoGame's event loop pumps SDL, and it does **not** consume `SDL_GetRelativeMouseState`.

`src/Allegiance.ClientCore.Game/Flight/RelativeMouse.cs` loads `SDL_SetRelativeMouseMode` and `SDL_GetRelativeMouseState` through Cdecl delegates. Its library candidates follow DesktopGL's architecture, bundled runtime, application-directory and system paths, including the universal `runtimes/osx/native` package. `SDL_WasInit(SDL_INIT_VIDEO)` rejects an uninitialized second SDL instance. The handle stays alive until disposal. Android, absent exports/libraries, and refused relative-mode requests retain the centre-warp fallback. Mode selection logs `[mouse] relative` or `[mouse] warp` once per selected mode per game instance.

`Game1.RelativeMouse.cs` owns the shared policy for **both Classic and Modern**. The same helper supplies ordinary spectator free-look and shipless local-skirmish free-look. Orbit dragging remains a free pointer.

| State | Owner / behavior |
|---|---|
| Focused airborne mouse flight | `Flight`: hidden/confined relative pointer; read accumulated pixel deltas once per input sample |
| Focused spectator free-look | `FreeLook`: same delta transport and existing look sensitivity, independent of the flight-stick toggle |
| ToggleMouse off in ordinary flight, expanded map, command camera, orbit pointer | `None`: visible pointer |
| Dock/hangar, chat/quick-chat, command composition, menus/settings/help, briefing, investment/teleport, paused skirmish | `None`: visible pointer |
| Focus loss, non-flight screens, disconnect, exit/disposal | Immediate release; visible pointer where the window remains alive |
| Re-enter flight/free-look or switch owners | Release previous owner, flush queued motion, discard the first sample; preserve integrated stick deflection |

The update wrapper synchronizes capture around every frame, including early returns. Input consumption also refreshes ownership after in-frame toggles/composition changes. Deactivation and exit release capture directly; cancellation of an exit does not permanently disable later capture. Existing scripted training pilots keep their existing pixel-offset fixture and do not acquire an OS pointer.

Successful builds used a task-local CLI home/cache, the existing local package directory and an offline package-source configuration, with NuGet audit disabled for these sandbox builds. **Local-only scope deviations:** an early restore was inadvertently allowed to use default NuGet sources before the offline source list was installed (the initial default attempt failed on its read-only audit cache). The existing discovery fixture also made an unexpected non-loopback probe through its real-CLI fallback, described below. These automatic access paths should have been blocked before running the checks; no remote configuration/deployment was performed. Subsequent builds use cached sources and isolated discovery reruns use an absent-CLI stub.

## Mapping equivalence

`MouseFlightController` arithmetic and response settings are unchanged. Previously `dx = mouse.X - centreX`; the relative path supplies SDL's accumulated `dx` in the same sign/pixel units. Both reach the same `Update(dx,dy)`, then the same deadzone/rescale, linear/quadratic response, invert, adjustable sensitivity, roll-modifier and autopilot handling. No delta is multiplied by frame time, no stick decay is added, and no physical joystick/gamepad or persistent option is changed.

`P950RelativeMouseTests` compares **exact** deflections and yaw/pitch outputs for complete motion histories, covering all three deadzones, both curves, both invert states, sensitivity endpoints, zero motion/hold, reversals and unit-circle saturation. It also covers capture/release/re-entry/owner switching, first-sample flushing, idempotent disposal, unavailable/refused SDL and once-only logging. Both HUD styles run the ownership sequence. The existing controller/response tests remain part of the isolated gate. A final regression also pins the observer menu-close case: shipless observers return before the ordinary flight-frame latch resets, so a stale flight-only consumed-menu flag must not block free-look after the actual menu closes.

## Workstation window proof

**Linux, GNOME Wayland desktop, SDL `x11` through Xwayland.** A compositor-created virtual mouse supplies OS input through the local GNOME EIS socket (`libei`); twenty motions of **(+20, −10)** reach SDL's raw relative accumulator. The production client, controller, local flight simulation and real embedded Free Flight host consume them. The proof never writes controls, virtual-stick values, ship orientations or scripted training input. No SDL relative-warp-emulation hint is used.

Both styles produced **yaw −0.57296485 / pitch +0.10427519**, with measured orientation changes of **87.65° Classic / 35.42° Modern**. The window also verifies briefing/chat/menu release and reacquisition, spectator free-look ownership including the stale menu-latch regression, ToggleMouse-off release and release on exit. Exact receipts: [window.json](P950_assets/window.json); [log excerpt](P950_assets/mouse-log.txt).

I opened and inspected both captures. [Classic](P950_assets/mouse-flight-classic.png) shows the outlined reticle/status labels, blue geometry, Neptune map and equipment panes among asteroids. [Modern](P950_assets/mouse-flight-modern.png) shows circular status gauges, the deflected mouse indicator and `MOUSE DZ 10% QUAD`, with the rotated scene and Neptune map. The briefing was dismissed before capture; each screenshot was flushed before changing style.

The final post-fix window refresh passed after the desktop became unlocked. One intermediate attempt was blocked by GNOME with `Session creation inhibited` while its screen-saver API reported active; no lock/security setting was bypassed. Final capture and final build fingerprints match in the compact evidence. Focus-loss behavior is covered by the pure transition tests; the final capture itself had uninterrupted focus.

**RDP, SDL's native Wayland backend, Windows, macOS and a physical mouse were not tested.** The local compositor virtual mouse is the tested input source. XTest cannot provide this workstation's raw Wayland pointer motion, so the reproducible proof uses EIS instead. This is not an RDP end-to-end claim.

Reproduce from the repository root with the artwork variables from `local.env`, a task-local `XDG_CONFIG_HOME`, and two local terminals:

```sh
SDL_VIDEODRIVER=x11 ALSOFT_DRIVERS=null "$HOME/.dotnet/dotnet" run --no-build \
  --project tools/p950-mouse-proof -- \
  src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  docs/reports/P950_assets compositor > .scratch/p950/window.log 2>&1
python3 tools/p950-mouse-proof/inject-compositor.py .scratch/p950/window.log
```

The injector requires the workstation's `python3-dbus`, `libei`, X11 and GNOME compositor. It creates a local virtual pointer, focuses only the named proof window and closes its session afterward; it does not open an RDP/network connection. `manual` mode accepts a physical mouse instead.

## Gate results

| Gate | Passed / Failed / Skipped or result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **119 projects, 0 warnings / 0 errors** |
| New tests, each method 3× in isolated processes | **30 / 0 / 0** |
| Existing mouse controller/response cohort 3× | **126 / 0 / 0** |
| One complete Fast tier | **13,307 / 11 / 3**; 13,321 executions in 650.0 s; **FAIL** |
| Fast infrastructure | Passed; see compact qualification evidence |
| Non-task failures and timing checks, 3× isolated | Completed; classification below |
| Showcase, five alternating pairs | **Base 5/5; patch 3/5 full-test passes, zero skips. Parking bound: 5/5 each.** Patch arrival misses: 23/30 and 18/30; isolated follow-ups 3/3 passed |
| Glossary links and parity CSVs | Passed, including P950's two supplemental rows; LF CSV |
| Working-tree hygiene | `git diff --check` passed; no personal paths/emails/secrets added to code |

Showcase qualification uses the unmodified P700 15v15 fixture on `m2xspiderw.igc`, civs 25/3, with server GC, pool transport, native ship updates, native GotoPlan and parallel broadcast building explicitly pinned. Immutable snapshots and encode-once retain their enabled defaults; native broadcast ordering stays off. Five consecutive alternating base/patch pairs are the authoritative cohort; any isolated retries are reported separately. The cohort is a **qualified FAIL** for the full fixture, despite every parking-bound observation passing. `git diff HEAD` confirms no changes in Core, Server, Bot, ClientCore, MCP, P700 or its P660 setup fixture. MCP does not reference the game presentation library and P700 creates no Game1, so the modified capture code is outside this executable path. The arrival signature is already ledgered (including failing dispatch controls) in P940; this does not erase the unequal observed cohort rates. These are unseeded functional controls on a shared host, not a causal performance A/B. An earlier exploratory control without the explicit GC pin was interrupted and is not counted.

The full Fast result remains a failure; isolated passes do not replace it. Its three skips are optional private Meshy-pack cases, with artwork/native sources available through `local.env`. No missing-artwork skips are hidden. The two final observer-latch regression cases were added after this single Fast run and are covered by the final isolated cohort rather than included in its historical discovery count.

Persistent isolated failures are the existing WingmanDefend expected-target/actual-null assertion and five `PlayerBundlePathsTests` executions. The latter assume their temporary bundle is outside a repository; mandatory worktree-local scratch permits ancestor `local.env` discovery. They are already ledgered from earlier lanes. The other assertions pass 3/3 in isolation after redirecting the profile to writable XDG scratch and suppressing the real Tailscale CLI for its intended absent-CLI fixture. The original discovery test's null callback unexpectedly invoked the real local CLI and discovered a non-loopback server; its raw private output remains in scratch. No intentional remote-host proof was performed.

Timing offenders were rerun individually three times. P777's second isolated execution was **10.231 s** against the original **10.227 s** calibrated ceiling (other repetitions **10.162 / 9.716 s**); the remaining successful isolated timing checks fit that ceiling. Exact durations and identities are in the compact evidence and `docs/TEST_FLAKES.md`.

## Showcase control

All five alternating pairs used the same pinned settings. Cells show full-test outcome; arrivals; longest stopped-with-target interval; observation seconds.

| Pair | Dispatch base | Working tree |
|---|---|---|
| 1 | PASS; 24/30; 1.203 s; 180.9 s | FAIL; 23/30; 3.614 s; 480.1 s |
| 2 | PASS; 24/30; 2.310 s; 479.7 s | PASS; 24/30; 0.000 s; 260.7 s |
| 3 | PASS; 24/30; 2.019 s; 286.6 s | PASS; 24/30; 1.115 s; 283.1 s |
| 4 | PASS; 24/30; 0.207 s; 219.0 s | PASS; 24/30; 2.913 s; 205.1 s |
| 5 | PASS; 24/30; 1.919 s; 262.3 s | FAIL; 18/30; 1.922 s; 480.0 s |

The working-tree arrival failures were **23/30** and **18/30** at the original 480-second deadline. All ten runs observed gunfire and damage and satisfied the five-second parking bound; the maximum working-tree stopped interval was **3.614 s**. Full-fixture qualification remains **FAIL: 3/5**, against **5/5** dispatch controls.

The three fresh isolated working-tree follow-ups passed **3/3**, zero skips, with maximum stopped interval **1.813 s**. They do not replace either failure in the original cohort. Exact arrivals, combat receipts and TRX durations are in [qualification.json](P950_assets/qualification.json).

No training mission or pilot source was changed. A supplementary full-lesson sweep was attempted: its ClientCore project passed **126 / 0 / 0**, while the much broader MonoGame all-lesson replay was stopped before completion. It is **not** claimed as a qualified training gate; the focused input tests and real Free Flight proof above are the P950 behavior checks.

Raw build/TRX/window logs, control revision/binary fingerprints and intermediate failed automation attempts live under the worktree's **`.scratch/p950/`**. The sandbox cannot write the lane's external evidence directory. Existing test tools' hard-coded scratch output was retained under `.scratch/p950/generated/`. Small reviewable receipts/screenshots are under `P950_assets/` (under 2 MB). No screenshot or raw log is staged.

## Native intent

Native `effect/efimage.cpp:49-60,182-209` supplies first-enable discard, subtractive virtual-stick integration, unit-circle clamping and centre reset. Native `engine/camera.cpp:408-425` derives free-look from pointer displacement and restores its starting position. These line numbers were re-grepped in the read-only native source. **Era constraint: Win32 cursor warp** transported relative motion; it is not a requirement to reproduce a failing platform call. Tracking: `docs/parity/p950-relative-mouse.csv`.

## Modern approach

SDL relative capture transports pixel motion without depending on per-frame cursor warping, with a preserved warp fallback when relative mode is unavailable. Shared ownership gives menus/focus/exit their pointer back, while existing steering math and response preferences remain unchanged. RDP and native Wayland still need their own device/session validation.
