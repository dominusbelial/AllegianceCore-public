# P576 — native per-sector sky + optional aleph destination glimpse

## Summary

Gave every sector its own **native sky** instead of the same three generic
point-star parallax shells everywhere: a **seeded star field**, the **environment
(nebula) dome** and the **planet poster**, each per-cluster from the cluster's own
map data, switched on sector change (warp transit) — the default sky, because it
is native. Added an **optional, non-native "aleph destination glimpse"** (default
OFF, `--aleph-glimpse on`) that faintly shows the destination sector's sky inside
each visible aleph's funnel centre. Built the `Sky: Native | HD` seam for P577
(`--sky native|hd`; HD is the P577 plug point, not implemented here).

Part B was the task Domingo scoped from Claude's idea after loving the native
aleph in P562/P568 — but Part A (per-sector sky) was a prerequisite gap found
while scoping: there was no destination sky to glimpse.

---

## A0 — Native study (required) and reference sheets

Studied the native sky as a **library, not per-location art**:

- **10 environments** `globe1…10.mdl` — each is a *sky-dome mesh* (a `MeshGeo`
  named `object` inside the `globeN` namespace) textured with
  `environmentNbmp.mdl` (8,388,728 B ≈ 2048² RGB565; `environment9/10bmp` are
  256²). Decoded with the P566/P576 `.mdl` decoder (`tools/sky-study/decode_sky.py`):
  - `globe1…9`: a **full sphere, radius 7500, centred at the camera**, 78 verts /
    96 tris (globe10: radius **5**, 98 verts / 128 tris — a small special dome).
  - UV is full equirectangular [0,1]×[0,1]; normals are near-zero (degenerate) →
    the dome is drawn **unlit** (`ShadeModeCopy`, `efgeo.cpp:1029-1052`).
  - `globeN.mdl` imports `environmentNbmp` — confirming the posterName → texture
    mapping `globeN` ⇔ `environmentNbmp` (`globe1.mdl` namespace list: `model,
    environment1bmp`).
- **43 planets** `nebplntNNbmp.mdl` — 2048² RGB565. `nebplnt51…63bmp.mdl` are
  72-byte ASCII modeler stubs (`nebplntNNbmp = ImportImageFromFile("nebplntNN.png")`)
  whose real texture is the sibling `nebplntNN.png`.
- **Draw order & blending** (`WinTrek.cpp:3214-3222`): scene → posters → stars →
  environment. The environment dome is drawn **last**, `ShadeModeCopy` (unlit),
  **depth-write off**, into a Z-less 3D layer (`GeoImage::Render`,
  `image.cpp:1159-1178`; the dome's near-black surround is **colour-keyed
  transparent** — `modeler.cpp:726`, `device3d.cpp:1172` — so the nebula's black
  reveals the stars/posters/scene drawn beneath). The planet is a
  **billboarded square** at its direction, sized by `planetRadius`
  (`PosterImage::AddPoster`, `trekigc.cpp:243-248` + `efimage.cpp:515-563`).
- **Planet placement** (`clusterIGC.cpp:44-57`):
  `sinLat = planetSinLatitude/240 − 0.5`, `cosLat = √(1−sinLat²)`,
  `lon = planetLongitude/240 · 2π`, `pos = (cos lon·cosLat, sin lon·cosLat, sinLat)`;
  the two angle fields are `BytePercentage` (divisor 240, `Utility.h:1307+`);
  `planetRadius` is a byte 50–100 used directly as the poster scale.
