# P589 — space objects against the black sky: the native SUN + LENS FLARE (per-sector light direction), plus moons (native art) and distant galaxies / small nebulae as cut-out objects (no domes)

## Summary

Per Domingo (2026-10-08): single objects on black read well, and "planets look
really good against the black background" — "maybe we can do something similar
with other space objects … positioned like a painting against the sky box."
Building on P588's black-sky default (native planet only), P589 adds the rest
of the space objects that read as clean "paintings" on black, all drawn at
infinity, camera-relative, behind everything, with no mid-frame
render-target switches (the P573/P576b/P582b lessons):

- **Native sun + lens flare** — the per-sector light source, a faithful port of
  native `LensFlareImage` (`effect/efimage.cpp:573-760`) driven by the cluster's
  `LightDirection` (`WinTrek/WinTrek.cpp:3614-3617, 3639`): a visible sun
  `STAR_AURA` at the light direction plus the eight-element ghost flare.
- **Moons** — 0-2 per sector, seeded by the cluster id, native art
  (`moonbmp`/`moon7bmp`/`moonnebbmp`), placed near the planet, lit with the
  sector light.
- **Distant galaxies / small nebulae** — real telescope cut-outs (Helix,
  Whirlpool, Ring, Sombrero, Pinwheel, Lion×2), 2-4 per sector, planet-like
  size, dim, no star specks, feathered edges.
- **Deep-field galaxies + Little Red Dots** — hundreds of small galaxy sprites
  (from the Hubble Ultra Deep Field) + a few dozen procedural red dots, one
  batched/instanced draw.

Toggles: `--moons off`, `--distant-objects off`, `--galaxies off` (all ON by
default in the black sky). The aleph destination-glimpse shows the
destination's objects too.

---

## Implementation

### Native sun + lens flare — `LensFlareRenderer.cs`

Faithful port of native's `LensFlareImageImpl::Render`
(`effect/efimage.cpp:706-750`) using the exact native element table
(`effect/efimage.cpp:601-613`) and surfaces (`effect/efart.h:8-10`):

- 8 elements laid out along the line through the screen centre; each centred at
  `screenCentre + pointLight·m_pos`, scaled by `m_scale·(viewportWidth/800)`,
  drawn **additively** (`BlendModeAdd`), **flat-shaded** (`ShadeModeFlat`)
  (`efimage.cpp:724-726`). Blue elements tint `Color::Blue()`, white elements
  `Color::White()`.
