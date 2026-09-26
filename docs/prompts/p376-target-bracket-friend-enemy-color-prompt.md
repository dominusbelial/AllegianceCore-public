# P376 — target selection bracket should be red for enemy, blue for friendly

## Why this task exists

Domingo asked directly: "the box that surrounds a real enemy target
should be red colored, friendly can be blue... regardless of the
team" — i.e. a simple universal (not per-side-unique) friend/enemy
color convention for the target-selection bracket, independent of
which side the local player is actually on.

**Confirmed this is a genuine feature request, not a native-parity
gap**: the bracket in question (`TargetSelectionStyle`/
`TargetSelectionView.cs`, from P345) is this port's own original UI
design — P345's own commit message says its aqua-blue color was
chosen only to look visually distinct from the separate missile-lock
reticle (`LockBracketStyle`'s grey/amber/red), not to encode side
identity. There is no existing native red/blue target-bracket
convention being restored here; this is new, explicitly-requested UX
work. Build it as specified rather than searching for a native
citation that doesn't apply to this particular UI element.

**Current state** (`Rendering/TargetSelectionStyle.cs`): exactly two
fixed colors regardless of the target's side —
`Selected = new(0.28f, 0.78f, 0.95f)` (aqua-blue) and
`Engaged = new(0.50f, 1.00f, 1.00f)` (brighter icy-cyan) — chosen by
`Color col = v.Engaged ? TargetSelectionStyle.Engaged :
TargetSelectionStyle.Selected;` (`HudRenderer.cs:752`).

## Scope

1. **Determine the target's real side** at the same point
   `ComputeTargetSelectionBracket`/`DrawTargetSelectionBracket`
   already resolves the ship (P359 already does exactly this lookup
   for the pilot name/health readout — reuse the same `ShipEntity`
   fetch, don't re-derive it). Compare against the local player's own
   side (`world.MySideId`) — this port has no alliance concept
   (established convention elsewhere in the project), so "friendly"
   collapses to "same side as me" and everything else is "enemy".
2. **Replace the fixed `Selected`/`Engaged` color pair with a real
   friend(blue)/enemy(red) pair**, while preserving the existing
   Engaged-vs-merely-Selected visual distinction (currently conveyed
   by both a brightness difference and border thickness via
   `ThicknessFor(engaged)`) — keep that distinction working WITHIN
   each side's color family (e.g. a brighter/thicker red when engaged
   toward an enemy, a brighter/thicker blue when engaged toward a
   friendly), rather than collapsing engaged-vs-selected into a single
   flat color. Non-ship targets (station/asteroid/warp) have no side —
   keep their existing neutral treatment, only ships get the red/blue
   treatment.
3. Keep this consistent with (but not necessarily identical in exact
   hue to) the radar/HUD side-coloring work already landed this
   session (P372, `StationRenderer.FriendlyColor`/`EnemyColor` for
   stations) — check whether reusing those exact same two color
   constants makes sense for visual consistency across the HUD, or
   whether a red/blue specifically for THIS bracket (as literally
   requested) should be its own distinct pair. Use judgment; note
   which you chose and why in the report.

## Explicitly out of scope

- `LockBracketStyle` (the separate missile-lock reticle, grey/amber/
  red) — different system, already has its own real state-based color
  logic, don't touch it.
- P372's station/radar coloring — already correct, don't touch, just
  reference for consistency if relevant per scope item 3.
- Any per-side-unique coloring (P372's radar approach) — this task is
  specifically the simple universal red=enemy/blue=friendly binary
  Domingo asked for, not a third per-side-color scheme.

## Verification — real proof, not just build/test

- **Live proof, required**: real 2-side multiplayer session. Confirm
  the target-selection bracket is genuinely RED when locked onto an
  enemy ship and genuinely BLUE when locked onto a friendly/same-side
  ship, on both sides (so it's truly relative to each viewer, not tied
  to one hardcoded side) — i.e. side A sees side B's ship as red and
  their own teammate as blue; side B sees the mirror image. Confirm
  the engaged-vs-selected distinction still reads clearly within each
  color.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The friend/enemy target-bracket coloring, committed on `master`
   (not pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P376_TARGET_BRACKET_FRIEND_ENEMY_COLOR.md` in the repo
   root: the exact color values chosen and why, and the live
   verification performed from both sides' perspective.
