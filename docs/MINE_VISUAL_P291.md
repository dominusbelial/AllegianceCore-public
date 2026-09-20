# P291 — Mine Visual (deployed mines are now tracked and visible client-side)

## Relationship to the arc

- `MINE_WIRE_CODECS_P93` — the `ExportMine` (fmid 26 / `OT_mine`) and
  `MineDestroyed` (fmid 84) wire codecs were already decoded in this port.
- `MINE_SIM_OBJECT_P94` — `Sim.Mine` exists and the server wires
  `BroadcastExportMine` / `BroadcastMineDestroyed` to the sim's created/destroyed
  events (same-side audience).
- `MINE_PROXIMITY_DETONATION_P95`, `MINE_PROBE_DROP_P224` — the deploy/detonation
  mechanic is real server-side.
- **Gap closed here:** unlike probes (P260/P290), mines were **not tracked
  client-side at all** (`ClientWorld` had no `MineEntity`/`Mines`), so nothing
  could render them. P291 adds real client-side wire-state plumbing and the
  missing visual.

## What changed

1. **`ClientCore/State/MineEntity.cs`** (new) — mirrors `ProbeEntity` but also
   carries `MinetypeId` (the wire's `DataMineExport.MinetypeId`), which the
   renderer needs to resolve the real field radius (see radius nuance below).
2. **`ClientCore/State/ClientWorld.cs`** — a `Mines` dictionary populated by
   `Apply(ExportMine)` (fmid 26 / `OT_mine` — a mine is static, so the EXPORT
   position is final) and removed by `Apply(MineDestroyed)` (fmid 84), plus the
   two `case` arms in the `Apply(IFedMessage)` switch.
3. **`ClientCore/Session/ConnectionSession.cs`** — decodes the `OT_mine` branch
   of the shared fmid-26 EXPORT and routes `MineDestroyed.Fmid` (84) to the
   world (the P264 lesson: without the `Apply(IFedMessage)` case, the per-type
   `Apply` is dead code — verified by a must-fail test).
4. **`ClientCore.MonoGame/Rendering/ShopCatalog.cs`** — exposes `MineTypes` so
   the client can resolve a mine's type radius from the static core.
5. **`ClientCore.MonoGame/Rendering/MineFieldGeometry.cs`** (new, pure) — the
   native `MineFieldGeo` decal count `1 + int(strength·radius²/100)` (strength
   1.0) and deterministic uniform-in-sphere offsets.
6. **`ClientCore.MonoGame/Rendering/MineModelSource.cs`** (new) — resolves the
   real mine sprite (`fxminebmp.mdl`) and the per-type field radius.
7. **`ClientCore.MonoGame/Rendering/MineRenderer.cs`** (new) — draws each tracked
   mine as a native `MineFieldGeo` decal cloud (source-alpha billboards of the
   real fxmine sprite, grayscale distance brightness `clamp(100/dist,0,1)`).
8. **`Game1.cs`** — instantiates the renderer in `LoadContent`, gathers the local
   sector's `world.Mines` under the Gate lock and draws each cloud in the render
   loop (mirroring the P290 probe wiring).

## The real wire messages used for tracking

- **Creation:** `FM_S_EXPORT(26)` with `objecttype = OT_mine (3)` carrying the
  fixed 32-byte `DataMineExport` (`MineId`, `ClusterId`, `MinetypeId`,
  `LauncherId`, `SideId`, `P0`, `Fraction`, `createNow`). Native's `CmineIGC::Export`
  (`src/Igc/mineigc.cpp:158-185`) writes exactly this; the reference server
  creates a mine through the same generic
  `CreateObject(..., OT_mine, &obj, sizeof(obj))` path (`FedSrv.CPP:10072`).
  The server broadcasts it same-side on deploy
  (`Server.BroadcastExportMine`, `Server.cs:10253`).
- **Removal:** `FM_S_MINE_DESTROYED(84)` (`Messages.h:603-606`), carrying
  `clusterID` + `mineID`, broadcast same-side on detonation/lifespan expiry
  (`Server.BroadcastMineDestroyed`, `Server.cs:10312`).

## Census — mines carry NO model (the brief's mesh assumption is FALSE)

The brief assumed mines have per-type `ModelName`s like probes and directed a
`MissileModelSource`-style `.mdl` mesh census. The census disproves it (a
brief-positive-claim that had to be verified, not trusted):

- **Across all 782 mine records in every core in the corpus (210 files), the
  `ModelName` field is empty.** No mine type anywhere declares a mesh model.
- Native renders a mine as a **textured decal field**, not a mesh:
  `CmineIGC::Initialize` calls `LoadMine(textureName, 1.0f, radius, iconName, ...)`
  (`src/Igc/mineigc.cpp:103`), and the client `LoadMine` (`src/WinTrek/trekigc.cpp:1934-1957`)
  loads the surface `textureName + "bmp"` into a `MineFieldGeo`
  (`src/effect/efgeo.cpp:15-87`) — `1 + int(strength·radius²/100)` decals at
  `Vector::RandomPosition(radius)` within a sphere of the mine's radius, each a
  camera-facing `DrawDecal` of the texture, brightness `bound(100/dist,0,1)`,
  source-alpha blended (`BlendModeSourceAlpha`).
- The single real mine texture across the corpus is `fxmine`
  (asset `fxminebmp.mdl`, a 128×128 sprite), loaded at runtime with the P241
  `MdlTextureDecoder` — the same runtime-native-asset pattern as the missile/
  probe sources, applied to the mine's real (texture) representation.

## Radius nuance (don't confuse `Radius` and `MineRadius`)

Native C++ resolves `dataMineType->radius` on a `DataMineTypeIGC*` to the
**derived** member `DataMineTypeIGC::radius` (`igc.h:1992`), which member
shadowing hides behind the inherited `DataObjectIGC::radius` (`igc.h:1907`).
This port's decoder names that derived radius `DataMineTypeIGC.MineRadius`
(offset 464); its `Radius` (offset 16) is the shadowed, unused
`DataObjectIGC::radius` — verified garbage (`-107374180`) in every real corpus
record. So the visual/operational radius is `MineRadius` (75–250 in the corpus),
which is what `MineModelSource.ResolveRadius` reads and what the renderer sizes
the decal field to. The task brief's wording ("MineRadius is the blast radius")
matches the C# naming but the field is native's single mine radius, used for
both the render extent and detonation.

## Scope boundary (unchanged by design)

- Probe, chaff, treasure, buoy, building-effect rendering remain separate tasks.
- The deploy/detonation mechanic (P224/P95) is untouched.
- Native reference source (~/Development/Allegiance) is read-only.
- Mines are drawn with **no team tint**, matching native's pure-grayscale
  `Color(bright,bright,bright)` (`efgeo.cpp:70`) — unlike probes/stations.

## Verification (real, not just build/test)

**Live proof** (`/tmp/p291verify`, evidence copied to `.verify_p291/`): boots a
real in-process server (teams:1) + a real headless `ConnectionSession` (the true
client wire path), readies to `Started`, deploys a real PCore001 mine type 9
(`Ship.DeployMine`) and renders the REAL `MineRenderer` offscreen (P234/P235
sync-in-LoadContent recipe). Key log lines:

```
[mine] ship 8096 deployed -> EXPORT(26)/OT_mine 9 pos ... -> 1 same-side peer(s)
[p291] server: mine id 5464 at SERVER-AUTHORITATIVE position (720.0,730.0,920.0)
[p291] CLIENT world.Mines hydrated: id 5464, side 0, cluster 1050, minetypeId 9, position (720.0,730.0,920.0)
[p291] server-vs-client position delta = 0.0000   (mine is static)
[p291] resolved field radius = 100
```

The two PNGs (`mine_closeup_real_position.png`, `mine_threequarter_field.png`)
show the fxmine decal cloud forming a roughly spherical field centred exactly
where the red/green/blue origin axes cross — i.e. the mine is visible in the 3D
scene **at its real position**, with the wire evidence above proving the new
client-side tracking received the deploy broadcast.

**Build:** whole solution `AllegianceCore.slnx`, 0 warnings / 0 errors.

**Tests:** `ClientCore.Tests` 132 (added 5 `ClientWorldMineTests`), MonoGame.Tests
495 (added 9 `MineFieldGeometryTests` + 3 `MineModelSourceTests`). The dispatch-
switch test was proven to FAIL when the `Apply(IFedMessage)` case is removed
(must-fail-if-removed). Core.Tests full suite: see commit message for the 2× run
result (only the pre-existing `LiveTurretGunnerBindingTests` timing flake is
expected).

## Commit
On `master` (not pushed — Domingo pushes). No side branch.
