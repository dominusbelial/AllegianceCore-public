# P577 — HD sky tier from real telescope data (NASA star map + ESA/Hubble/Webb/NASA nebulae & planets)

## Summary

Added the **HD sky tier** behind P576's `Sky: Native | HD` switch
(`--sky native|hd`), per Domingo's request ("something just better? higher
definition? … real telescope public data? nasa … we will do both right? native
and new version via switch? — Yes, both"). The HD tier upgrades every layer of
the P576 per-sector sky with **real telescope public data**, matched per native
library entry so each sector keeps its mood. **Native stays the default** until
Domingo approves the comparison sheets (this task ships HD behind the switch;
the approval step is his).

The native tier is a **library, not per-location art** (P576 studied it): 10
environment domes `globe1…10` textured with `environmentNbmp` (2048² RGB565) and
43 planet posters `nebplntNNbmp`; maps pick entries per cluster. The 221 map
(`m2xspiderw.igc`) uses **6 environments** (`globe2,3,4,5,6,8`) and **9 planets**
(`nebplnt06,12,19,25,33,35,40,43,45`) across its 13 sectors — verified by
decoding the map (sector table in §Proof). HD provides a counterpart for all
10 environments and all 9 used planets.

---

## Sources, licences & credits

Every HD asset derives from real telescope public data with a verified licence.
Full mapping, per-asset source URL, licence and credit live in
`docs/reports/P577_assets/sky-hd.json`; licence texts + credit table are in
`docs/reports/P577_assets/licenses/` (the P546 packaging pattern). The in-game
Credits/Licences screen (F12) carries the visible attribution CC BY requires.

| Component | Source | Licence |
|---|---|---|
| Star field | NASA SVS **Deep Star Maps 2020** (dataset 4851, Hipparcos-2/Tycho-2/Gaia DR2), celestial ICRF/J2000 plate-carree, 8192×4096 EXR | Public Domain (NASA) |
| Nebulae (env1–10) | ESA/Hubble release images (heic0506a/1608a/1704a/1501a/0619a/1707a/2007a/1007a/1307a/1509a) | CC BY 4.0 (visible credit, no implied endorsement) |
| Planets (9 slots) | NASA Apollo/JPL/Hubble public-domain full-disc images | Public Domain (NASA) |

Credit lines per asset are listed in `licenses/README.md` and shown in-game.
`licence_url` for ESA/Hubble: `https://esahubble.org/copyright/`; for NASA:
`https://svs.gsfc.nasa.gov/4851/`, `https://photojournal.jpl.nasa.gov/`,
`https://images.nasa.gov/`.

---

## Implementation

### `tools/hd-sky/` (offline asset pipeline, committed)
- `make_hd_stars.py` — decode `starmap_2020_8k.exr`, max-pool to a 4096×2048
  working map (preserves sparse star points), detect+grow star maxima (punch),
  LUT tonemap, then bake a **seam-free equirectangular** star map
  (`stars_equirect_2048x1024` desktop / `1024x512` Android) *and* a 6-face
  cubemap. The 8k EXR is 130 MB; the baked equirect is a few MB.
- `make_hd_nebula.py` — compose a real telescope photo onto each native
  environment's palette/layout: cover-fit the photo, Reinhard-lab colour-
  transfer to the native texture (native palette preserved), boost the photo's
  high-frequency detail, keep the native bright-region layout, colour-key the
  near-black surround (native dome reveal). Output 2048×1024 RGBA equirect.
- `make_hd_planet.py` — full-disc planet image → square RGBA disc texture.

### Runtime (`HdSkyRenderer.cs`, `HdSkyGuard.cs`, `ISkyRenderer.cs`)
- **`ISkyRenderer`** — shared surface (`SetCluster`, `Draw`) implemented by
  `NativeSkyRenderer` and `HdSkyRenderer`, so the aleph destination-glimpse can
  render the arriving sector's **HD** sky when the tier is HD.
