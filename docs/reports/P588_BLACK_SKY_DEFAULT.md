# P588 — new DEFAULT sky: black background + the old point stars + the native planet + space dust; nebula domes (native & HD) become opt-in only

## Summary

Per Domingo (2026-10-08, live review): *"keep the old black sky; keep the
native planets only, with the space dust we had; I'd rather have the black
background; the sky looks like improperly stitched fabric."* Added a **`black`
sky tier and made it the DEFAULT**: a pure black backdrop with the restored
**pre-P576 point starfield** (the three parallax `Starfield` shells), the
**native planet poster** per sector, and the unchanged ambient **space dust** —
and **no nebula dome**. The P576 native dome and P577 HD dome become **opt-in**
(`--sky native`, `--sky hd`). The aleph destination-glimpse in black mode shows
the destination's black sky + its planet + stars.

"Improperly stitched fabric" is the dome/cube mapping seams (P577 cube faces /
P576 dome UVs) — see "Dome seam cause" below. It no longer matters for the
default because there is no dome.

---

## Implementation

### New files

- `Rendering/BlackSkyRenderer.cs` (P588) — the black tier, an `ISkyRenderer`.
  `SetCluster` carries only the planet; `Draw` draws the **restored pre-P576
  starfield shells** (exact old params, `Starfield` three layers centred on the
  origin, near brighter/sparser → far dimmer/denser) and then the native planet,
  all camera-relative under the sky's own large-far projection
  (`NativeSkyPlacement.SkyProjection`), with pinned render states (P568 lesson).
  **No dome by design.**
- `Rendering/PlanetPoster.cs` (P588) — the per-sector native planet billboard,
  extracted so the native, HD and black tiers all draw the **identical** native
  alien planet poster (P576 placement/size, camera-relative, no far clipping).
  Draws with the same proven vertex/effect path as the native dome
  (`VertexPositionColorTexture` + `VertexColorEnabled`, `AlphaBlend`).

### Changed

- **Dispatch** (`Game1.Rendering.cs` `DrawSkyP576`): black (default) →
  `_blackSky.Draw`; native → `_nativeSky.Draw`; hd → `_hdSky.Draw`; the
  pre-cluster fallback stays the three parallax starfields (identical to the
  black sky minus the planet).
- **Default tier = `black`**: `LaunchOptions.SkyTier` now defaults to `"black"`
  and adds pure helpers `EffectiveSkyTier(requested)` (resolves unknown → black;
  unit-tested "default resolves to black") and `SkyTierDrawsDome(tier)` (false
  only for black; unit-tested "black draws no dome"). `Game1` stores the
  resolved tier; `Program.cs` parses `--sky black|native|hd` with black default.
- **Aleph glimpse** (`AlephGlimpseRenderer.cs`): the `SkyIsHd` bool became a
  `SkyTier` string; the destination glimpse constructs `BlackSkyRenderer` when
  the arriving tier is black, so the glimpse shows the destination's black sky +
  its planet (+ stars).
- **Credits screen** (`CreditsOverlayRenderer.cs`): `ShowHdCredits` — the HD
  telescope attribution table (F12) is shown **only when `--sky hd` is actually
  in use**; otherwise a note explains only native art is in use (black/native).
- `Game1.Lifecycle.cs`: constructs `_blackSky`; wires `SkyTier` into the glimpse
  renderer and `ShowHdCredits` into the credits renderer.
- `Game1.cs` / `Game1.Session.cs` / `Program.cs`: settings/help text updated for
  the new default and the `black|native|hd` options.

### Star-layer choice (the task's "pick one and justify")

Used the **pre-P576 `Starfield` shells** (restored exactly — same radii /
count / seeds / brightness as the original creation in
`Game1.Lifecycle.cs:303-308`), NOT the P576 seeded stars, because that is
literally the "old black sky" Domingo asked to keep. The P576 seeded stars sit
on a shell at infinity (no parallax); the finite bands of the restored shells
give the real motion parallax the old look had — the visual cue that made the
pre-P576 sky feel alive.

### Native fidelity

The planet is the P576 placement/size, ported from
`clusterIGC.cpp:44-57` (`AddPoster` → `PosterImage::AddPoster`,
`trekigc.cpp:243-248`, `efimage.cpp:515-563`), already cited in
`docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md`; the black tier reuses it
verbatim through `PlanetPoster`.

---

## Dome seam cause ("stitched fabric") — noted for later

The "improperly stitched fabric" Domingo saw is the seam/stretch of the dome &
cube mappings:

- **P577 HD**: the 6-face NASA **cube** star skybox shows face seams and corner
  gaps in the real window (`HdSkyRenderer` originally built a cubemap).
- **P576 native dome**: the `globeN` sphere is textured with an equirectangular
  `environmentNbmp`; equirect->sphere mapping stretches at the poles and
  introduces a visible meridian seam along the texture wrap.

Neither matters for the new default (no dome). If the domes are ever wanted
again, a follow-up can fix the seams (e.g. the HD tier already moved off the cube
to a seam-free equirect `stars_equirect`, per P577b) and double-cover/rotate the
dome UV wrap.

