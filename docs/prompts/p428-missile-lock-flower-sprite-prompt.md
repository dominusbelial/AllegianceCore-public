# P428 — real missile-lock sprites (native's flower-burst), not a procedural bracket

## Why this task exists

Domingo, from memory of the original game: *"lock would show a very cool
visual cue around the ship box wrapper, it should draw a very colored
flower shape... when the marker was shown lock was gained and you could
launch the missile."* Investigated and confirmed against native's real
source + real art assets (decoded and viewed directly, not guessed).

## Confirmed real native mechanic

`~/Development/Allegiance/src/WinTrek/radarimage.cpp:217-218,330-332`
swaps between two distinct bitmaps by lock state (asset ids named in
`artwork.h:80-81`):
- **Partial lock**: `AWF_FLIGHT_PARTIAL_MISSILE_LOCK_ICON` →
  `plockbmp.mdl` (64×64) — open corner-bracket square, orange/brown.
- **Full lock**: `AWF_FLIGHT_MISSILE_LOCKED_ICON` → `lockedbmp.mdl`
  (64×64) — the SAME orange corner brackets, **plus a green 4-petal
  flower-burst filling the center**. Confirmed by decoding both real
  `.mdl` files to PNG (via this port's own `MdlTextureDecoder`) and
  viewing them directly — genuinely flower-shaped, not a color tint.
  Domingo's "flower" memory is accurate; his color memory (red) is
  close but off — it's orange corners + green center, not red.

This is a real **sprite swap** between two fixed assets on the
partial→full transition, not a recolor/animation of one shape.

## Confirmed port gap

This port's current lock indicator
(`src/Allegiance.ClientCore.MonoGame/Rendering/LockBracketStyle.cs` +
`HudRenderer.cs`'s `DrawLockBracket`, P230) is entirely procedural: four
hand-drawn open-corner "L" brackets (`DrawLine` calls on a 1×1 white
pixel), tinted by `LockBracketStyle.ColorFor` (grey→amber→red across
None/Partial/Full) and pulsed/spun via `HalfSizeFor`/`RotationFor`. It
never loads or draws `plockbmp.mdl`/`lockedbmp.mdl` at all — no flower,
no orange highlight detail, nothing sprite-based. The lock STATE
machinery itself (`Ship.UpdateMissileLock`, the wire message, client-side
`MissileLockState`/`MissileLockFeedback` tracking) is already correct
and faithful to native — this is a rendering-only gap.

## Scope

1. Decode the two real assets the same way this port's existing
   precedent already does it — reuse the pattern in
   `src/Allegiance.ClientCore.MonoGame/Rendering/GaugeTextures.cs`
   (`MdlTextureDecoder.Decode` + `ToPremultipliedRgba`, loaded once and
   cached, resolved via `ALLEGIANCE_ARTWORK_PATH`) — don't invent a new
   loading mechanism. Locate `plockbmp.mdl`/`lockedbmp.mdl` in the real
   Artwork tree the same way `GaugeTextures`/`RockTextureFactory`
   already resolve their own `.mdl` files.
2. Replace `DrawLockBracket`'s procedural corner-bracket drawing with
   real sprite rendering:
   - **No lock**: keep whatever this port already does for the
     no-lock state (investigate whether native shows anything at all
     for `LockState.None` — if native shows nothing until a lock
     attempt begins, match that; don't invent new behavior here if the
     brief's own confirmed scope is only the two real assets above).
   - **Partial**: draw the real `plockbmp.mdl` sprite.
   - **Full**: draw the real `lockedbmp.mdl` sprite (the flower-burst).
   - Preserve the existing pulse animation on top of the real sprite if
     it reads well (a static sprite may look inert compared to today's
     animated brackets) — use judgement, but the SHAPE must be the real
     asset, not a procedural stand-in. Note in the report which
     animation choice was made and why.
3. Keep `LockBracketStyle`'s existing pure per-state API shape where it
   still makes sense (size/rotation-over-time helpers may still be
   useful for the sprite's scale/pulse), but its `ColorFor`
   grey/amber/red tinting is superseded by the real sprites' own fixed
   colors — remove or repurpose it accordingly rather than leaving dead
   code.

## Explicitly out of scope

- The lock STATE/math itself (`Ship.UpdateMissileLock`, the wire
  message, `MissileLockState`/`MissileLockFeedback`) — already correct,
  don't touch.
- `TargetSelectionStyle`'s own selection-box rendering (P345, a
  DIFFERENT, closed-rectangle box for the player's own autopilot
  target) — a separate mechanic, don't conflate the two.
- Any other HUD element.
- P368-P427 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: connect a real MonoGame client to a real
  server, get a real missile lock building on a target (any existing
  live-lock test setup this port already has is fine to reuse), and
  confirm visually: partial lock shows the real orange bracket sprite,
  full lock shows the real bracket-plus-green-flower sprite — screenshot
  or describe both states plainly in the report.
- A test proving the two `.mdl` assets decode to the expected real
  pixel dimensions/non-trivial content (mirroring whatever assertion
  style `GaugeTextures`' own tests already use for its assets).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P428_MISSILE_LOCK_FLOWER_SPRITE.md` in `docs/reports/`
   (this project's reports now live there, not the repo root — see
   `docs/reports/` for the existing convention): the asset paths found,
   the animation choice made on top of the real sprites, and the live
   verification performed.