- **`HdSkyRenderer`** — per-cluster, camera-centred (P576b invariants):
  1. **Star dome**: NASA equirect on the native `globe1` sphere scaled to 7110
     (inside the alpha-keyed nebula dome) — replaces the seeded point stars.
  2. **Environment dome**: native `globeN` mesh + HD `hdsky/nebula/envN.png`.
  3. **Planet**: native billboard (lat/long/radius unchanged) + HD
     `hdsky/planet/nebplntNN.png`.
  Render states pinned (depth off, CullNone, alpha-blend) exactly as native.
- **`HdSkyGuard`** (pure, unit-tested): per-entry **readability cap** — a
  luminance ceiling (225/255) preserving hue and an HSV **saturation clamp**
  (0.62) — applied to every HD texture on load; plus the native-id → HD-asset
  naming rules (`globeN`→`envN`, `nebplntNNbmp`→`nebplntNN`). A global
  **`SkyIntensity`** (`--sky-intensity F`, default 1.0, clamped [0,2])
  multiplies the whole HD backdrop.
- Wire-up (`Game1.Rendering.cs` `DrawSkyP576`): HD tier dispatches to
  `_hdSky`; sector change calls `SetCluster` on both tiers. `--sky-lowres`
  selects the Android asset set. In-game **Credits/Licences** screen (F12)
  shows the licence table.

Readability logic ported/cited from native: dome unlit `ShadeModeCopy`
(`efgeo.cpp:1029-1052`), colour-keyed transparent surround
(`modeler.cpp:726`), draw order stars→dome→planet (`WinTrek.cpp:3214-3222`),
planet billboard sized by `planetRadius` (`efimage.cpp:515-563`). These are the
P576 native rules; the HD tier changes only textures + the star layer.

---

## Readability guard (scope item 3)

Measured (real-window backbuffer, centre region where a ship sits) — see
`docs/reports/P577_assets/proof/sheets/readability.csv`:

| env | native mean luma | hd mean luma | hd max luma | hd bright% (>200) |
|---|---|---|---|---|
| env1 | 91.9 | 92.2 | 223.5 | 3.6 |
| env2 | 74.4 | 71.8 | 168.9 | 0.0 |
| env3 | 47.2 | 38.6 | 165.2 | 0.0 |
| env4 | 34.9 | 32.4 | 118.9 | 0.0 |
| env5 | 12.6 | 15.9 | 40.3 | 0.0 |
| env6 | 15.6 | 20.3 | 125.9 | 0.0 |
| env7 | 42.2 | 46.4 | 172.3 | 0.0 |
| env8 | 44.6 | 41.6 | 157.4 | 0.0 |
| env9 | 86.4 | 71.0 | 162.8 | 0.0 |
| env10 | 87.7 | 76.4 | 202.2 | 0.0 |

All HD skies keep the backdrop dark (mean ≤ 36% of full scale; max ≤ 223, below
the 225 cap), so a dark ship silhouette reads clearly against every one — see
the `sheets/*` PNGs where a dark ship is silhouetted on each HD sky.

---

## Memory per tier

Uncompressed GPU texture upload per cluster (the sky's resident textures):

| Tier | Textures | Bytes | Total |
|---|---|---|---|
| Native | env 2048×1024 RGB565 + planet 2048×1024 RGB565 (+ star points VB) | 8 + 8 MiB | ~16 MiB |
| HD desktop | star equirect 2048×1024 RGBA + nebula 2048×1024 RGBA + planet 1024×1024 RGBA | 8 + 8 + 4 MiB | 20 MiB |
| HD Android | star 1024×512 + nebula 1024×512 + planet 512×512 (RGBA) | 2 + 2 + 1 MiB | ~5 MiB |

HD desktop adds ~4 MiB/cluster over native (third texture + RGBA8 vs RGB565) —
a modest, bounded cost for the higher-definition backdrop; Android is cheaper
than native desktop.

---

## Proof

Real-window backbuffer PNGs (presented frames, not `--capture`/offscreen) in
`docs/reports/P577_assets/proof/`:
- `sheets/{native,hd}_envN.png` — **Native vs HD side-by-side for all 10
  environments** (the 6 the 221 map uses are env2,3,4,5,6,8), each with a dark
  ship silhouette (readability) — real window, built by `tools/hdsky-proof`
  driving the actual `NativeSkyRenderer`/`HdSkyRenderer`.
- `hd_live_omega.png` / `native_live_omega.png` — **live real-window 2560×1440**
  HD and native sky in Omega (env2) with a real ship in view, captured by
  `scripts/live-proof.sh` with `--sky hd|native`.
- `hd_glimpse_omega.png` — HD aleph destination-glimpse (`--sky hd
  --aleph-glimpse on`): the glimpse renderer now constructs the HD sky for the
  destination sector (subtle, as in P576).
- `stars_cubemap_contact.png` — the NASA Deep Star Maps 2020 skybox.

The 221 map's cluster → environment/planet usage (decoded from
`m2xspiderw.igc`): Omega→globe2/nebplnt43, Alderan/Cirrus/Juniper/Terra→globe3,
Ganymede/Tellus→globe4, Plato/Rigel→globe5, Bug Nebula→globe6,
Aurora/Gaia/Organia→globe8; planets 06/12/19/25/33/35/40/43/45.

