# P701 — native station bays and landing lights

Date: 2026-10-15. Supplied checkout: `codex/p701`, HEAD `d31ce96d`
(P699 WIP). Changes remain in the working tree; no commits or pushes.
The simulation docking/frame reconstruction from P699 is unchanged.

## What was wrong

The MDLX reader already collected the station's base mesh groups, but discarded
which `TextureGeo` image enclosed each group. `NativeStationRenderer` combined
those groups and painted the whole station with one exterior skin. Bay interiors,
rock shells and secondary panels therefore sampled the wrong atlas. Its separate
radial-centroid winding heuristic could also reverse a concave bay independently
of the exterior.

A second failure affected **15 referenced image namespaces**: these are text MDL
files using `ImportImageFromFile("...png", ...)`, rather than binary RGB565
surfaces. The old station loader rejected them. Examples include `beltersbaybmp`,
`ss300bmp`, and `ss301bmp`. Merely finding their `.mdl` filenames, as the earlier
P615 audit did, did not establish that the renderer could load them.

Finally, the parser skipped the entire serialized `LightsGeo` payload. Flight
rendering substituted three approximate station beacons; the station inspector
rendered no station lights at all.

## Final rendering behavior

- Preserve each mesh's image namespace, including nested texture scope and
  parent inheritance. Combined GPU buffers now have a separate draw range for
  every native texture group.
- Use native's first `FindTexture()` result as the inherited parent image;
  `TextureGeo` children bind their own images. A meaningful station texture
  override initializes the parent image, as in native, rather than repainting
  every child. The garrison's `bgrnd03bmp` is an **asteroid surface**, so its rock
  shell now looks different from the old incorrectly panelled shell.
- Compute the station's mesh radius as native does: maximum vertex distance
  from the model origin across the base and LOD geometry. The previous merged
  bounding-box spheres understated/overstated station scale. Geometry and light
  decals now use `stationRadius / nativeMeshRadius`. The independent
  [radius inventory](P701_assets/station-radii.csv) covers all 81 models.
- Keep authored mesh indices and normals. Remove the station-only radial
  winding guess. Both colour and soft-particle depth passes use the same buffers
  and counterclockwise-face culling. No fabricated bay geometry or double-sided
  rendering was added.
- Load both compiled RGB565 surfaces and the actual filename in text image
  imports. PNG textures use the existing 3-D texture row convention. Station
  skins retain their source alpha under native's source-alpha blend. Every
  binary station surface has its colour-key flag clear, and bay PNGs are opaque;
  `ss304.png` additionally contains alpha 254. No invented transparency key is
  applied. Texture sampling uses native's wrapping address mode. See
  [all 105 texture sources](P701_assets/texture-sources.csv).
- Read the **`lights` export**, independently from `object` and `frames`. Decode
  every colour, model-space position, period, phase, ramp-up, hold and ramp-down.
  Use the mesh's flip, scale and station orientation for the lights too. Decal
  half extent is `0.5 * nativeScale * pulse`, with the saved colour and alpha.
- Both flight and inspector draw the native `f101bmp` additive light decals,
  using depth testing without depth writes. Native's projected-radius cutoff
  (`>32` pixels) is applied to station lights. Models with no native lights
  receive no invented beacons.

The station inspector adds `--inspect-station <type-id|model>`,
`--inspect-native-only`, `--inspect-pitch <degrees>`, and
`--inspect-light-time <seconds>` for reproducible
close views and pulse samples. Without a fixed light time, the lights animate
from the running clock. Its HUD shows total and currently active native lights.

## Bays, groups, frames and the runway rows

The client catalogue is PCore001: **143 station types, 81 distinct models,
207 base mesh groups, 1,987 native light records**. The independent inventory
covers that entire catalogue, not just garrisons. Its 105 image namespaces are
90 compiled RGB565 surfaces and 15 direct PNG imports; these bay images do not
use an animated texture deck. Their animated guide lights come from `LightsGeo`.

