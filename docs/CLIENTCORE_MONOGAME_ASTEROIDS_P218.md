# P218 — render asteroids as a procedural rock mesh

## Summary

The playable MonoGame client now draws real asteroids. Previously asteroids
were read only for autopilot soft-lock targeting (`CollectTargetCandidates`,
`Game1.cs:1273`) and never appeared in `Draw()`. P218 adds a deterministic,
procedurally-generated rock mesh (`Rendering/RockMeshFactory.cs`), a
MonoGame-facing `Rendering/AsteroidRenderer.cs` that builds one shared
buffer and shades it per-instance, and a sector-filtered draw loop in
`Game1.Draw()` that scales each rock by its real `Radius`. Purely additive
client rendering — zero server / `Allegiance.Core` wire change.

## Scope decision (why a procedural mesh)

No real asteroid 3D asset exists in the artwork tree
(`/home/domingo/Development/Artwork` only has generic UI-primitive `.x`
files — `globe*.x`/`sphere.x`/`cube.x`). Domingo's explicit session choice:
a quick procedural rock mesh, not another Meshy-generation pipeline
round-trip like the ship model needed. Every rock on the map reuses this ONE
shared mesh, scaled per instance by its real radius — the same "one shared
mesh" scope cut already accepted for ships (every ship renders the same Bios
Scout hull regardless of model).

## What real data feeds each instance

Each asteroid instance is placed/sized from the REAL wire record
`DataAsteroidIGC` already flowing into `ClientWorld.Asteroids` via
`ExportAsteroid` (one-shot per asteroid at join; asteroids are static):
- `Position`/`Up`/`Forward` (`float[3]`) — world pose. Converted with the
  existing `Vec(float[])` helper (`Game1.cs:1438`) and an `Orientation` via
  `o.Set(Vec(ast.Forward), Vec(ast.Up))` (`Orientation.Set`, `Orientation.cs:89`).
- `Radius` (short, world units) — the rock's real physical size
  (`Asteroid.cs:85-88`, the sim's own mining-proximity gate reads this same
  value). Applied as a uniform scale per instance so bigger asteroids look
  bigger.
- `ClusterId` (short) — which sector the rock lives in, same concept as
  `ShipEntity.SectorId` (set from `SET_CLUSTER`/`SHIP_STATUS`,
  `ClientWorld.cs:164,180`).

## The sector filter (the whole point)

`ClientWorld.Asteroids` is NOT sector-filtered (fine for targeting, which
frustum-projects everything; wrong for a draw loop). The draw loop filters to
`ast.ClusterId == myShipEntity.SectorId` where the player's own entity is
resolved from `world.Ships[world.MyShipId]` (the same read pattern as
`PickNearestTarget`/`CollectTargetCandidates`). Without this, a draw loop
would try to render all 134 rocks of the "Combat Demo" map (`m2xspiderw.igc`)
regardless of which sector the camera is in.

KEY MAP FACT measured directly (via the port's `MissionMap.Load`,
`Model/MissionMap.cs`): although `m2xspiderw.igc` has 13 clusters, ALL 134 of
its asteroids live in a SINGLE cluster — id `1052 "Alderan"`. The ship spawns
in `Sectors[0]` = `1050 "Ganymede"` (`SpawnDefaultShip`, `Server.cs:5880`),
which has ZERO asteroids. This made the filter both critical and cleanly
demonstrable in both directions (below).

## Implementation

### `Rendering/RockMeshFactory.cs` (pure logic, no MonoGame on the surface)
- Unit icosahedron → `subdivisions` (default 2) × midpoint-subdivide with an
  unordered-edge cache so adjacent triangles share split vertices (rough rock,
  not a smooth ball).
