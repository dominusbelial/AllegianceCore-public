# P372 — radar/HUD contacts have no friend/enemy color (native colors every blip by real side)

## Why this task exists

Domingo asked live: "why is it so difficult to see a friendly ship?
[...] it should just be visible right? we dont have anything on the
ui for that?" Investigated against both this port and native source.

**Confirmed real gap.** This port has TWO places a ship could carry a
friend/enemy visual cue, and neither does:

1. **3D-view ship rendering** (`Game1.cs`): stations get a real
   friend/enemy tint (`Rendering.StationRenderer.FriendlyColor`/
   `EnemyColor`, teal vs orange, based on `(world.MySideId ?? -1) ==
   sSide`) — but ships get NO equivalent tint anywhere. A ship's only
   identity signal is its civ-specific art (not something a player has
   memorized) plus a name label that only appears once you've already
   target-locked it.
2. **Radar bracket overlay** (`Rendering/RadarBracketRenderer.cs`,
   P250) — draws edge-clamped blips for off-screen ships/stations/
   warps, but per its own doc comment "Object type is told apart by a
   simple shape + colour": it colors purely by TYPE
   (`ShipColour = new(0.28f, 0.85f, 0.85f)`, one fixed teal for every
   ship, `StationColour` gold, `WarpColour` violet) — no side/team
   distinction at all.

**Native does have a real, explicit mechanism for this** — confirmed
by direct read, not inference. `WinTrek/radarimage.cpp:678-684`:

```cpp
// Color for the side
IsideIGC* pside = pmodel->GetSide();
Color color = pside
              ? ((maskBrackets & c_maskFlash) ? Color::Red() : pside->GetColor())
              : s_colorNeutral;
```

Every native radar blip — ships, stations, everything with a side —
is colored by that contact's REAL side/team color (`pside->GetColor()`),
not a fixed per-type color. This is the exact same "decode
`pside->GetColor()` as a real RGBA color" mechanism already ported
once this session for P363's side-colored ship motion trail
(`shipIGC.cpp:149`, `trekigc.cpp:4419`) — the plumbing to get a real
side's color already exists in this port, just not wired into the
radar bracket renderer.

Also note (same native file, ~line 743-866): native additionally
special-cases `pside == psideMine` (own side) and
`pside->AlliedSides(pside, psideMine)` (allied side) in several
nearby label/visibility branches — this port has no alliance concept
(an already-established, accepted simplification elsewhere in the
project), so "allied side" collapses to "own side only", matching the
project's existing convention. Don't build a new alliance concept for
this task.

## Scope

1. **Wire real per-side color into `RadarBracketRenderer`'s ship
   blips**, replacing the fixed `ShipColour` for the ship case with
   the contact's real side color — reuse whatever P363 already built
   to decode `pside->GetColor()` into a usable RGBA `Color`, don't
   re-derive it. Station and warp blips can keep their existing
   type-based colors (stations already have their own friend/enemy
   tint in the 3D view; warps have no side at all) unless you find
   clear evidence native also side-colors those — check briefly, but
   don't over-scope if it doesn't.
2. **Also apply a real side-based tint to ships in the main 3D-view
   render** (not just the radar bracket), mirroring exactly how
   `StationRenderer.FriendlyColor`/`EnemyColor` already works for
   stations (`(world.MySideId ?? -1) == sSide ? Friendly : Enemy`) —
   check whether native's ship rendering (not just the radar) does
   real per-side tinting/marking too (search `WinTrek/` for how a
   ship's OWN hull rendering, not just its radar blip, might carry a
   side indicator — a colored running light, a hull decal, or similar)
   before deciding exactly how to apply it in 3D (a full hull recolor
   would clash with the real per-civ art already confirmed correct
   this session — don't do that; something additive like a corner
   marker, nameplate tint, or bracket color is more likely the right
   shape, but verify against native rather than guessing).
3. Distinguish real side identity, not just "friendly vs everyone
   else" — since this map has exactly 2 sides that's equivalent here,
   but use the real side id / real side color lookup (not a boolean),
   so a future 3+ side mission would show genuinely different colors
   per side, matching native's own per-side (not binary) color model.

## Explicitly out of scope

- Building a new alliance/diplomacy concept — out of scope, matches
  existing project convention.
- Re-doing station coloring — already correct, don't touch.
- Re-deriving the `pside->GetColor()` decode mechanism — reuse P363's,
  don't duplicate it.
- P368-P371 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real multi-client session, 2+ real sides.
  Confirm a friendly ship's radar blip (both on-screen and off-screen/
  edge-clamped) and/or 3D-view indicator visibly differs in color from
  an enemy ship's, using each side's real color (not a hardcoded
  friend=X/enemy=Y binary if the mission has more than 2 sides to test
  with — note in the report if this mission only has 2 sides available
  to verify with, that's fine, just say so plainly rather than
  overclaiming n-side verification you didn't actually do).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The side-colored radar/HUD contact fix, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P372_RADAR_SIDE_COLOR.md` in the repo root: what native
   ship-hull-level side indicator (if any) was found beyond the radar
   blip, what was built, and the live verification performed (describe
   the actual colors observed for each real side).
