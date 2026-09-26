# P372 — radar/HUD contacts get real friend/enemy (per-side) colour

Native's 3D-viewport bracket/blip overlay colours every side-bearing contact by
that contact's REAL side colour — `radarimage.cpp:682-685`:

```cpp
// Color for the side
IsideIGC* pside = pmodel->GetSide();
Color color = pside
              ? ((maskBrackets & c_maskFlash) ? Color::Red() : pside->GetColor())
              : s_colorNeutral;
```

Before this task the port had two possible places a ship could carry a
friend/foe cue and neither did: the 3D ship render (no tint — a ship's only
identity was its civ art plus a name label that only appears once you already
target-lock it) and the radar bracket overlay (`RadarBracketRenderer`, P250),
which coloured purely by TYPE (one fixed teal for every ship). This task wires
the real per-side colour into both.

## What native actually does (verified by direct read, not inference)

1. **Radar / 3D-viewport brackets — native DOES side-colour them.** Every blip
   (ship, station, probe, mine, anything with a side) is drawn in
   `pside->GetColor()`; only objects with NO side fall back to the neutral grey.
   This is the exact decode (`pside->GetColor()` → real RGBA) P363 already
   ported for the side-coloured motion trail (`shipIGC.cpp:149`,
   `trekigc.cpp:4419`, `Side.ColorR..A` seeded from native's fixed side palette).
   The plumbing — `ClientWorld.TryGetSideColor` — already existed; it was just
   not wired into the radar bracket renderer.

2. **Ship HULL rendering — native does NOT per-side tint it.** I searched the
   native client for any side-derived colour consumed while drawing a ship's
   own hull (running light, decal, mesh tint): there is none. The only
   `GetSide()->GetColor()` consumers are the trail (`SetTrailColor`), the radar
   bracket system (`radarimage.cpp:682`), the sector-map icons, scoreboard /
   team panes, and console/HUD text — never the 3D ship mesh. A native ship's
   hull is pure civ art. So the honest 3D-view answer is: native's ONLY per-side
   in-world indicator is the bracket/blip overlay (which this port's
   `RadarBracketRenderer` already mirrors). Consequently a full ship-hull
   recolor is both unfaithful to native and would clash with this port's real,
   already-confirmed per-civ art (P287/P351-356) — the additive marker route the
   brief anticipated is the right shape, and native's bracket-only model backs
   that up.

Native also special-cases `pside == psideMine` / `AlliedSides(...)` in adjacent
label/visibility branches (radarimage.cpp ~743-866). This port has no alliance
concept (established project convention), so "allied" collapses to "own side"
and is out of scope — nothing new built here.

## What was built

### Scope 1 — radar bracket ship blips use the contact's real side colour

`Rendering/RadarBracketRenderer.cs`:

- `RadarContact` gained a nullable `Color? SideColour` (trailing optional
  positional param — existing constructions still compile unchanged).
- New pure, GPU-free resolver `RadarBracketRenderer.ContactColour(type,
  sideColour)` returns the supplied side colour when present, else the
  type-based palette colour. Applied to all contact kinds, but only ships
  supply a side colour (below).
- `Game1.DrawRadar` resolves each ship's real side colour via
  `ClientWorld.TryGetSideColor(ship.SideId, …)` — REUSING P363's decode, not
  re-deriving it — and passes it into the contact. Ships on side 0 (Gold) and
  side 1 (Blue) therefore draw in their own real side colour, on-screen AND
  edge-clamped (the edge bracket inherits the same colour). Station blips keep
  their type-gold and warp blips type-violet, per the brief's permissive scope.

Because the colour comes from the real side id / side-roster lookup (not a
friendly-vs-everyone boolean), a future 3+-side mission shows a genuinely
different colour per side, matching native's per-side (not binary) colour model.
On this 2-team map it reduces to friendly/enemy, which is exactly the
equivalence the brief allows.

### Scope 2 — additive per-side in-world ship tag in the 3D view

New `Rendering/ShipSideTagRenderer.cs`: a small side-coloured upward chevron
("team tag") drawn a fixed offset above each ON-SCREEN ship's projected centre,
in that ship's real side colour. This is the port's 3D-view hull-side indicator:
it is additive (never recolours the real civ art, consistent with the native
finding above), always camera-facing, and distinct from the centred radar square
(so it reads as a floating tag glued to the hull). Off-screen ships are already
covered by the side-coloured edge bracket, so only on-screen ships draw a tag.
`Game1.DrawRadar` gathers tag data in the same locked pass that builds the
contacts (reusing the same projection + side-colour resolution) and draws them
right after the brackets.

`RadarContactType.Ship`'s side colour and the tag both resolve through the SAME
`TryGetSideColor` roster lookup, so Scope 1 and Scope 2 always agree on a ship's
colour.

### Tests

`tests/…/RadarBracketSideColorTests.cs` (6, GPU-free): ship+real side → side
colour; ship+null → teal fallback; station/warp+null keep type hues; supplied
side colour honoured for any type; two sides resolve to genuinely different
colours (not a boolean); `RadarContact` carries `SideColour` through.

## Live verification — real multi-client, 2 real sides

Harness: `.verify_p372/` (real in-process `Allegiance.Server`, `XXV_simple.igc`
`teams:2`, two real `ConnectionSession` peers seated on opposite sides; the
friendly/enemy assignment is taken from the peer's real `MySideId`, not assumed).
Real `SideRoster(211)` colours arrive over the wire:
`side 0 'Alpha' = (0.737, 0.627, 0.000)` (Gold 188/160/0) and
`side 1 'Bravo' = (0.000, 0.541, 0.851)` (Blue 0/138/217). Each frame projects
the real server ships through the REAL `RadarProjection`, resolves each ship's
real side colour via the REAL `ClientWorld.TryGetSideColor`, and renders the REAL
`RadarBracketRenderer` + REAL `ShipSideTagRenderer` offscreen (960x640), then
classifies gold-dominant vs blue-dominant pixels.

Results (`frames/onscreen_both.png`, `frames/onscreen_tags_only.png`,
`frames/edge_clamp.png`, `p372_run.log`):

- **onscreen_both** — friendly ship (side 0) OnScreen drawn **Gold**
  `{R:188 G:160 B:0}`; enemy ship (side 1) OnScreen drawn **Blue**
  `{R:0 G:138 B:217}`. `friendly-colour px=105, enemy-colour px=105` — both > 0:
  two different side colours on screen at once. (Vision: a gold square+caret and
  a blue square+caret, clearly different colours.)
- **onscreen_tags_only** — the same two ships drawn with ONLY the new
  `ShipSideTagRenderer`: a **gold** chevron and a **blue** chevron (two distinct
  hull tags in each side's real colour).
- **edge_clamp** — friendly moved to centre (**Gold**, on-screen) while the
  enemy is pushed far off the right side: its EDGE-CLAMPED bracket renders at the
  viewport edge in the enemy side colour **Blue**. `friendly-colour px=105
  (centre), enemy-colour px=64 (edge bracket)` — both > 0. (Vision: blue square
  at the screen edge, gold marker centred.)

This covers the required live criteria: a friendly ship's blip differs in colour
from an enemy's on-screen, and the off-screen/edge-clamped case carries the same
per-side colour, using each side's REAL roster colour (not a hardcoded
friend=X/enemy=Y binary). **This mission (`XXV_simple`, teams:2) has exactly 2
sides, so the per-side variance was verified across 2 sides only — I am stating
that plainly rather than overclaiming n&gt;2 verification.**

## Build / tests

- Whole-solution build (Debug and Release): **0 warnings, 0 errors**.
- P372 unit tests: 6/6 pass.
- Full `dotnet test`: only the pre-existing documented live-wire timing flakes
  are expected (see run logs; none touch the radar/side-colour/tag paths).

## Repository note

Committed on `master` per the landing workflow (not pushed — Domingo pushes).
A pre-existing unstaged edit to `src/Allegiance.Bot/Bot.cs` was already in the
working tree before this task; it is unrelated and was left out of this commit.