- The `STAR_AURA` (surface 1, `f105bmp`) sits at `pos +1.00` = the projected
  light position and is the visible **sun aura** (each sector's light source);
  it is the only element native rotates, by `(screenX+screenY)/200`
  (`efimage.cpp:740-743`).
- Driven per-sector via `SetLightDirection(pcluster->GetLightDirection())`
  (`WinTrek.cpp:3614-3617, 3639`): `BlackSkyRenderer` normalises the cluster's
  `LightDirection` (toward the sun) and feeds it in.
- Occlusion: native composites the flare as a 2-D backdrop element with no
  per-ship depth test — the opaque scene (ships) is drawn afterwards and reads
  naturally over the flare (P547 depth is not needed; this is the faithful
  native behaviour). The flare pins its own depth-off / CullNone / additive /
  LinearClamp states (the P568 "pin render states" lesson) so it cannot be
  clipped by inherited depth/cull.
- **Native-fidelity note:** native's *active* `Render()` hardcodes
  `m_vec = Vector(0,0,1)` (`efimage.cpp:709`), which would point the flare
  straight ahead regardless of sector. But `WinTrek::SetCluster` explicitly
  calls `SetLightDirection(pcluster->GetLightDirection())`
  (`WinTrek.cpp:3639`) — the per-sector intent is unambiguous — so P589 uses
  the cluster light direction (the task's directive), and documents the `:709`
  hardcode as a dormant native leftover rather than the live-set path.

### Moons — `MoonRenderer.cs`

- 0-2 per sector seeded by `ClusterId` (`SpaceObjectMath.MoonCount`,
  SplitMix64 — deterministic, no `System.Random`).
- Native art: `moonbmp.mdl` / `moon7bmp.mdl` / `moonnebbmp.mdl` (the task's
  "unused native art"), colorkeyed + premultiplied via the shared
  `MdlTexture2D` helper.
- Placement is non-native ("non-native placement, native art"): each moon sits
  near the native planet — a deterministic 5-25° angular offset
  (Rodrigues rotation around a perpendicular axis) and a smaller angular size
  (25-60% of the planet's half-angle) — drawn with the same poster path as the
  planet (`SkyBillboard`, `efimage.cpp:515-563`).
- Lit consistently with the sector light: `SpaceObjectMath.MoonIllumination` =
  Lambert day-side with a 0.25 ambient floor, so the moon shows a crescent
  phase facing the sun (native has no moon; this is the task's "lit
  consistently with the sector light direction if the art allows").

### Distant galaxies / small nebulae cut-outs — `DistantObjectRenderer.cs`

- 2-4 per sector seeded by `ClusterId`, planet-like angular size (half-angle
  1-3.5°, the native planet is 2.5-5°), **dim** (brightness 0.35-0.85) so they
  never compete with ships/HUD.
- Sprites from `p589obj/` (under the artwork root) — real telescope objects cut
  out by `tools/space-objects/process.py` (luminance→alpha black-point lift,
  automatic point-source star removal outside the object envelope, soft radial
  border feather, ≤1024px). Straight-alpha PNGs are premultiplied at load so
  the feathered edges blend without fringes.
- Objects (7): Helix NGC 7293 (NOIRLab CC BY 4.0), Whirlpool M51, Ring M57,
  Sombrero M104, Pinwheel M101 (ESA/Hubble CC BY 4.0), Lion NGC 2392 (Webb MIRI
  CC BY 4.0 + NASA public-domain optical/X-ray). Sources/credits/licences in
  `docs/reports/P589_assets/sky-objects.json`.

### Deep-field galaxies + Little Red Dots — `GalaxyFieldRenderer.cs`

- **One batched/instanced draw:** every galaxy sprite from `p589gal/` (386
  individual galaxies segmented from the Hubble Ultra Deep Field by
  `tools/space-objects/build_galaxy_library.py`) is packed into a single atlas
  texture; each sector's ~420 galaxies + ~40 Little Red Dots are emitted as ONE
  `VertexPositionColorTexture` triangle list sampling the atlas — a single draw
  call for the whole field. Measured: **avg 2.7 ms** for the full
  `BlackSkyRenderer.Draw` (stars + planet + sun + moons + distant + galaxies)
  at 2560×1440.
- Log-distributed sizes from ~2 px up to a small disc (half-angle
  0.016-1.2°, well below planet size); random orientation each; dim
  (0.18-0.5); seeded per cluster.
- **Little Red Dots** are **procedural** (no imagery): a bright soft core +
  faint halo sprite generated at runtime, tinted per-instance from deep red to
  orange-red, always tiny (half-angle 0.0065-0.034° ≈ 1-4 px at 2560×1440).

---

## Native fidelity (every ported rule cited)

| Rule | Native source |
|---|---|
| Lens-flare element table (8 entries) | `effect/efimage.cpp:601-613` |
| Flare surfaces f104/f105/f106 | `effect/efart.h:8-10`, `efimage.cpp:643-656` |
| Additive + flat-shade render | `efimage.cpp:724-726` |
| STAR_AURA at +1.0 = sun, rotation | `efimage.cpp:740-743` |
| Poster billboard path (planet/moons/distant) | `efimage.cpp:515-563` (`PosterImage::Render`) |
| Per-sector light direction | `WinTrek.cpp:3614-3617, 3639` |
| Planet angular size | `efimage.cpp:515-563` |

---

## Tests (failing-first)

- `SpaceObjectMathTests` (Core, 14 tests): determinism (same cluster id ⇒
  identical placements; different ids ⇒ differ), moon count 0-2, moon smaller
  than planet + unit direction, `MoonIllumination` in [ambient,1] (full day-side
  toward light, ambient night-side), distant count range + availability,
  galaxy field count/log-sizes/dim/determinism, Little Red Dot tiny-red-band +
  determinism, and the exact native lens-flare table + surface names.
- `SpaceObjectLaunchOptionTests` (MonoGame, 6 tests): moons / distant objects /
  galaxies all default ON; each can be disabled.
- **Failing-first demonstrated:** with `GalaxiesOn` flipped to `false`,
  `Galaxies_DefaultToOn` FAILS; with the final default it passes. The pure
  `SpaceObjectMath`/`LensFlareElements` modules are brand-new (no pre-P589
  code existed), so their tests pin the behaviour they introduce (they could
  not compile/run against today's pre-P589 code — the same treatment P588 gave
  its brand-new dome-gating helpers).
- Result: MonoGame sky + P589 tests green; Core `SpaceObjectMathTests`
  14/14 green. Full suite numbers in the Suite section.

## Build

Whole solution `AllegianceCore.slnx` Debug: **0 warnings, 0 errors**.

---

## Proof (real window 2560×1440, P572 tooling)

`docs/reports/P589_assets/proof/` — real presented backbuffer frames
(real window, not `--capture`/offscreen), driven by `tools/p589-proof`
(the P572 tooling extended for the space objects):

- `{Alderan,Ganymede,Rigel}_sun.png` — camera at the sector's light direction:
  the native sun aura (`STAR_AURA`) + the full lens-flare ghost string (blue
  `MID_RING`s + white end rings) lined up through the centre.
- `{…}_sunstrip_00..11.png` (Alderan, 12 frames) — the frame strip turning
  ACROSS the sun: the flare elements slide correctly as the camera rotates.
- `{…}_planet_moons.png` — the native planet + its moon(s) (crescent phase lit
  by the sector light), stars, galaxies.
- `{…}_objects_galaxies.png` — distant-object cut-outs + the deep-field galaxy
  + Little-Red-Dot field on black.
- `{…}_readability.png` — a dark ship-silhouette wedge over the planet: ships
  stay readable against the objects behind them.
- `{…}_galaxies_off.png` / `{…}_moons_off.png` — the `--galaxies off` /
  `--moons off` toggles strip the field (visual confirmation of the toggles).
- **Frame time:** `BlackSkyRenderer.Draw` avg **2.7-3.5 ms**, max **5-15 ms**
  across 54 real frames (the max is the cold first frame / atlas build; the
  steady state is ~2.7-3 ms).

Asset QC (the task's "reject on the contact sheet"):
`docs/reports/P589_assets/sheets/contact_objects.png` and
`contact_galaxies.png` composite every candidate against pure black (the actual
in-game blend). The distant objects show clean single objects with no
rectangles/seams/banding; the three star-rich galaxies (Whirlpool/Sombrero/
Pinwheel) went through two tightening passes (a third star-detection scale +
a tighter object envelope) so no background specks float around them — the
bright knots inside their own spiral arms/disk are the objects' star-forming
regions and are correctly kept. The deep-field galaxy library's top tiers are
clean, varied morphologies/colours; the lower tiers are tiny/faint (6240
components → 386 kept; 4154 sub-min-area specks, 1578 compact bright star
cores, 49 frame-edge, 73 neighbour-bleed rejected).

---

## Suite

Three consecutive full-suite runs on the final code (P471 runner). P589's own
tests are green in **every** run (Core `SpaceObjectMathTests` 14/14 + MonoGame
`SpaceObjectLaunchOptionTests` 6/6 + the P576/P577/P588 sky tests); every
reported failure is a documented rotating live-wire timing load-flake in
`docs/TEST_FLAKES.md` (e.g. `LiveWinConditionTests.Board_refused_once_mission_over`
ledger:34, `LiveTreasurePickupTests.Rescue_ship_picking_cash…` ledger:157,
`MineCrossSideVisibilityLiveTests.Enemy_ship_spots…` ledger:184,
`GameOverPlayersWireTests…` ledger:30/206, `ThreePeerSeededSessionTests…`) —
none involve P589 code. Runs were reported as they happened, not re-run to force
a clean pass (the task's "report each run as it happened" rule).

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|-----------|------|
| 1 | 12/12 | **1644/1644** | 559/561 | 3828/3833 |
| 2 | 12/12 | **1644/1644** | **561/561** | 3828/3833 |
| 3 | 12/12 | **1644/1644** | **561/561** | 3831/3833 |