- Each vertex displaced radially by a deterministic per-vertex integer hash
  seeded from a fixed constant (`DefaultSeed = 81737`) — no `System.Random`,
  no time, so the shape is byte-identical across runs/calls (the same
  fixed-seed discipline as `Starfield`'s `seed` param, `Starfield.cs:39-40`).
- Returns `RockMesh(Positions, Normals, Indices)` as plain `float[]`/`int[]`
  — the `Flight/*` "no MonoGame on the surface" discipline (see removed
  `RemoteShipPredictor`, commit `a8b54af`), so it is unit-testable without a
  device.

### `Rendering/AsteroidRenderer.cs` (MonoGame half)
- Constructor builds ONE `VertexBuffer`/`IndexBuffer` from the shared mesh
  (like `Starfield`, "generated, not loaded", `Starfield.cs:31-32`).
- `Draw(world, view, projection)` renders it with a `BasicEffect`
  (`EnableDefaultLighting()` for cheap per-vertex-normal shading) — the same
  World/View/Projection/lighting pattern as `ShipRenderer.Draw`
  (`ShipRenderer.cs:48-64`). Flat gray-brown rock tint.
- Deliberately NO `ShipRenderer`'s `NoseAlignmentCorrection`/`CullNone`
  special cases — those exist only for the Meshy/Blender FBX winding/axis
  quirks; this procedurally-controlled mesh uses normal back-face culling.

### `Game1.cs` `Draw()` (alongside the ship loop)
- `_asteroidRenderer` built once in `LoadContent`.
- Each frame (Flying): gather the own-sector rocks under the `Gate` lock,
  release it, then draw each via
  `rRot * Matrix.CreateScale(rRadius) * Matrix.CreateTranslation(rXnaPos - cameraEye)`
  using the SAME `view`/`projection`/`cameraEye` already built for ships that
  frame (the camera matrices are not rebuilt). Uniform radius scale commutes
  with rotation, and the translation is last — preserving the P207
  camera-relative convention.

## Out of scope (untouched, as briefed)
- No server / `Allegiance.Core` wire change; asteroid pose/radius/cluster
  already travels the wire.
- `ShipRenderer.cs`, `Flight/*.cs`, `ChaseCamera.cs`/`CockpitCamera.cs`,
  `Starfield.cs` — unchanged, additive only.
- Mining depletion visuals, per-civ/region variety, destructible rocks,
  station rendering.
- `CollectTargetCandidates`/`PickNearestTarget`/`CycleTarget` targeting
  logic (commits `8aeb3d2`/`f8e245d`, unrelated to drawing).

## Verification

### Build
`dotnet build AllegianceCore.slnx` → 0 warnings, 0 errors, whole solution.

### Unit tests
`dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` run 3× → all pass
every run. Suite 115 → **121** (6 new `RockMeshFactoryTests`: real-mesh
shape, indices-in-range, same-seed byte-identical determinism, different-seed
different-shape, subdivision monotonicity, displaced-shell radius bounds).

### Live screenshot verification (real server + real client, offscreen RT)
Ran a real `Allegiance.Server` on `m2xspiderw.igc` ("Combat Demo", 13 sectors,
134 asteroids per its own `[boot]` log) and connected the real MonoGame
client with an offscreen `RenderTarget2D` capture (the session-standard P207
technique — the Wayland backbuffer is unreliable). Frames vision-checked:

- **Positive — rocks render.** With the ship in Alderan (the only
  asteroid-bearing sector), the diagnostic read `sector 1052 → 134 rocks
  drawn, 134 total`; captured frames show numerous low-poly, faceted
  grey/brown rocks with per-face light/shadow shading at varied sizes (large
  foreground, small background), confirming both the procedural mesh and the
  per-radius scale.
- **Negative — other-sector rocks do NOT appear.** With the ship back in
  Ganymede (`Sectors[0]`, ZERO in-sector asteroids), the diagnostic read
  `sector 1050 → 0 rocks drawn, 134 total across all sectors`, and captured
  frames show NO rocks anywhere — proving the `ClusterId == SectorId` filter
  excludes the other 134 rocks in the world. The visual + numeric results
  agree in both directions.

(The Alderan run used a temporary, reverted-before-commit spawn override in
`Server.cs` to place the test ship in the asteroid sector — shipped code is
unchanged; `Server.cs` diff is empty.)

## Commit
Single P218 commit on `master`, message prefixed `P218:`, not pushed
(Domingo pushes).
