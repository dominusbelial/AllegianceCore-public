# P385 — Debug-mode camera FOV override (native 50° default)

## What was added

A new **client-only** launch flag, `--debugMode` (boolean presence flag, no
value), matching the existing `Program.cs` `--lobby` / `--capture` parsing
loop. When present, every camera the pilot sees is dropped to native's real
default vertical FOV, `s_fDefaultFOV = 50°` (`WinTrek.h:26`); normal
(flag-absent) launches are byte-for-byte unaffected.

| Camera | Normal launch | `--debugMode` |
|---|---|---|
| Capture-overview (`ComputeCaptureCamera`) | 100° | **50°** |
| Chase / Cockpit / Missile (in-flight) | 75° | **50°** |

Files changed (all in `src/Allegiance.ClientCore.MonoGame/`):
- `Program.cs` — parse `--debugMode` (presence flag), thread into `LaunchOptions`.
- `Game1.Session.cs` — `LaunchOptions.DebugMode` field (client-only; no wire exposure).
- `Game1.cs` — `_debugMode` + `_captureFovDeg` (defaults 100°) fields; constructor
  overrides to 50° and logs at startup when the flag is set.
- `Game1.Lifecycle.cs` — `ComputeCaptureCamera` now uses `_captureFovDeg`
  (100 normal / 50 debug) instead of a hardcoded 100°; the three in-flight
  cameras use `flightFovDeg` (75 normal / 50 debug).

Startup log when active (matching the port's bracketed log convention):

```
[client] --debugMode: camera FOV set to native default 50° (vertical), was 100°
```

## Scope decision (flagged for Domingo)

The brief anchored the change on `Game1.cs:3667` — the `const float FovDeg = 100f`
inside `ComputeCaptureCamera`, which is the **`--capture` fleet-overview camera
only**. It is never rendered in normal flight. The cameras Domingo actually flies
with (chase/cockpit/missile) use a separate `fieldOfViewDegrees: 75f`
(`Game1.Lifecycle.cs:110-133`).

The brief's own verification ("rendered FOV is visibly narrower", "a warp/aleph
... is now outside the frame at the narrower 50°") is only satisfiable in the
in-flight view. So, as a judgment call in the absence of an available reviewer,
**`--debugMode` was extended to drop the three in-flight cameras (75° → 50°) as
well**, while keeping the letter-of-the-brief overview override (100° → 50°).
This makes the feature genuinely usable for the stated purpose ("lets have it
for debug mode i can test it later", comparing native framing while flying).
If you want it strictly overview-only, drop the `flightFovDeg = _debugMode ? 50f : 75f;`
block in `Game1.Lifecycle.cs`.

Out-of-scope respected: no wire signal (server `liveSettings.debugMode` is still
paired by hand, never transmitted), no dynamic range-scaled FOV port
(`WinTrek.cpp:417`), no live mid-session toggle, and the default (flag-absent)
FOV values are unchanged (100° / 75°).

## Live proof performed (real output, not just build)

**1. Boot-log line — real client, real launch.**

Ran the compiled client binary against the real Wayland display, with and
without the flag:

```
$ dotnet Client.MonoGame --debugMode 127.0.0.1 59999 dbgtest
[monogame] direct connect: ...
[client] --debugMode: camera FOV set to native default 50° (vertical), was 100°   <- present
...
$ dotnet Client.MonoGame 127.0.0.1 59888 nodbgtest
...no --debugMode line at all...                                            <- absent
```

The line appears only with the flag; the normal launch's startup output is
otherwise identical.

**2. Render/FOV narrowing — real production `ChaseCamera`, offscreen frames.**

A MiniGame harness (`/tmp/p385fov`, ProjectReference to the MonoGame csproj,
same offscreen-RT recipe as the project's P234/P240/P251 live-verify harnesses)
drives the REAL in-flight `ChaseCamera` (production offsets 350-behind /
90-above, near 10, far 6000) through its real `Matrices()` at FOV 75 (normal)
vs 50 (debug). It renders a row of 13 landmark "ships" plus one peripheral
magenta "aleph/warp" at eye level ahead of the ship, and measures where each
lands on screen.

Evidence: `.verify_p385/fov75_normal.png`, `.verify_p385/fov50_debugmode.png`,
`.verify_p385/p385_run.log`.

```
fov75_normal:    FOV=75°  grid 13/13 pts in frame (ndc -0.886..0.886)
                 aleph ndc.x=0.775  IN frame (screen x=1136 px)   <- "seen from the side", near right edge
fov50_debugmode: FOV=50°  grid  9/13 pts in frame (ndc -0.972..0.972)
                 aleph ndc.x>1      OFF FRAME (clipped right, magenta absent, red edge marker)
```

Independent vision check of the saved PNGs agrees: at 75° the aleph (magenta)
is present in the right half of the frame; at 50° it is gone and a red
off-frame marker appears at the right edge. This is exactly the brief's
required check — the warp/aleph that previously appeared "from the side" near
the edge of the wide FOV is now outside the frame at native's narrower 50°.

**3. Overview camera (the literal constant) — projection FOV.**

The same `Matrix.CreatePerspectiveFieldOfView` call `ComputeCaptureCamera`
uses delivers, at 16:9:

```
FovDeg=100 -> horizontal half-FOV 64.7°   (the wide fleet shot)
FovDeg=50  -> horizontal half-FOV 39.7°   (native default)
```

## Build & test

- Whole-solution build: **0 warnings, 0 errors**.
- Full `dotnet test AllegianceCore.slnx` run 2×. The project this task actually
  changed — **`Allegiance.ClientCore.MonoGame.Tests` — is 761/761 green in both
  runs.** Every failure is in the server/wire assemblies (`Allegiance.Core`,
  `Allegiance.ClientCore`), none reference any P385 symbol, and they follow the
  documented rotating live-wire flake pattern (a different subset each run):

  | Test | Run 1 | Run 2 | Isolation |
  |---|---|---|---|
  | LiveTurretGunnerBindingTests.* | ✗ | ✗ | ✗ (documented persistent machine flake) |
  | LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission | ✗ | – | ✗ (documented environmental, P200) |
  | AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide | ✗ | ✗ | ✓ |
  | LiveCountdownStartPingTests.* | ✗ | – | ✓ |
  | LiveRipcordTests.* | ✗ | – | ✓ |
  | LoadoutChangeLiveTests.* | ✗ | – | ✓ |
  | CargoBayReloadTests.* | – | ✗ | ✓ |
  | LiveChatSectorWireTests.* (documented family) | – | ✗ | ✓ |
  | LiveTreasurePickupTests.* | – | ✗ | ✓ |
  | NpcShipFoundationLiveTests.* (documented family) | – | ✗ | ✓ |
  | ClientCore JoinFlowLiveTests / ShipDeleteGoneLiveTests | ✗✗ | ✗✗ | ✗✗ (live-server wire tests; the ClientCore.Tests project does not even reference the MonoGame project — see below) |

  Structural guarantee: `Allegiance.ClientCore.Tests.csproj` references only
  ClientCore/Core/Server — never the MonoGame project this change touches — so
  those two ClientCore failures cannot load or be affected by P385.

Totals: Core.Tests 3366 total / 3360 passed in both runs; ClientCore.Tests 227 /
225 in both runs; MonoGame.Tests 761/761 in both runs; Lobby.Tests 12/12.

## Deliverables

1. `--debugMode` flag + FOV override, committed on `master` (NOT pushed —
   Domingo pushes).
2. This report, `P385_DEBUG_FOV_TOGGLE.md`, in the repo root.
