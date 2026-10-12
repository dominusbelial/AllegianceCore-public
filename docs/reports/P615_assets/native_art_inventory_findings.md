# P615 — Native Art-Coverage Inventory

Scope: enumerate every art asset the **native** Allegiance client loads at render/geometry
time, cross-reference each against the C# port (this repo), and rank the coverage gaps by
visual prominence as follow-up candidates. Companion data file:
`native_art_inventory.csv` (54 rows).

## How native loads art (source rule)
- Game entities (ships, stations, missiles, probes, projectiles, asteroids, mines,
  chaff, treasure, buoys) go through `CmodelIGC::Load(...)`/`LoadDecal`/`LoadMine`
  (`Igc/modelIGC.cpp:155,276`) → `ThingSite.LoadModel` (`WinTrek/trekigc.cpp:1816`),
  which honours a `textureName` override only when it is alpha-leading and differs from
  the model (`trekigc.cpp:1837-1845`), then loads `<modelName>.mdl` and the texture unit;
  otherwise the model's own imported texture is used (`ThingGeo::FindTexture`,
  `effect/thinggeo.cpp:1038-1043`; `<modelName>bmp` convention `engine/xfile.cpp:577`).
- Effects load art by macro name: `effect/efart.h`, `particlegeo.cpp`
  (e_smoke/e_fire/e_booster/e_damage/e_tele), `efgeo.cpp` (trail/build/aleph_sphere/bit),
  `efimage.cpp` (muzzle flare + lens flare), `efapp.cpp` (strobe), `alephgeo.cpp` (f02).
- Explosions/shield-flare load in `WinTrek/trekigc.cpp:2517-2560` from `artwork.h`
  (exp20-25bmp, fx18bmp shockwave, fx09bmp + lens30/60/90).
- Sky: `Igc/clusterIGC.cpp:41-57` picks the environment dome (`globeN` +
  `environmentNbmp`), planet poster (`nebplntNNbmp` via `AddPoster`), star-field seed
  (`effect/starimage.cpp:32-53`).

## Classification summary (54 rows)
| status | count |
|---|---|
| native-faithful (port loads real .mdl + decoded texture) | 41 |
| untextured-or-substituted (mesh faithful, texture absent/placeholder) | 2 |
| different (port uses different size/blend/geometry than native) | 6 |
| unused (native loads; port has no equivalent path) | 5 |

## Ranked gaps by visual prominence (follow-up candidates)
1. **Probes — untextured** (`Igc/probeigc.cpp:111-113`). `ProbeRenderer` parses the real
   `utl02`/`cc_pitprobe` .mdl mesh but renders it **solid lit, texcoords set to 0**
   (P233 never decoded probe textures). Highest-priority visual gap: probes are
   player-deployed and constantly on screen. → feed the probe `.mdl` texture through
   `MdlTextureDecoder` like ships/missiles.
2. **Warp/aleph geometry — different** (`effect/efgeo.cpp:405`, `alephgeo.cpp:53`).
   `WarpRenderer` keeps the P242/P258 **procedural hourglass** silhouette (textured with
   `alephbmp` + `f02bmp` star), whereas native uses the `aleph_sphere` cone/`AlephGeo`.
   Silhouette visibly differs during transits.
3. **e_boosterbmp afterburner smoke — different** (`particlegeo.cpp:163`). Port reuses the
   e_smoke family; native has a distinct exhaust smoke texture.
4. **e_damagebmp hull-damage smoke — different** (`particlegeo.cpp:141`). Port vents
   e_smoke via `DamageDebrisRenderer` instead of the dedicated damage sprite.
5. **trailbmp — different** (`effect/efgeo.cpp:524`). `ShipMotionTrailRenderer` uses a
   procedural lateral-gradient strip; native's `TrailGeo` paints `trailbmp`.
6. **debris1bmp — different/unused** (`debrisgeo.cpp:51`). Native `DebrisGeo` surface not
   used; port vents smoke instead.
7. **Moons — different placement** (native moon art `moonbmp`/`moon7bmp`/`moonnebbmp`).
   `MoonRenderer` uses native art but non-native placement and a lit-disc presentation.
8. **Buoys — unused** (`Igc/buoyigc.cpp:39`, LoadDecal 'buoy'/'buoyicon'). No 3D buoy
   renderer in the port (only map icon).
9. **Minor native-effect geos — unused**: `build` (`efgeo.cpp:135`), `utl10` bit
   (`efgeo.cpp:795`), radar `pointer`/`joystickindicator` (`indicator.cpp:122-123`),
   `plnt19bmp` editor preview (`mdledit.cpp:793`). Low/zero gameplay visibility.

Everything else — **ships, stations, asteroids, missiles, mines, chaff, projectiles,
treasure, explosion films, shockwave, shield flare, particles, sun lens flare, sky dome,
planet poster, seeded stars** — is `native-faithful`: the port parses the real `.mdl`
mesh and decodes the real texture via `MdlFile.Parse` + `MdlTextureDecoder.Decode`
(`NativeShipRenderer`, `NativeStationRenderer`, `NativeAsteroidRenderer`,
`NativeMissileRenderer`, `MineRenderer`, `ChaffRenderer`, `ProjectileSpriteRenderer`,
`NativeTreasureRenderer`, `ExplosionRenderer`, `ShieldFlareRenderer`,
`NativeEffectTextures`, `LensFlareRenderer`, `NativeSkyRenderer`, `PlanetPoster`).
