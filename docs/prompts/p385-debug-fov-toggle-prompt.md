# P385 — debug-mode camera FOV override (compare current wide FOV against native's real default)

## Why this task exists

This session's frustum discussion surfaced that this port's camera FOV
(`Game1.cs:3667`, `const float FovDeg = 100f;` — vertical, ~130° horizontal
at 16:9) is deliberately very wide ("a wide, spacious fleet shot so
individual ships read," per the existing comment). Domingo observed this
as sometimes seeing an aleph "from the side of the ship, not in front" —
explained by the wide FOV, not a bug — and wants a way to flip to native's
real default FOV to compare live, without committing to a permanent
change yet ("lets have it for debug mode i can test it later").

## Confirmed real native default

`~/Development/Allegiance/src/WinTrek/WinTrek.h:26`:
```cpp
const float s_fDefaultFOV = RadiansFromDegrees(50.0f);
```
Applied via `Camera::SetFOV`/`SetPerspective` (`engine/camera.cpp:148-157`,
`cos(0.5*fov)/sin(0.5*fov)` → the camera's internal `m_focus` scalar), set
at combat-screen entry and on every `ResetCameraFOV()` call
(`WinTrek.cpp:329, 3814, 6491, 6600`). **50° is native's real default —
exactly half this port's current 100°.** (One real native wrinkle, note
but don't port here: `WinTrek.cpp:417` scales FOV dynamically —
`fov = (range / radius) * s_fDefaultFOV` — in some external/orbit camera
context; out of scope, this task only needs the static default value.)

## Scope

1. Add a new client launch flag, matching the existing `Program.cs` `--flag`
   / `--flag=value` convention exactly (see the `--lobby`/`--capture`
   parsing loop, `Program.cs:24-42`): `--debugMode` (boolean presence
   flag, no value needed, mirroring the server's `liveSettings.debugMode`
   naming for consistency even though this is a client-only flag with no
   wire exposure — see "explicitly out of scope" below for why).
2. When present, use native's real default (`50f` degrees) as `FovDeg`
   instead of the current hardcoded `100f` at `Game1.cs:3667`. Implement
   as a simple instance/static field defaulting to `100f`, overridden to
   `50f` when the flag is set — do not remove or rename the existing
   constant's normal-mode value; normal (flag-absent) launches must be
   byte-for-byte unaffected.
3. Log plainly at startup when active, matching this port's existing
   `[boot]`/`[npc]`-style convention, e.g.
   `[client] --debugMode: camera FOV set to native default 50° (vertical), was 100°`.

## Explicitly out of scope

- **Wiring this to the server's actual `liveSettings.debugMode` value.**
  The server flag governs spawn topology/lifepod endurance (P377-P379,
  P381-P382) and is never exposed to clients today — there's no existing
  wire signal carrying it, and building one just to drive a purely
  cosmetic camera parameter is disproportionate. This task's `--debugMode`
  is a client-only launch flag Domingo sets by hand alongside the server's
  flag when testing, same manual-pairing convention already used for
  `docker/compose.server.json`'s `liveSettings.debugMode` itself. If a
  real need for the client to know the server's debug state ever comes up
  for a gameplay-affecting reason, that's a separate, bigger task.
- `WinTrek.cpp:417`'s dynamic range-scaled FOV — a different, unrelated
  camera behavior, not this task.
- Any UI/hotkey to toggle FOV live mid-session — launch-time flag only,
  matching `--lobby`/`--capture`'s existing shape.
- Changing the default (flag-absent) FOV value — stays `100f`.
- P368-P384 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real client with `--debugMode` and
  confirm the boot log line appears and the rendered FOV is visibly
  narrower (compare a screenshot or live observation against a normal
  launch without the flag) — specifically check whether a warp/aleph that
  previously appeared "from the side" near the edge of the wide FOV is now
  outside the frame at the narrower 50°, confirming the change is real and
  matches native's default framing.
- Confirm a normal launch (no `--debugMode`) renders identically to before
  this change (same FOV, no new log line).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least 2x:
  only the pre-existing documented live-wire timing flakes expected.

## Deliverables

1. The `--debugMode` flag and FOV override, committed on `master` (not
   pushed — Domingo pushes).
2. Report doc `P385_DEBUG_FOV_TOGGLE.md` in the repo root: confirmation of
   the live before/after comparison performed.
