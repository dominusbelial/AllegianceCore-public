# P589b — P589 review fixes: filled galaxies, no star backgrounds, round moons

Per Claude's review of P589's own assets (2026-10-08) and Domingo live:
1. The deep-field galaxy library rendered as HOLLOW SQUARES / frame edges (the
   galaxy went to alpha 0 and the tile border survived).
2. Hero objects (Sombrero especially) still carried field stars ("we don't want
   photos with star backgrounds, but the natural object itself").
3. Moons rendered as SQUARE rock-texture tiles ("some really don't make sense").

All three are fixed with failing-first tests. Everything is committed on
`master` (not pushed).

---

## 1. Galaxy field: hollow squares → filled galaxies on black

### Root cause (one-character inversion)

`tools/space-objects/build_galaxy_library.py` `feather_alpha` computed its
soft alpha from `ndimage.distance_transform_edt(~mask)` — the distance from the
BACKGROUND to the object. That is 0 *inside* the object and grows *away* from
it, so the raised-cosine mapping gave the galaxy interior alpha 0 and the box
margin around it alpha up to 1: the sprite became the surrounding frame, i.e. a
hollow rectangle with an empty centre. The generator's own cosine `window`
vignette only zeroed the outermost ~1 px, so the frame band survived a few
pixels inside the border (visible as faint little squares across the real-window
sky).

Fix: use `distance_transform_edt(mask)` (distance to the nearest object pixel:
0 in the background, growing toward the object's medial axis), so the same
raised-cosine maps core→1, edge→0 — the object stays FILLED and fades to
transparent in the margin. Documented at `feather_alpha` in
`build_galaxy_library.py`.

### Automated check (failing-first)

`tools/space-objects/check_galaxy_library.py` asserts, per sprite:
- alpha at the tile border ≈ 0 (no frame edge),
- alpha at the brightest raw-RGB object pixel is clearly high (the core is not
  hollow) and a fully-opaque interior exists (max alpha ~1),
- the alpha mass sits away from the border (centre not transparent).

Failing-first on the pre-fix library: **379/386 sprites fail**
(`bright_pixel_alpha≈0.00x` — the galaxy core was transparent). After the fix
the rebuilt 233-sprite library passes **233/233**.

Rebuild (`build_galaxy_library.py` now also purges `--out` so stale frames can
never linger, and the invariants are enforced in-loop with a
`rejected_alpha_invariant_p589b` tally in `galaxy-library.json`):
- 6240 initial components → 457 after star/spike/edge rejection → 270 selected
  → 37 rejected by the new alpha invariant (tiny galaxies whose feather never
  reaches a fully-opaque core) → **233 retained** (within the 150-300 spec).
- `sheets/contact_galaxies.png` now shows filled galaxies with soft edges on
  black, no outlines.

The sheet is generated in `docs/reports/P589_assets/sheets/contact_galaxies.png`;
sprite copies refreshed in `sprites/galaxies/` (233).

---

## 2. Hero objects: remove the field-star background, keep the object

### Root cause

`tools/space-objects/process.py` removed compact stars only *outside*
`dilate(obj_ext, keep_pad)` and multiplied alpha by a slowly-decaying blurred
envelope, so (a) sharp foreground stars just off the disk and in the outer halo
kept residual alpha, and (b) superimposed PSF stars on the disk were fully
protected. Result: "photos with star backgrounds".

### Fix

Two changes in `process.py`:
- **Outside the object envelope → alpha 0.** The final alpha is multiplied by a
  *hard* envelope (`distance_transform_edt(obj_ext)` smoothed over a small
  margin) that is exactly 0 in the surrounding sky and 1 over the object — so no
  scattered background star specks can float around it (step 3b).
- **Conservative on-object PSF-star removal.** A foreground star is a SHARP,
  TINY, ISOLATED point (Airy disc + spikes) even when silhouetted on the disk,
  whereas the object's own knots / H II regions are extended blobs. A new
  `onobj_star` pass removes only the small connected sharp components (≤ ~9-10
  px at working res) on the object, sparing the disk/H II texture (step 2b).
  It is enabled for the galaxies (Whirlpool/Sombrero/Pinwheel) and OFF for the
  planetary nebulae (Ring/Helix/Lions) whose central white-dwarf star is part of
  the object.

### Automated check (failing-first)

`tools/space-objects/check_hero_objects.py` counts compact bright points on dark
surroundings per object (per-object ceilings chosen so the pre-fix library FAILS
and the fixed library PASSES):

| Object        | pre-fix | fixed |
|---------------|---------|-------|
| whirlpool-m51 | 5  FAIL | 2  OK |
| ring-nebula-m57 | 1 FAIL | 0 OK |
| sombrero-m104 | 14 FAIL | 4 OK |
| pinwheel-m101 | 65 FAIL | 52 OK* |

\* Pinwheel is a face-on spiral whose inter-arm H II knots are morphologically
near-identical to points on dark gaps; its ceiling (55) tolerates those while
still catching a real star-background regression. The galaxy STRUCTURE is
preserved in every object (verified visually — spiral arms, dust lane and H II
regions remain crisp; only the point-source field stars are gone).

`sheets/contact_objects.png` regenerated; sprites refreshed in
`sprites/distant_objects/`.

---

## 3. Moons: square texture tile → lit circular disc

### Root cause

The native `moonbmp`/`moon7bmp`/`moonnebbmp` art is a SURFACE texture for the
3D `moon.mdl` sphere, not a disc image; `MoonRenderer` drew it as a flat
billboard (`SkyBillboard`, the same square-poster path as the planet), so the
unclipped surface texture appeared as a mottled SQUARE.

### Fix (`MoonRenderer.cs`)

New `MoonRenderer.GenerateLitDisc` (pure, device-free) builds a lit circular
disc from the surface texture per moon:
- a **circular alpha mask** clips the square's corners — alpha → 0 at the
  silhouette rim (no square edge at any size);
- a **Lambert terminator** from the sector light direction shades a day side
  down to an ambient night floor — a crescent/gibbous phase consistent with the
  sun, like the native planet posters.

The moon's sphere normal is reconstructed per texel from the billboard basis
(`right`, `up`, facing) and dotted with the real light direction. `moonnebbmp`
flows through the same path. Generated once per sector in `SetCluster` (≤2
moons).

### Failing-first test

`tests/.../P589bMoonDiscTests.cs` (headless, 4 facts) asserts:
- corners of the bounding square are transparent (not a square tile),
- the centre facing the light is full alpha / bright,
- a side light produces a day/night terminator with the night side at the
  ambient floor,
- the disc edge fades before the square corners.

Pre-fix the class could not exist (no `GenerateLitDisc`); post-fix it passes
4/4.

---

## Proof (real window 2560×1440, backbuffer)

Re-ran `tools/p589-proof` (opens a REAL 2560×1440 window and saves each
PRESENTED backbuffer via `GetBackBufferData` — not `--capture`/offscreen, the
path that hid the P547/P562 regressions):

- `docs/reports/P589_assets/proof/{Alderan,Ganymede,Rigel}_planet_moons.png` —
  the moon is now a round, gibbous disc lit from the planet's light side (no
  square tile; verified visually).
- `docs/reports/P589_assets/proof/{...}_objects_galaxies.png` — the deep field
  shows FILLED galaxy blobs (no hollow squares in the sky).
- `docs/reports/P589_assets/proof/p589b_crops/*_galaxy_{0..3}_400.png` — 400%
  crops around galaxy-field sprites per sector, each a filled soft-edged blob,
  no square frame.
- `.../readability.png`, `.../galaxies_off.png`, `.../moons_off.png`, and the
  `sunstrip` re-taken with the new library.

Frame time: `BlackSkyRenderer.Draw` **avg 2.655 ms, max 4.324 ms** across 54
real frames (steady state ~2.7 ms; the max is the cold first frame).

---

## Build & suite

Whole solution Debug: **0 warnings, 0 errors**.

Three consecutive full-suite runs on the final code (P471 runner; each test
project run 3×). P589b's own tests are green in EVERY run — the 4 new
`P589bMoonDiscTests` (part of MonoGame 1648) plus the Python
`check_galaxy_library.py` / `check_hero_objects.py` (run separately, failing-
first above). Every reported failure is a documented rotating live-wire
timing load-flake in `docs/TEST_FLAKES.md`, none involving P589b code (the
only code change in a compiled assembly is `MoonRenderer` in the Game project;
nothing in Core/ClientCore logic was touched). Runs were reported as they
happened, not re-run to force a clean pass.

| Project | Run 1 | Run 2 | Run 3 |
|---------|-------|-------|-------|
| Lobby | 12/12 | 12/12 | 12/12 |
| MonoGame | 1648/1648 | 1648/1648 | 1648/1648 |
| ClientCore | 561/561 | 559/561 | 560/561 |
| Core | 3828/3833 | 3831/3833 | 3828/3833 |

ClientCore failures (all `docs/TEST_FLAKES.md` live-wire):
`ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`
(ledger: 134-ish, same watch-latch family), `FullSessionKillIntegrationTests.
FullSession_ThreePeers_…CrossSideKill` (ledger: cross-assembly live-wire
timing).

Core failures (all live-wire timing, ledger entries below):
`LiveWinConditionTests.Board_refused_once_mission_over` (ledger:34),
`LiveStationRescueTests.Lifepod_reaching_rescue_station_…` (ledger:156),
`LoadoutChangeLiveTests.Boarding_and_leaving_…` (ledger: load-flake),
`LiveRipcordTests.Re_target_activates_to_requester_only` (ledger: load-flake),
`LiveChatSectorWireTests.Individual_AllSector_…` (ledger: load-flake),
`AddPartTests.Part_pickup_decline_recreates_treasure_…` (ledger:247),
`GameOverPlayersWireTests.Win_by_elimination_broadcasts_…` (GameOverPlayers
family, ledger:30/206). Sets rotate run-to-run (ledger:440).
