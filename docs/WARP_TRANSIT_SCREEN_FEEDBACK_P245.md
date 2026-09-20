# Warp Transit Screen Feedback (P245)

## What this is — and is NOT

A short, full-screen **flash-to-black-and-back** that fires at the exact moment
the local player's ship changes sector on a warp transit, in the MonoGame
client.

This is **original UX design, not a native-source port.** An exhaustive grep of
the entire native `WinTrek/` tree found no fade/flash/blackscreen transition
coded on warp transit — the only transit-adjacent client feedback in the native
checkout is a `jumpSound` play (`trekigc.cpp:3998,4415`). Domingo's memory of a
"quick blackscreen and then you were thrown into the other system" plausibly
arises from the real velocity-fling mechanic read together with the
instantaneous scene swap this port performs; there is no coded native screen
effect to reproduce. This stage fills a real, confirmed usability gap (P244
fixed the "aleph vanishes" bug but transit itself still felt instantaneous and
unmarked).

**Not touched:** the velocity-carry mechanic itself (exit velocity = entry
speed + bonus, launched away from the exit gate — `ServerHost.cs:675-702` vs
native `fsship.cpp:126-127`) is already correct and untouched; the 
`ExitWarpSpeedBonus` tuning gap is P246's job; `WarpRenderer.cs` / the warp
mesh is untouched; no native source was modified (read-only).

## Design

- **Detection** is the same one-shot the transit itself produces on the wire:
  the server sends exactly one `FM_S_SET_CLUSTER(7)` per transit to the
  transiting peer (`Server.OnShipWarped`, `Server.cs:11582-11611`), reflected
  in the client as the local ship's `SectorId` changing (`ClientWorld.cs:202-212`).
  `Flight/WarpTransitFlash.cs` edge-detects that change (previous known sector
  != current) — a strict edge, not a distance check — so it fires **exactly
  once per transit** and re-arms only on a genuine later change. The first
  `SET_CLUSTER` at join (the "here is your ship" assignment) establishes the
  baseline without flashing.
- **Effect** is a triangular opacity pulse 0 → 1 (fully black) → 0 over
  `FlashDurationSeconds = 0.36s`, peaking at the midpoint (`0.18s`), drawn as
  a full-viewport opaque black rect LAST in `HudRenderer.Draw` (over HUD and
  all) — a genuine brief "blackscreen", distinct from P228's translucent red
  damage wash. Short enough that the player immediately sees/reacts to their
  new velocity and orientation (the fling is never masked for long).
- **Scope discipline:** the tracker is pure (no MonoGame types on its surface,
  headless-testable), gated to `MyShipId` in `DrawHud` exactly like the P228
  damage tracker.

## Files

- `src/.../Flight/WarpTransitFlash.cs` (new) — pure tracker.
- `src/.../Rendering/HudFrame.cs` — added `WarpFlash` float to the frame.
- `src/.../Rendering/HudRenderer.cs` — draws the black wash last.
- `src/.../Game1.cs` — feeds the tracker with the local ship's sector in
  `DrawHud`; logs the single arm.
- `tests/.../WarpTransitFlashTests.cs` (new) — 9 pure-logic tests.

## Live proof (.verify_p245/)

Real in-process `Allegiance.Server` (XXV_simple.igc) + real `ConnectionSession`;
the ship is driven kinematically into a real warp until the server performs a
genuine transit; each captured frame renders the real `HudRenderer` (real
`WarpTransitFlash`) offscreen. Log excerpt:

```
[warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
[warp] peer 1 ship 8096 -> SET_CLUSTER sector 1057 ...
[p245] >>> TRANSIT ARM #1: frame=22 elapsed=1.599s sector -> 1057 opacity=0.000 (client first saw new sector)
[p245] >>> TRANSIT ARM #2: elapsed=2.707s sector -> 1051
[p245] Phase B done: arms total=2 (expected 2 live transits)
```

- **Exactly once per transit:** two live transits in one run → exactly two
  arms (`TRANSIT ARM #1` / `#2`), confirmed by the tracker's `JustTransited`
  edge plus the server's own `[warp] crossed` + `SET_CLUSTER` logs.
- **Fires at the transit moment, not before:** frames 000–021 (pre-transit) are
  all `opacity 0` and byte-identical (57,307 B — uniform clear frame); the arm
  lands on the same frame the sector changes.
- **Clears quickly:** the opacity sequence across the transit is the designed
  pulse —
  `0 → 0.34 → 0.69 → 0.96 → 0.63 → 0.30 → 0`
  (frames 22→28). **Measured** from the tracker's real wall-clock at the
  labels: flash begins at transit (t=0), reaches near-full black at ~173 ms,
  fully clear by ~360 ms (the visible blackout window ≈ 0.36 s, matching the
  design constant — the brief asks for the measured figure, here 0.36 s).
- **Vision-confirmed screenshots:** frame `f025` (peak) = ~95–98% black
  blackout; frame `f028` (cleared) = normal clear HUD; frame `f000` (pre) =
  normal clear HUD. The blackout is unmistakable and momentary.

**Must-fail proven:** neutralizing the tracker's arm condition turns
`SectorChange_ArmsOnce_...` and `MultipleTransits_EachArmExactlyOnce` red (2/2),
then green again after revert.

## Build / test

- Whole-solution build: **0 warnings, 0 errors.**
- `Allegiance.Core.Tests`: 3065 pass / 3066 across multiple runs — only the
  documented pre-existing `LiveTurretGunnerBindingTests` timing flake (fails on
  the pristine tree; not chased). One intermittent
  `LiveWinConditionTests.Board_refused_once_mission_over` flake proved
  environmental (green in isolation; my change is client-only, zero Core/Server
  references).
- `Allegiance.ClientCore.Tests`: 83 pass (unchanged).
- `Allegiance.ClientCore.MonoGame.Tests`: **251 pass** (was 242; +9 new
  `WarpTransitFlashTests`), 2 clean runs.

## Out of scope (unchanged)

P244's transit-trigger/render-filter fix (already on master), `ExitWarpSpeedBonus`
(P246), the warp mesh (`WarpRenderer.cs`), and all native source.
