# HD-3: atlas materials across native object renderers

2026-10-18. Changes are left in the working tree; no commits or pushes.

The schema, rendering and pack-builder work is implemented. **The requested
real-pack HD screenshots of a multi-atlas hull, station and flying missile are
blocked by the available ESRGAN outputs.** All 21 available atlas sets belong to
single-atlas hulls; none overlaps the core's station, missile or multi-atlas-hull
stems. [The source inventory and intersections](HD3_assets/available-real-atlases.json)
record that result. No production textures were generated or assigned to unrelated atlases.

## Pack and material behavior

Schema 2 uses `textures.<stem>.native.TextureSha256` and
`textures/<stem>/{basecolor,normal,metallic,roughness,emission}.png`.
An atlas's `.mdl` texture hash is checked on first use. A missing atlas, stale
native hash, missing/corrupt map, unavailable shader or exhausted budget uses the
native material for that part. Successful and failed lookups are cached until
restart. Schema 1 still reads model-keyed paths, checks both model and texture
hashes, and honors its legacy completion/review ledger.

`NativeMaterialParts` retains the texture scope and index range of every mesh
through vertex-buffer merging. Each part uses its own texture stem, falling
back to the inherited texture only when the mesh has no explicit TextureGeo.
The same ranges are used for HD and native fallback; native winding, geometry,
UVs and depth passes stay intact. Tangent buffers allocate only on a successful
HD draw. Native readers handle case-sensitive artwork paths without disabling
case-insensitive atlas lookup.

One client-wide `HdMaterialRenderer` now serves every object kind with the
existing **512 MiB** mip-inclusive budget, staging reserve, lazy upload and idle
LRU/defer behavior. Materials sharing an atlas share residency. The saved HD
toggle controls the whole service. Wrap/clamp addressing follows the native
caller for every PBR map. Native effect glows, station lights and star-core
passes remain native; the aleph funnel and treasure flag decal can use a
validated/resident HD basecolour through their existing unlit/additive passes,
retaining animation, tint, blending and depth behavior rather than lighting
those emissive effects as opaque hulls.

