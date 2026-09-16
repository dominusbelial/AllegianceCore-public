# P227 — render warps as a procedural swirling vortex in the MonoGame client

## Summary

The playable MonoGame client now draws warp gates. Previously `ClientWorld.Warps`
was read only for targeting/wayfinding and never appeared in `Draw()` — the
exact gap Domingo hit ("i cant see anything, no warp gate, or asteroid field";
asteroids landed in P218, warps never did). P227 adds a deterministic,
procedurally-built warp funnel/vortex mesh (`Rendering/WarpMeshFactory.cs`), a
MonoGame-facing `Rendering/WarpRenderer.cs` that builds one shared buffer,
constantly swirls it about the warp's own forward axis, and colours it with a
warm dark-rim → amber → white-hot-tip gradient, plus a sector-filtered draw
loop in `Game1.Draw()` that scales each gate by its real `Radius`. Purely
additive client rendering — zero server / `Allegiance.Core` wire change.

## Scope decision (why a procedural vortex, no texture)

Native source confirms warps did have a real rendered effect, gated under the
same show/hide machinery as ships/asteroids/stations
(`WinTrek.cpp:8039`:
```
case OT_warp:
    visibleF &= m_bShowAlephs;
```
). The native model was texture-mapped from a per-warp texture name
(`warpIGC.cpp:33` `LoadWarp(dataWarp->warpDef.textureName)`), but no such asset
exists in this checkout's artwork tree (the same two-tree situation asteroids
and ships were already in for P218). So — per Domingo's scope cut, mirroring
`RockMeshFactory` — the warp is generated procedurally instead.

The shape is not a static ring but a **funnel/vortex**, matching the original
game's own tutorial description (`training/mission4.cpp:494`):

> *"like a whirlpool in space... a big swirling brown vortex, with a bright spot in the center."*

The swirl is built into the geometry (a helix twist per ring) plus the
per-vertex colour gradient; the renderer adds a continuous time-driven spin.

## What real data feeds each instance

Each warp instance is placed/sized/oriented from the REAL wire record
`DataWarpIGC` already flowing into `ClientWorld.Warps` via `ExportWarp`
(`ClientWorld.cs:46-47`, one-shot per warp at join):
- `Position` (`float[3]`) — world position, via the existing `Vec(float[])`
  helper (`Game1.cs:1696`).
- `Forward` (`float[3]`) — the warp's forward axis. A warp carries **no `Up`
  vector** — only `Forward` plus a `Rotation` roll (the `DataWarpIGC` layout,
  `igc.h:2506`), unlike `DataAsteroidIGC` which has `Up`. Because a warp gate
  is rotationally symmetric about its forward, the `Orientation` is built from
  the single-argument `o.Set(Vec(wp.Forward))` (`Orientation.cs:124`, the same
  eject-branch precedent) — no fabricated `Up` is forced in, and the swirl
  animation spins about this same forward axis.
- `Radius` (short, world units) — the gate's true physical size (all
  `m2xspiderw.igc` warps are `radius=100`, measured from the map corpus).
  Applied as a uniform scale per instance so the rim diameter reads at true
  world size — warps are "very large, many ships could cross at the same time".
- `ClusterId` (short) — which sector the gate lives in, the same concept as
  `Asteroid.ClusterId` (P218) and `ShipEntity.SectorId`.

## The sector filter (the whole point, mirroring P218)

`ClientWorld.Warps` is populated from every `OT_warp` on the map regardless of
sector (fine for targeting, which frustum-projects everything; wrong for a
draw loop). The draw loop filters to `warp.ClusterId == mySector` where the
player's own entity is resolved from `world.Ships[world.MyShipId]` — the same
pattern as the P218 asteroid loop and `CollectTargetCandidates`. Without it a
draw loop would render all 44 warps of the "Combat Demo" map
(`m2xspiderw.igc`) at once.

## Implementation

### `Rendering/WarpMeshFactory.cs` (pure logic, no MonoGame on the surface)
Returns a `WarpMesh(Positions, Colors, Indices)` as plain `float[]`/`int[]`
(the `Flight/*` "no MonoGame on the surface" discipline, same as
`RockMesh`). Deterministic: only `MathF`, no `System.Random`, no time, so
same params → byte-identical output across calls (the `Starfield`/
`RockMeshFactory` fixed-seed discipline).
- Geometry: `ringCount` concentric rings along local `+Z` (forward), each of
  `segments` vertices around the circumference. Ring radius tapers from
  `rimRadius` (unit) at `z=0` to ~0 at `z=depth` via `(1−t)^taper`; each
  successive ring is twisted further around the axis by an accumulated
  `spiralTightness` phase — so the **static mesh itself reads as swirling**
  (a "drain" shape) even before animation.
- Colour: piecewise-linear warm gradient by depth `t` — dark brown rim
  (t=0) → saturated amber (t=0.6) → white-hot tip (t=1) — overlaid with
  `SpiralArms=3` brighter wedges whose phase rides the same per-ring helix
  twist, so the vortex has visible structure for the animation to turn.
- Grid-triangulated between consecutive rings (both winding orders; the
  renderer disables back-face culling so the hollow funnel's inner wall is
  visible).