- **Stars** (`StarImage::SetCount`, `starimage.cpp:32-53`): `count = nStars·4`,
  `srand(seed)` then per star `direction = Vector::RandomDirection()`
  (`vector.cpp:37-46`: `(cos yaw·cosPitch, sin yaw·cosPitch, sinPitch)` with
  `sinPitch=random(−1,1)`, `yaw=random(0,2π)`) and `bright = random(0.25,1)`,
  drawn **at infinity** (directions only — native has **no parallax**; motion
  streaks come from the star image's own line drawing, `starimage.cpp:115-171`).
  Seeded with the **MSVC CRT `rand()`** (LCG `next=next·214013+2531011`,
  `(next≫16)&0x7fff`) so the field is byte-identical to native.

**Contact sheets** (Domingo's reference + the basis for C's mapping):
`docs/reports/P576_assets/contact_environments{,_full}.png` (all 10) and
`contact_planets{,_full}.png` (all 43), plus every texture individually. Built by
`tools/sky-study/decode_sky.py` (a faithful port of `decode_texture.py` +
`MdlParser.cs`; handles the PNG-stub planets too).

### Reference sheets
- Environments: `contact_environments.png` / `contact_environments_full.png`
- Planets: `contact_planets.png` / `contact_planets_full.png`

---

## A — Native per-sector sky (default ON: it's native)

### Data path
The port's client already holds every needed field: `ClientWorld.Clusters` is a
`Dictionary<short, DataClusterIGC>` (full `OT_cluster`: `StarSeed`, `Stars`,
`PosterName`, `PlanetName`, `PlanetSinLatitude/Longitude/Radius`, `LightColor`/
`LightDirection`); the own ship's `SectorId` selects the current one. No new
wire/map plumbing was required (`DataClusterIGC.cs` was already decoded).

### New code
- `src/Allegiance.Core/Sim/NativeSkyMath.cs` — pure ports of the MSVC `rand()`
  LCG, `StarImage::SetCount` star generation, and the planet direction/radius
  maths. Headless, unit-tested.
- `src/Allegiance.ClientCore.Game/Rendering/NativeSkyRenderer.cs` — the renderer:
  seeds stars, loads the `globeN` dome (`MdlFile.Parse`) + `environmentNbmp`
  texture, and the planet billboard; draws them camera-centred at infinity with
  **pinned render states** (P568 lesson): depth off, additive stars, alpha-blend
  for the colour-keyed dome/planet, cull restore, sampler restore.
- Wiring (`Game1.Rendering.cs` `DrawSkyP576` + `Game1.Lifecycle.cs` +
  `Game1.cs`): replaces the three generic parallax starfield draws; on sector
  change (`SectorId` change) calls `NativeSkyRenderer.SetCluster`. The old
  parallax `Starfield`s are retained only as the pre-cluster fallback backdrop.

### Native fidelity notes
- Stars replace the port's three parallax layers: native has **no** parallax
  field (directions at infinity only), so per the task the parallax fields are
  retired from the default path (motion cues came from native's own star image).
- Dome rendered unlit at its native 7500 radius (flight far plane is 80000);
  near-black colour-keyed to alpha 0 so the nebula reveals the stars, matching
  native's colorkey compositing.
- Planet drawn as a camera-facing billboard at the planet direction, on the sky
  shell, sized from `planetRadius` (sized to read clearly on screen).

---

## B — Aleph destination glimpse (non-native, OPTIONAL, default OFF)

`--aleph-glimpse on` (client-only; default OFF):
- `Rendering/AlephGlimpseRenderer.cs` renders the **destination sector's** sky
  (via `NativeSkyRenderer`, so the glimpse is exactly the sky you get when you
  arrive) into a small cached `RenderTarget2D` (256², rendered once per
  destination), then composites it as a camera-facing quad at the warp with a
  **soft radial mask** (centre ~30%, fading to 0 before the funnel wall), under
  the funnel geometry / P562 f02 star core, with the P562 flow intact.
- **Cost bounded**: nearest-first, at most `MaxGlimpses` (6) alephs, sub-threshold
  on-screen funnels skipped; one low-res target per destination, cached.
- `--sky native|hd` seam: `hd` is the P577 plug point and currently falls back to
  Native.

---

## Unit tests (failing-first)

- `tests/Allegiance.Core.Tests/NativeSkyMathTests.cs` (12) — MSVC `rand()`
  fidelity (seed 1 → 41, determinism, seed-dependence), `GenerateStars` count =
  `nStars·4`, determinism, unit directions, brightness ∈ [0.25,1], planet
  direction vs the native formula (prime meridian → +X, quarter-longitude → +Y,
  north pole → +Z=0.5).
- `tests/Allegiance.ClientCore.MonoGame.Tests/SkyLaunchOptionDefaultsTests.cs`
  (3) — **aleph glimpse defaults OFF**, sky tier defaults to `native`, can be
  switched on.

Results: NativeSkyMathTests 12/12, SkyLaunchOptionDefaultsTests 3/3 on the final
code. (These exercise brand-new functionality — the "today's code" state had no
native sky at all.)

---

## Build

Whole solution: **0 warnings, 0 errors** (multiple incremental + a full
`-m:8` build).

---

## Suite (3 runs, `scripts/run-tests.sh`, on the final code)

Three consecutive full-suite runs on the final code (each ~12 min). All failures
are the **documented live-wire timing load-flakes** in `docs/TEST_FLAKES.md`
(lines 38-39, 46, 51, 182, 268-272, 395, 206-207) — fixed-sleep live-socket
receipt under full-suite load, **unrelated to P576** (P576's own tests are pure
and pass every run; the failing assemblies' totals sit at their documented
baselines, and the specific failing tests rotate run-to-run exactly as the
ledger predicts):

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1   | 12/12 | 1587/1587 | 560/561 (1 flake: ThreePeerSeededSession…DeterministicVisibility, line 38) | 3813/3815 (2 flakes: SideElimination…Eliminating_side2 line 39, GameOverPlayersWire…Win_by_elimination line 206) |
| 2   | 12/12 | 1587/1587 | 561/561 | 3813/3815 (2 flakes: GameOverPlayersWire…Conquest_win_in_one_mission line 30/206, AsteroidMinedLiveTests…miner_hits_capacity line 268-272) |
| 3   | 12/12 | 1587/1587 | 560/561 (1 flake: ShipDeleteGoneLiveTests…UndockReturnsAndClears line 46/51) | 3811/3815 (4 flakes: LiveTeamLobbyTests…Unready line 395, LiveRipcordTests…insufficient_energy line 182, GameOverPlayersWireTests…Win_by_elimination + Conquest_win_in_one_mission lines 206) |

P576's new tests are in the passing totals every run: NativeSkyMathTests 12/12
(Core), SkyLaunchOptionDefaultsTests 3/3 (MonoGame). Per the task's "report each
run as it happened" rule, the suites were not re-run to force a clean pass — the
failing tests are the ledger's known rotating live-wire flakes.

---

## Proof

- A: real-window 2560×1440 screenshots (real presented-backbuffer PNGs via the
  P572 `--autoshots` path, `scripts/live-proof.sh`, 221 map `m2xspiderw.igc`) —
  see `docs/reports/P576_assets/proof/`:
  - `sky_Omega_nebula_galaxy.png` — Omega: blue/purple nebula dome (globe2,
    `environment2bmp`) + spiral-galaxy poster (nebplnt43) + seeded stars.
  - `sky_Alderan_earth.png` — Alderan: brown/orange dusty nebula (globe3) +
    Earth-like planet (nebplnt06) + stars. **Clearly a different sky than
    Omega**, matching the map data (`posters`/`planet` per cluster).
  - `sky_Omega_frame2.png` — second Omega angle.
  Two further sectors (Ganymede/Organia/Rigel) could not be reached this
  session: the P572 auto-goto autopilot loses its target ("autopilot OFF
  (target lost)") and the `--auto-target aleph` transit is single-shot per run
  (every run starts in Omega and its nearest aleph is the Alderan hub), so the
  ship consistently reaches only Alderan (pre-existing navigation automation
  limitation, `docs/LIVE_PROOF.md`). The two captured sectors already prove the
  feature's core: different environments AND different planets per cluster,
  both matching the map.
- B: the Rigel/Alderan aleph with the glimpse **on**
  (`--aleph-glimpse on`): `glimpse_Alderan_funnel_omega_sky.png` — the funnel's
  centre carries a faint blue/cyan portal glow distinct from Alderan's
  brown/orange sky (the destination is Omega, whose sky is blue/purple), i.e.
  the destination-sky glimpse composited into the funnel centre with the soft
  radial mask. `glimpse_off_alderan_funnel.png` — for comparison. The glimpse
  composites AFTER the opaque scene so the funnel doesn't cover it, at ~32%
  centre opacity fading to 0 before the funnel wall, bounded nearest-first
  (≤6 alephs). Cleaner off/on funnel pairs at 3 distances were limited by the
  same auto-navigation/framing flakiness (the funnel is only momentarily
  centred through the asteroid field); the feature is default-OFF.
