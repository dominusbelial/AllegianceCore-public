# P355 — Fix ships crushing to visually-black at unlit orientations

## Why this task exists

A live playtest this session (2026-09-23, verifying P354's ship-ship
collision physics) hit a real, reproducible rendering problem: two
companion bots with the **exact same hull** (`Enh Fighter`, native
`wc_icfig`) in the same scene — one rendered normally, the other
appeared **fully black**, indistinguishable from the space backdrop.
Because both ships share identical hull/texture/asset, this rules out
a texture-content bug (unlike the *real* dark-livery cases already
documented for some GT/IC hulls in P352/P353 — this is not that).

**Grounded investigation so far (do not re-derive, but DO verify —
see Scope item 1):**

- `ShipRenderer.Draw` (`src/Allegiance.ClientCore.MonoGame/Rendering/ShipRenderer.cs:89-118`)
  calls `basic.EnableDefaultLighting()` every draw
  (`ShipRenderer.cs:106`), then overrides only
  `AmbientLightColor = ShipAmbient` = `(0.32, 0.34, 0.36)`
  (`ShipRenderer.cs:52,107`, added P337 specifically *"so a ship's
  unlit hull faces still read real texture detail against space
  instead of crushing to black"*).
- MonoGame/XNA's `BasicEffect.EnableDefaultLighting()` sets up 3
  **fixed, hardcoded directional lights** in world space (key/fill/
  back, not camera-relative, not object-relative) — every ship in the
  scene is lit by the exact same 3 light vectors regardless of its
  position.
- The world matrix's ROTATION component is built purely from each
  ship's own sim `Orientation`
  (`PoseConverter.ToXnaWorldRelative` → `ToXnaRotation`,
  `src/Allegiance.ClientCore.MonoGame/Rendering/PoseConverter.cs:25-54`)
  — camera-relative offset only touches translation, never rotation.
  So two ships genuinely facing different directions (plausible here:
  both companions were independently AI-flown with different control
  inputs, so their orientations diverge over real flight time) will
  legitimately get different lighting results from the same fixed
  3-light rig — this is consistent with, but **not yet confirmed
  identical to**, what was observed live.

**Leading hypothesis (verify before fixing, don't assume):** a ship
whose lit faces happen to point away from all 3 of `BasicEffect`'s
fixed default lights receives only the `ShipAmbient` term
(`0.32-0.36`) multiplied against its base texture color. For a
darker-toned hull texture, `0.32 × (dark texel)` can render close
enough to `(0,0,0)` against a black space background to look
genuinely, indistinguishably black on screen — not a code path that's
"broken" for one ship and "working" for another, but a readability gap
in the current 3-fixed-light + flat-ambient model at certain
(orientation, texture darkness) combinations. This needs live
confirmation, not just a build — see Scope item 1.

## Scope

1. **Verify the hypothesis before touching any code.** Reproduce
   deliberately: spawn (or pose) two ships with the identical hull and
   texture at two different, known orientations — one facing toward
   at least one of `BasicEffect`'s 3 default light directions, one
   facing away from all three — and confirm live (screenshot or a
   direct per-pixel brightness check) that the away-facing one really
   does crush to visually-black while the toward-facing one doesn't.
   If the real cause turns out to be something else entirely (e.g. a
   genuine per-instance state bug, not orientation/lighting), say so
   plainly and pivot the fix accordingly — don't force this hypothesis
   if it doesn't hold up.
2. **Fix it** so no valid orientation of a real ship texture ever
   renders indistinguishable from the black space backdrop. Reasonable
   approaches, pick whichever fits this project's existing rendering
   conventions best (or propose something better, this is a POC-grade
   lighting setup per `ShipRenderer.cs:11-13`'s own doc comment, there
   is room to improve it directly rather than just patch around it):
   - Raise `ShipAmbient`'s floor (simplest, but changes the look of
     already-well-lit ships too — measure that trade-off).
   - Add a camera/view-relative fill light (a common "headlight"
     technique: a light that always points from the camera toward the
     scene, so whatever face the camera is looking at always gets
     *some* direct light regardless of world orientation) alongside
     the existing default 3 lights, instead of only relying on flat
     ambient.
   - A minimum per-pixel brightness clamp.
   Whichever you pick, verify it live at multiple orientations (not
   just the one lucky angle that happened to look fine in dev) and
   confirm the **existing, already-correct** well-lit look doesn't
   regress (the P352/P353 real-dark-livery cases, e.g. `gt-fighter`/
   `gt-tt`/`gt-sf`/`gt-carrier`, documented as genuinely dark-textured
   and NOT a bug — those should still read as recognizably dark/
   metallic, not artificially brightened into looking wrong).

## Explicitly out of scope

- The other 3 bugs found in the same playtest session (auto-dock
  firing on a fresh spawn near a station; a shop-UI
  `NullReferenceException` in `DataHullTypeIGC.CanMountPart`; a
  radar/target-pick visibility gap for a stationary-since-creation
  ship reaching a late-joining viewer) — separate, unrelated issues,
  not this task.
- `StationRenderer.cs:87`, `ProbeRenderer.cs:102`,
  `BuildingEffectRenderer.cs:81`, and the missile renderer
  (`Game1.cs:5349`) use the exact same `(0.32,0.34,0.36)` ambient
  constant and could plausibly have the same class of issue — **note
  it if your fix's investigation surfaces something conclusive about
  them, but do not modify them** in this task; ships only. A
  follow-up task can decide whether the same fix should apply there.
- P354's collision physics itself — already verified, unrelated,
  don't touch `Sector.ResolveShipShipCollisions`.
- Anything in `tools/vizfight` — the debug harness some of this was
  discovered through; fix the real client rendering code, not the
  test harness.

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the original repro (two
  identical-hull ships, one at an away-facing orientation) in a real
  running client and confirm BOTH are now visibly distinguishable from
  the space backdrop — a screenshot comparison (before/after) is the
  clearest evidence. Also confirm at least one already-correct
  well-lit ship (any hull that rendered fine before) still looks
  right, not overly flattened/washed out by whatever fix was applied.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. Report doc `P355_SHIP_LIGHTING_FIX.md` in the repo root: what the
   real root cause turned out to be (confirm or correct the
   hypothesis above), the exact fix applied and why that approach was
   chosen over the alternatives, the before/after live visual proof,
   and explicit confirmation the well-lit case didn't regress.