### `Rendering/WarpRenderer.cs` (MonoGame half)
Mirrors `AsteroidRenderer`: the constructor builds ONE shared
`VertexBuffer`/`IndexBuffer` from the factory output (the "generated, not
loaded" model — no content-pipeline asset) and draws per instance. Uses
`VertexPositionColor` + `BasicEffect { VertexColorEnabled = true }` — simpler
than lighting for the self-lit glow look, and it passes the factory's warm
gradient straight to the rasterizer. Two deliberate departures from
`AsteroidRenderer`:
- **Constant swirl (Domingo's explicit direction):** `Draw(world, view,
  projection, deltaSeconds)` accumulates real elapsed time internally
  (`_elapsed += dt`) and applies a continuous rotation about the mesh's local
  forward axis (`Matrix.CreateRotationZ(angle)`, `angle = _elapsed × 0.9 rad/s`)
  BEFORE the instance orientation — so the vortex spins about the warp's own
  forward axis forever, never a one-shot. The `CreateRotationZ * world`
  ordering applies the spin in the funnel's local frame, then the orientation
  rotates that spun frame into the world.
- **CullNone:** the funnel is hollow — viewers on the rim side must see its
  inner wall and viewers on the tip side its outer wall — so back-face culling
  is disabled (unlike the solid-rock argument that kept it for `AsteroidRenderer`).

### `Game1.cs` `Draw()` (alongside the P218 asteroid loop)
`_warpRenderer` built once in `LoadContent`. Each frame (Flying): gather the
own-sector gates under the `Gate` lock, release it, then draw each via
`wRot * Matrix.CreateScale(warp.Radius) * Matrix.CreateTranslation(wXnaPos - cameraEye)`
using the same `view`/`projection`/`cameraEye` already built for ships that
frame. The real `Radius` is baked into the world scale (so a `radius=100` gate
has a 100-unit rim); `gameTime.ElapsedGameTime.TotalSeconds` drives the swirl.

## Out of scope (untouched, as briefed)
- No server / `Allegiance.Core` wire change; warp pose/radius/cluster already
  travel the wire.
- The existing warp-as-fallback-target autopilot logic (commit `f62e46d`) is
  untouched — pure rendering, additive only.
- Non-frustum wayfinding aids (compass / nearest-object HUD pointer) — a
  separate gap, not this task.
- Animation/particles for warp *transit* itself, and projectile/missile/mine/
  chaff visual rendering — both out of scope.
- `AsteroidRenderer`/`RockMeshFactory`/`ShipRenderer` untouched except as
  pattern reference; native `~/Development/Allegiance` read-only.

## Verification

### Build
`dotnet build AllegianceCore.slnx` → 0 warnings, 0 errors, whole solution.

### Unit tests
`WarpMeshFactoryTests` (7 new, MonoGame.Tests 138 → **145**, all green every
run): real-mesh shape, indices-in-range, same-params byte-identical
determinism, different-params different-shape, rim→tip taper along the forward
axis, dark-rim → white-hot-tip gradient (with absolute "rim dark / tip bright"
bars — proven to discriminate: neutralizing the gradient makes it red), and
spiral-arm azimuthal variation (proven to discriminate: zeroing
`ArmAmplitude` makes it red).

Full suite, run repeatedly against the current baseline:
- `Allegiance.Core.Tests`: **3044** total / 3043 passing every run. The only
  interruptions are the two documented pre-existing machine-state flakes —
  `LiveTurretGunnerBindingTests` (fails persistently in isolation, known
  pre-existing; confirmed) and
  `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` (passes
  in isolation, confirmed). My changes are MonoGame-project-only and touch no
  `Allegiance.Core` symbol.
- `Allegiance.ClientCore.MonoGame.Tests`: **145** passing (all 7 new warp
  tests green, no failures).
- `Allegiance.ClientCore.Tests`: **55** passing.

### Live screenshot verification (real server + real client, offscreen RT)
Ran a real `Allegiance.Server` on `m2xspiderw.igc` ("Combat Demo", 13 sectors,
**44 warps**, 134 asteroids per its own `[boot]` log) and connected the real
MonoGame client on `:0`, capturing offscreen `RenderTarget2D` frames at several
wall-clock ticks (the session-standard P207 technique — the Wayland backbuffer
is unreliable). Frames vision-checked:

- **Positive — the warp gate renders as a vortex.** A temporary spawn override
  (reverted before commit; `Server.cs` diff is empty) parked the test ship near
  Ganymede's "Terra" warp (pos `(-2880,-2320,-58.5)`, radius 100) and the
  diagnostic logged `totalWarps=44 inSector=4 drawn=4` — all four Ganymede
  gates drawn. In the captured frames the gate appears as a large **amber
  funnel/vortex with a wide elliptical opening converging to a bright
  white-hot point**, dark-brown at the outer rim → amber in the middle →
  white-hot at the core — the exact "whirlpool... with a bright spot in the
  center" look. Viewed obliquely (cockpit camera so the local hull isn't in
  frame) it reads unmistakably as a **hollow swirling tunnel/vortex, not a
  solid ball** — the classic warp-gate signature.
- **Constantly swirling.** Three captures at ~17.5s / 19.5s / 21.5s (the ship
  cruises at 0.5×120 for the default Adv Miner) differ in the central funnel
  region (mean-abs pixel diff ≈ 3.3/255 over the 4s between the first and last
  frame), consistent with the elapsed-time-driven rotation (`0.9 rad/s` ≈ 3.6
  rad over that window); the swirl itself is time-driven by construction in
  `WarpRenderer.Draw`, never a one-shot.
- **Negative / sector filter.** `totalWarps=44` vs `inSector=4` proves the 40
  warps in the other 12 sectors are NOT drawn (only the 4 in the player's
  sector 1050 Ganymede are), matching P218's own positive/negative shape.

(The live run used temporary, reverted-before-commit spawn override in
`Server.cs` and a capture diagnostic in `Game1.cs` — shipped code is unchanged;
`git diff` for `Server.cs` is empty.)

## Commit
Single P227 commit on `master`, message prefixed `P227:`, not pushed (Domingo
pushes). Working tree clean.
