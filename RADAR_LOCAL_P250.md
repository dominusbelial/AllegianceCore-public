# Local Current-Sector Tactical Radar (P250)

## What this is

An **always-on local current-sector tactical radar** in the MonoGame client
(`src/Allegiance.ClientCore.MonoGame/`): every nearby **ship, station and warp**
in the player's CURRENT sector gets a small icon each frame — at its real
projected position when on screen, or clamped to the viewport edge in the
object's real direction when off screen — with a distance-based alpha fade.
It gives a pilot situational awareness of the local tactical picture without
hunting the 3D scene (Domingo's "gameplay sense" review pass, alongside P249's
whole-mission starmap, which is a separate feature and deliberately not rebuilt
here).

## Relationship to native — this is the real feature, not a radar disc

Native's is **not** a classic top-down radar circle. `WinTrek/radarimage.cpp`
(`RadarImage`/`RadarImageImpl`, 1482+72 lines) is an **on-screen 3D-viewport
bracket / edge-indicator system** — the same genre as a modern game's off-screen
contact markers. Every nearby object in the current sector (ships/stations/
missiles/mines/asteroids/warps) gets an icon in 3D-projected screen space
(`DrawImage3D`, additive); **on-screen** objects draw at their real projected
position, **off-screen** objects get their icon clamped to the viewport edge in
the object's real direction (`radiusBracket`/`distanceToEdge`,
radarimage.cpp:314-319, 315-410; the eye-space edge clamp WinTrek.cpp:7893-7923).
Distance-based alpha fade fades icons past `farRange` (radarimage.cpp:689-700).

This stage builds the **core mechanic** only: on-screen placement + edge-clamp +
per-type icons + distance fade. Text labels, LOD modes, importance-tiered
brightness, and missiles/mines/asteroids are named follow-ups (see Out of scope).

## Edge-clamp technique chosen (cited)

Implemented as a **clean 2-D edge-clamp against the screen rect**, equivalent to
native's eye-space clamp (the brief offered this option).

`Rendering/RadarProjection.cs` projects each object the established way this
port already does (subtract `cameraEye`, transform through `view*proj` — the
`ComputeLockBracket`/`CollectTargetCandidates` pattern):

- **On-screen test:** the real projected NDC lands inside [-1,1] → the icon is
  drawn at that exact projected pixel (native's `c_ucRadarOnScreenLarge` path,
  WinTrek.cpp:7868). Exact-screen-position correctness is pinned by the
  dead-centre ship landing at (480,360) = the true screen centre in the live
  proof.
- **Off-screen clamp:** the object's camera-space right/up direction (native's
  `TransformLocalToEye`, WinTrek.cpp:7882 — x right, y up) is projected at a
  forced in-front depth so its **lateral signs stay correct even when the object
  is behind the eye**, then the direction is normalised so the dominant axis
  lands on the inset viewport-rect boundary — the classic square edge-bracket
  (same information native clamps to the image rect, WinTrek.cpp:7893-7923).
  An inset (14 px) keeps a centred icon fully on screen (native's border gutter,
  radarimage.cpp:318-319). `RadarBracketLayout.Resolve` holds this pure
  on-screen/clamp math (headless-testable).