Live navigation to every sector is bounded by the documented P576 auto-goto
limitation (only Omega/Alderan reachable reliably), so the per-environment
side-by-side sheets come from the real-window `hdsky-proof` tool driving the
actual renderers, and the live client window proves the end-to-end HD tier
(Omega HD + native, real ship, 2560×1440).

---

## Unit tests (failing-first)

- `HdSkyGuardTests` (12): `EnvironmentIndex` globe→N parsing, `PlanetStem`
  `nebplntNNbmp`→`nebplntNN`, and the readability cap — over-bright pixel
  scaled to the ceiling preserving hue, high-saturation pixel clamped to the
  HSV ceiling, dark/low-sat pixel unchanged, transparent pixel untouched.
- `SkyP577LaunchDefaultsTests` (5) + existing sky-defaults: sky tier defaults
  to **native**, `SkyIntensity` defaults 1.0, `SkyLowRes` defaults off, tier/
  intensity can be switched.

Failing-first: with the pre-P577 guard (no HD sky), 5 tests failed
(`EnvironmentIndex` naming + cap cases); they pass once the HD guard lands
(20 new tests green; full MonoGame suite 1617/1617).

## Build

Whole solution `AllegianceCore.slnx` Debug: **0 warnings, 0 errors**.

## Suite (3 runs, `scripts/run-tests.sh`, on the final code)

Three consecutive full-suite runs on the final code (each ~12 min). All failures
are the **documented live-wire timing load-flakes** in `docs/TEST_FLAKES.md`
(unrelated to P577 — P577's own tests are pure/socket-free and pass every run;
the MonoGame and Core totals sit at their documented baselines and the specific
failing tests rotate run-to-run exactly as the ledger predicts):

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1 | 12/12 | 1617/1617 | 561/561 | 3808/3815 (7 flakes: rotating live-wire set) |
| 2 | 12/12 | 1617/1617 | 559/561 (2: `ClientWarpApproachSlowHullLiveTests.Legacy_200_450…` ledger:215, `FullSessionKillIntegration…CrossSideKill` ledger:33) | 3812/3815 (3: `GameOverPlayersWire…Win_by_elimination`+`Conquest_win_in_one_mission` ledger:30/206, `ShipUpdateBudgetLiveTests.Locked_target…` budget-timing class) |
| 3 | 12/12 | 1616/1617 (1: `ChatTargetComposeLiveTests.ComposeDefault_Team…` live-wire chat timing — load-flake class, not individually ledgered but passing runs 1-2 and the standalone full-MonoGame run) | 561/561 | 3814/3815 (1: `LiveRipcordTests.First_activation…` ledger:36) |

P577's new tests are in the passing totals every run: `HdSkyGuardTests` 12/12
and `SkyP577LaunchDefaultsTests` 5/5 (MonoGame), plus the 3 existing
`SkyLaunchOptionDefaultsTests`. Per the task's "report each run as it happened"
rule, the suites were not re-run to force a clean pass — the failing tests are
the ledger's known rotating live-wire flakes.