---

## Tests (failing-first)

- `SkyLaunchOptionDefaultsTests` (updated + extended): **`SkyTier_DefaultsToBlack`**
  — fails on the pre-P588 default (`native`), passes after; `SkyTier_DefaultResolvesToBlack`
  (unknown → black); `SkyTier_EachTierCanBeSelected`; `BlackSky_DrawsNoDome`
  (`SkyTierDrawsDome("black")==false`, native/hd true); aleph glimpse defaults off.
- `SkyP577LaunchDefaultsTests` (updated): native/hd remain selectable tiers
  (native is no longer the default).
- Failing-first demonstrated: `SkyTier_DefaultsToBlack` run against the default
  reverted to `native` → 1 failed; with the new default → passes. The dome-gating
  helpers are brand-new functionality (the pre-P588 code had no dome/no-dome
  distinction at all).

Result: 41/41 sky tests green (incl. `HdSkyGuardTests` 12, `NativeSkyPlacementTests`,
`SkyHdManifestValidationTests`).

## Build

Whole solution `AllegianceCore.slnx` Debug **and** Release: **0 warnings, 0
errors**.

---

## Proof (real window 2560×1440, P572 tooling)

`docs/reports/P588_assets/proof/` — real presented backbuffer frames (real
window, not `--capture`/offscreen):

**Black default (the three sectors × 4 directions + a planet-centred view):**
- `black_{Alderan,Ganymede,Rigel}_d0..d3.png` — pure black sky with point
  stars; **no nebula dome, no seams, no black wedges** (checked from 4 camera
  directions per sector).
- `black_{Alderan,Ganymede,Rigel}_planet.png` — the **native planet poster**
  visible at its native direction/size (nebplnt06 Earth-like for Alderan/Rigel,
  nebplnt33 for Ganymede), black background, point stars, no dome.

Built by `tools/blacksky-proof` (new, committed): drives the **actual**
`BlackSkyRenderer`/`NativeSkyRenderer`/`HdSkyRenderer` into a real 2560×1440
window's backbuffer and SaveAsPng's each presented frame. Frames render from the
**main game loop** (one scene per `Draw` tick) so the capture is a real
presented frame — rendering in `Initialize()` proved unreliable for the small
planet quad (the dome captured but the planet didn't); the main-loop tool
confirms the planet visibly renders.

**Opt-in tiers still work (same real-window tool + live client):**
- `native_Alderan_d0.png` / `native_Alderan_planet.png` — the P576 nebula dome
  + planet still draw.
- `hd_Alderan_d0.png` / `hd_Alderan_planet.png` — the P577 HD dome + planet
  still draw.

**Live real client (end-to-end, with the ambient space dust, HUD, ship) —**
`scripts/live-proof.sh`, default and per-tier launch:
- `live_default_black_Omega.png` — the DEFAULT sky is black with stars and the
  space dust (no dome; colored pixels are the afterburner/HUD only).
- `live_native_dome_Omega.png` — `--sky native` still shows its nebula dome.
- `live_hd_dome_Omega.png` — `--sky hd` still shows the HD nebula dome.

(The 221-map live client starts in Omega; live auto-goto to Alderan/Ganymede/
Rigel is the documented navigation limitation of `docs/LIVE_PROOF.md`, so the
3-sector black coverage comes from the real-window `blacksky-proof` tool driving
the actual renderers.)

---

## Suite

Three consecutive full runs via `scripts/run-tests.sh` on the final code:

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|-----------|------|
| 1 | 12/12 | **1638/1638** | 560/561 | 3815/3819 |
| 2 | 12/12 | **1638/1638** | 559/561 | 3813/3819 |
| 3 | 12/12 | **1638/1638** | **561/561** | 3815/3819 |

Every failure is a **documented live-wire timing load-flake** in `docs/TEST_FLAKES.md`
(the rotating live-socket set), unrelated to P588 — e.g. run 1 `FullSessionKillIntegration…
CrossSideKill` (ledger:33), `LiveRipcordTests.Cancel_while_active_aborts_sector_wide`
(ledger:417), `LiveCombatWireTests.Moving_target_streams…` (ledger:406), plus the
rotating `CargoBayReload`/`ShipsUpdateLive` members; run 2 `LiveTeamLobbyTests.Unready`
(ledger:395), `GameOverPlayersWireTests.Win_by_elimination`/`Conquest_win_in_one_mission`
(ledger:30/206), `AsteroidMinedLiveTests.miner_hits_capacity` (ledger:268-272),
`ClientWarpApproachSlowHullLiveTests` (ledger:215); run 3 `AutoReloadOnEmpty`, `LiveChatSectorWire`.
**P588's own sky tests are in the MonoGame 1638/1638 total every run**
(`SkyLaunchOptionDefaultsTests` + `SkyP577LaunchDefaultsTests` green 3/3). Per the
task's "report each run as it happened" rule the suites were not re-run to force a
clean pass — the failing tests are the ledger's known rotating live-wire flakes.