Why this over native's literal `radiusBracket`/`distanceToEdge` machinery:
native computes those from a screen-radius + a distance-to-edge, tuned around
icon *size*; this port's icons are fixed-size markers, so a direction-based edge
clamp is simpler and equally faithful to the observable behaviour (marker pinned
to the edge in the object's real direction), and it slots directly into the
existing `view*proj` projection.

## Icon scheme

Flat single detail level (per brief): object type is told apart by a simple
**shape + colour**, drawn procedurally with the 1×1 white pixel (StarmapRenderer
pattern):

| Type | Shape | Colour |
|------|-------|--------|
| Ship | small filled square | teal/cyan |
| Station | filled dot + outer ring | gold/orange |
| Warp | two concentric rings | violet/magenta |

## Distance fade — tuned live, not copied

Native: `closeRange=100`, `farRange=1000` (radarimage.cpp:689-700). Copied
blindly those numbers would be wrong for this port: **live measurement** of the
real m2xspiderw map (Ganymede sector, id 1050 — the spawn sector) put its **4
real warps at 3129–5714 units from the spawn point**. Native's 1000 — and a
first-pass 2000 — would hide **every** real warp in the starter sector, so the
radar would read empty exactly where a player needs it.

Final tuning: **`CloseRange=300`** (full opacity inside ~300 u) and
**`FarRange=4000`** (fully transparent at/past), a linear ramp between — exposed
as `RadarBracketLayout.CloseRange`/`FarRange`. 4000 keeps the nearest real gate
(~3130) faintly visible while fading the rest — a useful local tactical horizon
(~33 s of flight at scout speed ~120 u/s). `RadarBracketLayout.Opacity` is the
pure linear fade. This is documented as display tuning derived from the live
measurement, not blind native copying.

## Always-on (faithful default)

Native's radar is part of the standard HUD with **no separate toggle**, so this
is always-on. It does not structurally conflict with the P211 cockpit HUD: the
P241 gauges occupy the corners/centre while these small icons sit at object
positions throughout the viewport. (The brief's "toggle unless it conflicts"
escape hatch was assessed and not needed.)

## Files

- `src/.../Rendering/RadarBracketLayout.cs` (new) — pure on-screen/clamp/fade
  math + the fade-range constants. Headless-testable.
- `src/.../Rendering/RadarProjection.cs` (new) — world → screen projection
  shared by the game and the live harness (production path).
- `src/.../Rendering/RadarBracketRenderer.cs` (new) — draws the icons/edge
  brackets (own SpriteBatch, StarmapRenderer pattern).
- `src/.../Game1.cs` — `_radarRenderer` + `DrawRadar()` gather/render wiring,
  called in `Draw()` over the 3D scene.
- `tests/.../RadarBracketLayoutTests.cs` (new) — 9 pure tests.

## Gather — reuses existing per-sector filtering, no new plumbing

`Game1.DrawRadar` locks the Gate and walks `world.Ships` (other, living craft in
my sector), `world.Stations` (`ClusterId == mySector`) and `world.Warps`
(`ClusterId == mySector`) — the exact per-sector filtering the P218 asteroid /
P227 warp / P234 missile draw loops already do. Confirmed: no new wire or
`ClientWorld` plumbing; the client already holds per-sector object data. Own
ship's sector/position come from its existing `ServerShipUpdate`.

## Live proof (`.verify_p250/`)

A `/tmp/radar_harness` MiniGame (the P207/P218/P227 offscreen-screenshot
technique, rendered synchronously in LoadContent) boots a real in-process
`Allegiance.Server` on m2xspiderw + a real `ConnectionSession`, then renders the
REAL `RadarBracketRenderer` through the REAL `RadarProjection`/
`RadarBracketLayout`.

**Part B (`p250_partB.png`) — controlled deterministic contacts** (numeric +
vision confirmed):

| contact | world pos | dist | placement | px | alpha |
|---|---|---|---|---|---|
| ship_center | (0,0,-600) | 600 | OnScreen | **(480.0,360.0)** | 0.92 |
| ship_near | (0,180,-400) | 439 | OnScreen upper | (480.0,79.4) | 0.96 |
| ship_far | (0,-250,-3200) | 3210 | OnScreen lower | (480.0,408.7) | 0.21 |
| station_low | (500,-400,-900) | 1105 | OnScreen lower-right | (826.4,637.1) | 0.78 |
| station_behind_left | (-600,200,500) | 806 | **EdgeClamped LEFT** | (14.0,206.2) | 0.86 |
| warp_right | (700,100,500) | 867 | **EdgeClamped RIGHT** | (946.0,294.1) | 0.85 |

- **On-screen correctness:** `ship_center` lands at exactly (480,360) = the
  true screen centre (crosshair) — the icon is at the object's real projected
  position, vision-confirmed (a teal square dead-centre on the crosshair).
- **Edge clamp / off-screen correctness:** the station BEHIND the player and to
  the LEFT clamps to the **LEFT** edge (px.x=14=inset), the warp to the RIGHT to
  the **RIGHT** edge (px.x=946=W-inset) — confirmed by eye as gold dot+ring on
  the left border and purple double-ring on the right border. This is the exact
  "off-screen contact shows its bracket clamped to the correct edge in the
  right direction" requirement (incl. a genuinely-behind object).
- **Distance fade across two distances:** ship_near alpha 0.96 vs ship_far
  alpha 0.21 — two teal squares in the same frame, upper one vivid, lower one
  clearly faint (vision-confirmed). A third data point (station_low 0.78) plus
  Part A round it out.

**Part A (`p250_partA_real_warps.png`) — real map data** for authenticity: the 4
REAL Ganymede warps projected relative to the real ship spawn.

| warp | dist | placement | alpha |
|---|---|---|---|
| Bug Nebula | 3129 | EdgeClamped bottom | **0.24 (visible)** |
| Terra | 4819 | EdgeClamped | 0.00 (faded) |
| Rigel | 4516 | EdgeClamped | 0.00 (faded) |
| Plato | 5714 | EdgeClamped | 0.00 (faded) |

Vision-confirmed: exactly ONE faint purple ring pinned to the bottom edge (the
3129-u Bug Nebula), the other three absent — the fade + edge-clamp working
against real wire data (and the concrete justification for `FarRange=4000`).

**Must-fail proven (unit):** neutralising the edge-clamp direction (forcing the
marker to the right regardless of the object's real direction) turned the 3
directional tests red (`BehindLeft_ClampsToLeftEdge_NotRight`,
`BehindAbove_ClampsToTopEdge`, `DominantAxis_SelectsTheEdge`), then green after
revert.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **267 pass** (baseline 258 + 9 new
  `RadarBracketLayoutTests`).
- `Allegiance.ClientCore.Tests`: **83 pass.**
- `Allegiance.Core.Tests`: baseline **3066** — only the pre-existing documented
  `LiveTurretGunnerBindingTests` timing flake expected (this stage is
  client-only, zero Core/Server references).

## Out of scope / follow-up candidates (NOT built — named for the future)

- **Missiles / mines / asteroids as radar contacts** — the brief's explicit
  scope boundary; native draws all six types (radarimage.cpp:736-981), but this
  stage does ships/stations/warps. Asteroids are trivially includable (same
  `ClusterId == mySector` filter as P218, already a `DataAsteroidIGC`);
  missiles/mines need their per-sector gather loop (P234-style) + a mesh
  already rendered — a contained follow-up. Not built to honour the scope bar.
- **Text labels (name/range/shield/hull%)** — native labels "important" objects
  at full brightness (radarimage.h:12-16); this stage is flat, no text.
- **LOD modes** — native `c_rlAll`/`c_rlDefault`/`c_rlTarget`/`c_rlMinimal`
  (radarimage.h:39-44) gating detail; not built.
- **Importance-tiered brightness** — native highlights target/accepted/threat
  contacts brighter than the rest; not built.
- **Screen-radius-proportional icon scaling** (native `radiusObject`/
  `radiusBracket`, radarimage.cpp:307-314) — fixed-size markers here.
- **The whole-mission starmap overlay** is P249 (already landed) — none of it
  rebuilt here.

No native source was modified (read-only).