| Garrison/outpost model | Native bay/secondary images (alongside the exterior) | Lights |
|---|---|---:|
| `ss24a` | `cwbaybmp`, `bgrnd03bmp` rock shell | 42 |
| `ss27` | `ss27insidebmp` | 44 |
| `ss300` | `beltersbaybmp`, `bgrnd03bmp` rock shell | 50 |
| `ss301` | `beltersbaybmp`, `bgrnd03bmp` rock shell | 22 |
| `ss100`, `ss101` | `biosbaybmp` | 36, 41 |
| `ss01`, `ss98` | `rixbaybmp` | 54, 0 |
| `ss90` | `ss27insidebmp` | 15 |
| `ss92` | `ss92abmp` | 0 |
| `dgarr`, `dgsup` | `dregbaybmp` | 41, 41 |
| `dn_faphbsgar`, `dn_faphbsop` | `dn_faphbsinsidebmp` | 44, 15 |
| `tfgrr` | `bgrnd55bmp` | 16 |
| `rainop` | `rainop1bmp`, `rainop2bmp` | 16 |
| `faohbsgar`, `faohbsop` | own single texture atlas | 62, 20 |
| `ss401`, `ss402` | own single texture atlas | 1, 1 |

The runway-style lines are the **authored SS light positions beside the bays**,
compiled into `LightsGeo`. They are not extra `FrameData` lights or a procedural
approach-axis effect. The garage frames remain docking position/orientation
metadata, and native's compiler deliberately retains garage geometry in the
object graph. Compiled models combine drawable groups by texture; original
source group names do not survive as named renderable submeshes.

All 1,987 shipped station light records have period **1.25 s**, phase **0**,
ramp-up **0.1 s**, hold **0**, ramp-down **0.05 s**. Thus these particular models
blink together; they do **not** encode a chasing sequence. The implementation
retains individual values and also tests distinct phase/period/hold values.
Every saved position and colour is listed in
[station-lights.csv](P701_assets/station-lights.csv); all frame records and exact
binary light floats are retained in the
[independent snapshot](../../tests/Allegiance.ClientCore.MonoGame.Tests/Fixtures/P701StationModels.json).
[station-groups.csv](P701_assets/station-groups.csv) records each group's texture,
vertex/triangle counts and model light count.

## Native citations

All native paths below are relative to `~/Development/Allegiance/src/`.

| Native rule | Source |
|---|---|
| Station model, parent texture and radius | `Igc/stationIGC.cpp:240-249` |
| Dock/garage metadata loaded separately from rendering | `Igc/stationtypeigc.cpp:40-98` |
| Garage frames retained in the compiled object graph | `effect/efapp.cpp:531-560`, `592-602` |
| TextureGeo argument order and per-child texture binding | `engine/modeler.cpp:1310-1318`; `engine/geometry.cpp:1079-1082` |
| Group child state restoration and first texture search | `engine/geometry.cpp:1439-1473` |
| Parent texture initialization and fallback | `effect/thinggeo.cpp:1029-1043` |
| Meaningful model texture-name filtering | `WinTrek/trekigc.cpp:1837-1839` |
| Text ImportImageFromFile loads the declared image file | `engine/modeler.cpp:623-674` |
| Native mesh reader and unchanged index upload | `engine/modeler.cpp:1637-1660`; `engine/VBIBManager.cpp:441-503` |
| Source-alpha mesh blend | `effect/thinggeo.cpp:1642-1645` |
| Native CCW culling and wrapping texture address state | `engine/context.cpp:345`; `engine/RasterD3D9.cpp:374-375`; `engine/D3DDevice9.cpp:1207-1208` |
| LightData layout, binary write/read and exported lights lookup | `effect/efapp.cpp:99-112`, `270-283`, `330-340`; `effect/thinggeo.cpp:993` |
| SS colour, position and timing compiled from source groups | `effect/efapp.cpp:474-482`, `498-560` |
| Pulse envelope, saved colour and half-unit size | `effect/efapp.cpp:171-222` |
| Origin-centred mesh radius, maximum across groups and all LOD levels | `engine/geometry.cpp:408-418`, `1486-1496`, `1748-1757` |
| Model flip/scale/orientation applied to lights and object | `effect/thinggeo.cpp:1482-1510`; scale at `1185-1187` |
| Lights require projected radius greater than 32 pixels | `effect/thinggeo.cpp:1618-1630`; `engine/context.cpp:1858-1871` |
| Actual strobe image and additive, depth-read-only decals | `effect/efart.h:14`; `engine/context.h:921-929`; `engine/context.cpp:209-225`, `2312-2317`, `2403-2424` |

## Inspector evidence

