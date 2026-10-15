# P406 — warp-transit exit clearance is still scaled by WarpVisualScale (leftover of the reverted P238 trigger)

## Why this task exists

Found by the same kimi-k3 3D/flight/controls/navigation/HUD review as
P404/P405 (full report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
finding C4).

## Confirmed bug

`src/Allegiance.Server/ServerHost.cs:853`:
```csharp
float exitClearance = Sector.WarpVisualScale * rw.Warp.Radius + shipRadius + 5f;
```
Native exits at `destPos + up·rand(2,5) + right·rand(2,5) −
(shipRadius + 5)·backward` (`~/Development/Allegiance/src/FedSrv/
fsship.cpp:129-132`) — roughly 35-40 units from the aleph centre for a
scout. The port's scaled clearance puts a radius-100 warp's exit at
roughly **285** units — about 250 units farther out than native.

The comment above the line (`ServerHost.cs:833-845`) still justifies
the scale by "the destination warp's (P238-widened) collision sphere"
— but **P244 already reverted that trigger** to the raw
`warp.Radius + shipRadius` (`Sector.cs:2731`, "P244: raw collision
radius (no WarpVisualScale)"), and P247 shrank the visual itself to
2.5×. The scale factor's original reason for existing is gone. The 2s
`LastWarpTime`/`WarpCooldownSeconds` debounce
(`Server.Combat.cs:1683`, `Sector.cs:2716`) is what actually prevents
re-trigger ping-pong now — native exits *inside* its own collision
radius and relies on its equivalent warpState gate, not distance.

Player-visible effect: you emerge roughly 250 units farther from the
aleph than native, making "transit and double back" tactics and
general aleph-approach geometry feel wrong.

## Scope

1. `ServerHost.cs:853` → `float exitClearance = shipRadius + 5f;`
   (drop the `Sector.WarpVisualScale * rw.Warp.Radius` term entirely).
2. Refresh the stale comment at `ServerHost.cs:833-845` — it currently
   justifies the removed scale factor by the P238 trigger, which P244
   already reverted; replace it with the real current rationale (the
   2s debounce is what prevents re-trigger, not exit distance).

## Explicitly out of scope

- `Sector.WarpVisualScale` itself, and anything using it for the
  actual visual warp-gate rendering size — only its use in the exit-
  clearance calculation is wrong, the constant may still be correct
  for rendering.
- The `LastWarpTime`/`WarpCooldownSeconds` debounce mechanism — already
  correct, don't touch.
- P368-P405 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: transit a real warp in the debug topology
  (`liveSettings.debugMode`) and log the exit distance to the partner
  warp/aleph — expect `shipRadius + 5` ± jitter (roughly 40 units for
  a scout), not the old ~285.
- Confirm no re-trigger ping-pong across the 2s debounce window (fly
  straight after transit, do not steer back toward the warp
  immediately — should not re-trigger).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P406_WARP_EXIT_CLEARANCE.md` in the repo root: the
   exact change, the corrected comment, and the live verification
   performed.