| Kind | Production HD boundary / client call sites | Native rule |
|---|---|---|
| Ships/pods | [NativeShipRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs#L270), including flight, inspector, docked preview and native LODs | `~/Development/Allegiance/src/Igc/shipIGC.cpp:1412`; flip/scale in `effect/thinggeo.cpp:1484` and `:1501` |
| Stations | [NativeStationRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/NativeStationRenderer.cs#L157); flight and station inspector pass model/texture identity | `~/Development/Allegiance/src/Igc/stationIGC.cpp:240`; state scopes in `engine/geometry.cpp:1439` |
| Missiles | [NativeMissileRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/NativeMissileRenderer.cs#L257); flight and inspector volleys resolve the actual missile type | `~/Development/Allegiance/src/Igc/missileigc.cpp:50` |
| Probes | [ProbeRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/ProbeRenderer.cs#L133); client lazily loads each probe type's model instead of sharing only `utl02` | `~/Development/Allegiance/src/Igc/probeigc.cpp:112`; radius at `:118` |
| Model projectiles | [ProjectileModelRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/ProjectileModelRenderer.cs#L221); flight and inspector pass model identity and sector lighting | `~/Development/Allegiance/src/Igc/projectileIGC.cpp:53` |
| Asteroids | [NativeAsteroidRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/NativeAsteroidRenderer.cs#L167); flight passes the rock's model/texture identity | `~/Development/Allegiance/src/Igc/asteroidigc.cpp:32` |
| Aleph funnel | [WarpRenderer.Draw](../../src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs#L173) → shared emissive texture lookup; actual selected native texture is retained for fallback | `~/Development/Allegiance/src/effect/alephgeo.cpp:218` (cull-none/additive/no depth write through `:222`) |
| Treasure | [NativeTreasureRenderer.DrawModel](../../src/Allegiance.ClientCore.Game/Rendering/NativeTreasureRenderer.cs#L198) per mesh; `DrawDecal` uses shared emissive texture lookup | `~/Development/Allegiance/src/Igc/treasureIGC.cpp:118` (model selection), `:161` (decal), `:174` (model load) |

The per-mesh binding rule is native
`~/Development/Allegiance/src/engine/geometry.cpp:1079` (`TextureGeo::Render`),
with GroupGeo push/pop state at `:1443`. The inherited texture selection is
`~/Development/Allegiance/src/effect/thinggeo.cpp:1034`–1043. Existing native
model transforms are retained; this task does not change simulation, flight physics,
weapons, HUD or generated/Meshy art selection.

## Builder

[Usage and input contract](../../tools/hd-pack/README.md).
[tools/hd-pack/build_pack.py](../../tools/hd-pack/build_pack.py) validates all PNGs before publishing, permits the
ESRGAN 4K colour/normal/emission + 2K scalar layout, checks explicit sRGB/linear
tags/declarations, checks source hashes against artwork, records map hashes,
and writes a schema-2 folder and reproducible release zip. Untagged PNGs follow
the documented ESRGAN convention; sample values alone cannot prove colour-space
or generation provenance. Supply `maps.json` source metadata or
`--expected-hashes` to detect stale generator inputs before packaging.

`--core` runs the small C# catalogue exporter using the real core/MDL parsers.
Coverage includes model texture overrides and all explicit base/LOD mesh stems.
The real PCore001 catalogue contains **350 referenced stems** across 340 distinct
kind/model/override rows. Map-owned rocks/alephs and runtime treasure visuals are
outside static-core coverage, but their texture sets can be packaged normally.
No generated map files or release archives are included in this repository.

A real pack was built from `wc_icfig → fig02bmp`, `fig05 → fig05bmp`,
`cap04 → cap04bmp`, and `utl101 → utl101bmp`. All four source texture hashes from
the HD repo's `inputs/<model>/native.json` matched the artwork. Its output and
release zip are `/tmp/hd3-evidence/real-pack` and
[release zip](/tmp/hd3-evidence/real-pack.zip); [coverage](HD3_assets/real-pack-coverage.json)
is 4/350 stems. This is the feasible four-atlas pilot, not a claim of full HD
coverage.

## Validation

[Validation summary](HD3_assets/validation-summary.json) and
[focused invocation ledger](HD3_assets/focused-summary.json).

- Whole-solution build: **0 warnings / 0 errors**, including the catalogue tool.
- Server linux-x64 self-contained AOT publish: passed; **no IL2026/IL3050**.
- **25 selected C# methods, 78 fresh single-method passes**. The initial 24
  methods each passed 3/3; the later PNG-aleph check passed 3/3 and the binary
  aleph check passed another final 3/3 after that addition.
- **7 builder methods each passed 3/3**, including the tiny synthetic CLI pack,
  release zip reproducibility, partial map failures, dimensions, colour spaces,
  source hashes, safe mapping and overwrite refusal.
- Exactly **one** full run of Core, ClientCore and MonoGame, with live/loopback
  tests included. No second full sweep was run.

| Suite | Passed | Failed in sweep | Skipped | Wall seconds |
|---|---:|---:|---:|---:|
| Core | 4879 | 2 | 0 | 943.85 |
| ClientCore | 1380 | 0 | 1 | 1584.04 |
| ClientCore.MonoGame | 2443 | 0 | 3 | 968.71 |

The two Core failures were the existing transport shutdown race
`TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
(`ThreadStateException: Thread has not been started`) and
`LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (empty abort receipt).
Each passed **3/3 fresh exact-method runs**, selecting one case per invocation.
They meet the brief's load-flake rule and are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md).
The sweep failures remain recorded as failures; no transport, simulation,
assertion, deadline or quarantine code was changed.

ClientCore's one skip was the opt-in P660 showcase; its separately enabled
local run passed. The separately enabled local P542 projectile proof also
passed. MonoGame's three skips are existing tests requiring the unavailable
optional Meshy pack. Rig/221/200v200 diagnostic drivers were left to Claude as
instructed. The designated never-parks showcase gate passed **5/5 individual
runs**; no run failed, so the conditional base-rate comparison was not triggered.


Reproduction uses `NUGET_HTTP_CACHE_PATH=/tmp/hd3-nuget-http` in this sandbox
(the default per-user NuGet HTTP cache is read-only):

```sh
dotnet build AllegianceCore.slnx --nologo
dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release \
  -r linux-x64 --self-contained -p:PublishAot=true -o /tmp/hd3-aot
python3 -m unittest discover -s tools/hd-pack -p 'test_*.py' -v
# One method per invocation, repeated three times:
dotnet test tests/Allegiance.Core.Tests --no-build --no-restore \
  --filter FullyQualifiedName~HdPackTests.Schema2_multi_atlas_resolves_each_mesh_and_shares_material_across_model_kinds
# Renderer proof can also be invoked directly (kind = ship/station/missile/
# probe/projectile/asteroid/aleph/aleph_png/treasure):
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet \
  tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll "$PWD" \
  --atlas-test ship --art "$ALLEGIANCE_ARTWORK_PATH"
```

The new real-GL checks use real native geometry plus deliberately synthetic
8×8 maps, separately for all eight renderer kinds. They assert visible HD
rendering, zero upload with the toggle off, identical native rendering for an
absent pack, and isolated fallback for a missing map. The multi-atlas ship
`dn_faphshfig` loads both atlases; station `ss27` also loads both of its atlas
scopes. Aleph checks exercise both binary texture units and PNG-import units.
These regression checks are not presented as real-pack screenshots.

## Actual-client captures and remaining evidence

Both captures are unmodified **2560×1440** X11 client frames:

- [Real `wc_icfig` ESRGAN pack: native left / HD right](HD3_assets/real-ship-1440-compare.png).
  The production inspector used `--inspect-hd --inspect-hull 414 --window 2560x1440`.
  Its real 4K/2K material occupied 298.67 MiB; first synchronous upload was about
  4.36 seconds on this machine. Upload remains on demand as in HD-2.
  At this map resolution the unchanged 512 MiB budget cannot hold two such
  materials simultaneously: excess visible atlases retain native shading.
  Full simultaneous HD on a multi-atlas object therefore needs smaller input
  map sets that fit the budget; the regression tests use small sets to verify
  every part independently without raising the budget.
- [Multi-atlas `dn_faphshfig`: native fallback on both sides](HD3_assets/multi-atlas-native-fallback-1440.png).
  Its atlases `dn_faphsh000bmp` and `dn_faphsh001bmp` are absent from the supplied
  ESRGAN outputs. This demonstrates actual-client fallback, not HD coverage.

To finish the requested three real-pack comparisons, the external HD batch must
supply both hull atlases and a station and missile atlas (for example
`ss27bmp` plus `ss27insidebmp`, and `mis02bmp`). Build those per-stem map sets,
then capture with the production client. Generation is explicitly outside this
task's scope. No unavailable capture is marked passed.

Raw TRXs, logs, frame CSVs, real maps and zip are outside the repo under
`/tmp/hd3-evidence/`; AOT output is `/tmp/hd3-aot/`. This sandbox only permits
writes in the working tree and `/tmp`, so the brief's preferred
`~/allegiance-evidence/HD3/` cannot be populated from this lane. Copy the raw
folder there from the dispatcher if long-term retention is needed. Small
summaries/JSON/screenshots alone are retained in `HD3_assets`, below 2 MB.