Before captures came from the original, separately copied client build at the
supplied HEAD. After captures use the changed client and the same artwork,
station radius, camera distance and lighting. These are real 1280×720 DesktopGL
backbuffer reads, not a reconstructed mesh drawing. Mesa used its software GL
fallback on this display.

- [Overview of all 32 garrison/outpost types](P701_assets/garrison-outpost-overview.png).
- [Per-type before/after index](P701_assets/station-types.md): **32 sheets**, each
  containing three matched settled camera views (yaw 0°, 90°, 180°), **192 source
  views total**. Each row is before on the left and after on the right. The
  procedural placeholder also appears in the full inspector frames.
- [SS24a overhead bay view](P701_assets/ss24a-overhead-bay.png), at 70° elevation
  so the vertically entered bay can be seen without the procedural placeholder.
- [SS27 native bay lights, animated](P701_assets/ss27-bay-lights.gif), sampled from
  real inspector frames at the saved native pulse times. The GIF's full loop is
  1.25 seconds. [Peak](P701_assets/ss27-lights-peak.png) and
  [off](P701_assets/ss27-lights-off.png) retain full backbuffers.
- Measured model-region differences against time zero grow through **16, 49,
  102, 192 pixels**, shrink back to **49**, and become **exactly zero at 0.15 s
  and 0.5 s**. See [pixel measurements](P701_assets/light-pulse-pixels.json).
  This proves an actual growing/shrinking flash and a genuinely empty off phase.

Two capture details matter for reproduction. The existing sweep updates yaw
*after* drawing, so the initial `angle0` frame has a preceding pose; the sheets
use only matching settled `angle1/2/3` frames. Also, the existing PNG writer
finalized CRC32 between chunk type and data, producing invalid checksums. P701
fixes that encoder and adds a CRC regression. Baseline checksum repair changes
only CRC fields, preserving the captured image payloads.

Commands and assembly scripts are in
[tools/station-bay-proof](../../tools/station-bay-proof/README.md).
The [capture manifest](P701_assets/captures.csv) identifies each source frame.

## Verification

- Solution build: **0 warnings, 0 errors** ([build log](P701_assets/build.log)).
- **41 new/affected test methods, each run at least three times alone**:
  **129 invocations, 198 case executions, 198 passed**, no skips. Theories execute their cases
  sequentially within the isolated method. Covers the independent corpus oracle,
  synthetic nested image namespaces and unused light definitions, bay images,
  origin-centred radius including LODs, exact light transforms/colour/alpha/timing,
  native 32-pixel cutoff, PNG CRCs,
  existing MDLX parsing, station resolver/catalogue, strobes and screenshots.
  [Invocation ledger](P701_assets/isolated-tests.txt),
  [case totals](P701_assets/isolated-results.json).
- Real GPU audit: **3/3 passed**. Every one of the **81 models / 207 groups**
  has the expected buffer sizes and draw ranges, loads its image, and draws a
  nonblank model. Full readbacks of **all 105 textures** match their source RGBA
  with the expected row orientation, including all 15 PNG imports and their alpha.
  [GPU group results](P701_assets/station-gpu-audit.csv),
  [audit log](P701_assets/gpu-audit.log).
- All proof PNGs pass independent Pillow decoder verification. Captures were
  refreshed after correcting the native radius calculation.

Unfiltered suites (including live/loopback tests; no quarantine filter):

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4,566 | 0 | 0 |
| ClientCore | 1,017 | 1 | 1 |
| MonoGame | 2,225 | 0 | 3 |

The ClientCore failure was the unchanged
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`:
async logon drain order produced sides `(0, 1, 0)`. **After all three suites
finished, that exact method passed 3/3 in isolation**. It is classified as the
previously documented load flake under the brief's rule, and recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md). No assertion, deadline, quarantine trait or
transport/seating behavior changed.

The default ClientCore skip was the local **15v15 P660 showcase**. It was then
explicitly enabled with `ALLEGIANCE_LIVE_P660_SHOWCASE=1` and **passed**. Thus the
only remaining skips are the three existing unavailable private Meshy-pack
checks. The machine/rig-only `221` and `200v200` scenarios were not run, as
instructed. [Raw suite summary](P701_assets/suite-results.json),
[isolated flake reruns and enabled showcase](P701_assets/followup-results.json).

The final inspector-only elevation option was additionally built and exercised
in the real-window overhead capture after the suites; its default remains the
same 22° used by the matched comparison captures.
