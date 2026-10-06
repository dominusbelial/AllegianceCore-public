# P546 — Enhanced Explosions & Smoke (adopt the P543 spike into the game)

Branch: `master`. Ports the P543 VFX spike (branch `spike/p543-vfx`, commit
`cd4d501f`) into the **game** renderers (not the capture tool), so the proof
renders the real code path. Enhanced is the default; Native (the exact P263/P545
path) stays one switch away for parity checks. Bloom + soft particles remain out
of scope (they need compiled `.fx`; no Wine here) — kept documented for later.

---

## What was ported, from where

All new renderers live under `src/Allegiance.ClientCore.Game/Rendering/` and
share P545's `ParticleBillboardBatcher` (no second batcher) and the game's
content loading:

| New file | Origin (spike `tools/roster-capture`) | Role |
|---|---|---|
| `VfxAssets.cs` | `VfxAssets.cs` (adapted to game content) | Loads the 6 CC0 flipbook atlases from `Content/Vfx/atlas` via `TitleContainer`; slices grids like `ExplosionAssets`. |
| `EnhancedSmokeField.cs` | `Cc0SmokeField.cs` (renamed, reworked onto the batcher) | Animated CC0 smoke, **premultiplied**, sector-lit (P530), near-camera fade, screen-wide density budget. |
| `EnhancedExplosionRenderer.cs` | `VfxEnhancedRenderer.cs` (reworked onto the batcher) | CC0 fireball (additive, capped) + flash + sparks + native `fx18` ring + premultiplied smoke; size/timing from `ExplosionSampler`. |
| `EnhancedVfx.cs` | — (shared helpers) | `ParticleNearFade`, sector-light tint, premultiplied colour, hashes. |
| `ParticleDensityBudget.cs` | spike's `MaxSmokePerExplosion` idea (generalised) | Oldest-first-eviction screen budget. |

The spike's `tools/roster-capture` copies were **not** merged; instead the game
classes are used, and `roster-capture vfx` (new `VfxProof546.cs`) calls the
**game** renderers so the proof exercises the real path.

### Smoke everywhere (Enhanced)
Missile trails, engine trails and damage-debris smoke route through the shared
`EnhancedSmokeField` (animated CC0 smoke, random start frame + rotation,
premultiplied, sector-lit, near-faded). Fire puffs stay on the native art,
additive and intensity-capped. Both paths stay tested (Native = the byte-for-byte
P545 path).

## Assets — packaging & licences (no ALLEGIANCE_ARTWORK_PATH)

Unlike native `.mdl` art (read from the artwork tree), the CC0 atlases are **ours
to ship**. They are committed under
`src/Allegiance.ClientCore.MonoGame/Content/Vfx/`:

```
Content/Vfx/
  README.md                 ← per-file licence table
  licenses/*.txt            ← CC0 licence texts (Unity + Kenney archives)
  atlas/{Explosion00,Explosion01,Explosion01-light,Cloud01,WispySmoke01,DiscSmoke01}.png
```

- **DesktopGL:** `<None Update="Content/Vfx/..." CopyToOutputDirectory="PreserveNewest" />`
  copies them into the output `Content/Vfx/`; `VfxAssets` reads them via
  `TitleContainer.OpenStream("Content/Vfx/atlas/<stem>.png")`.
- **Android:** the existing `scripts/pack-android-assets.sh` copies the compiled
  DesktopGL `Content/` (including `Vfx/`) into `Assets/Content`, so the same
  `TitleContainer` path resolves inside the APK. Verified the PNGs propagate to
  `roster-capture`'s output Content too.
- `Explosion00.png` is committed (used by the fire-ball set) though the current
  renderer draws `Explosion01`/`Explosion01-light`; kept for the atlas set + a
  future variant.
- Total atlas VRAM (RGBA8 upload): **9.89 MiB** (the six atlases + the
  procedural soft disc). Native explosion art: **47.66 MiB**.

## The setting (Enhanced default)

- `LaunchOptions.EnhancedVfx` defaults **true** (`Game1.Session.cs`). When false
  (Native), Game1 constructs **none** of the enhanced renderers / does not load
  the CC0 atlases — the exact P263/P545 path runs.
- CLI flag `--fx native|enhanced` (`Program.cs`) for parity captures.
- Default is asserted by unit test (`Default_setting_is_enhanced`).

## Near-camera fade & density budget

- **Near-fade:** `ParticleNearFade.Flight` = gone below **6 u**, full above
  **45 u**. Re-picked against the real chase camera, which sits
  `distanceBehind=350` / `heightAbove=90` scaled by `shipRadius/95`
  (`ChaseCamera.ReferenceRadius`) ≈ **30 u** behind a fighter, **76 u** behind a
  bomber. 45 u full keeps every trail/ship puff at/behind the camera's own ship
  visible; 6 u gone clears only smoke literally at the eye. (The spike's 2 m/18 m
  were measured offscreen.)
- **Density budget:** `EnhancedExplosionRenderer.MaxSmokePerExplosion = 14`
  (per blast) and `EnhancedSmokeField.MaxSmokeOnScreen = 320` (screen-wide, all
  emitters share one field). The `ParticleDensityBudget` evicts the **oldest
  first** under load; the explosion smoke count formula is clamped to
  `[5, MaxSmokePerExplosion]`.
- Fire is intensity-capped: `MaxFireDecals = 8`, `FireballMaxAlpha = 0.75`
  (additive but below white); smoke is premultiplied so overlapping puffs
  accumulate translucency, never clipping to white.

## Native vs Enhanced proof (game path, `roster-capture vfx`)

Sheets written to `docs/P305_roster/p546/`:
`fighter.jpg`, `station.jpg`, `dense_combat.jpg`, `volley_t0.2/.5/1.0.jpg`.
Measured this session (all through the game renderers):

### Dense combat — blown-out share (6 overlapping fighter deaths at fireball peak, t=0.9s)
| Variant | Blown-out share (all channels ≥ 250) |
|---|---|
| Native (P263 additive) | **100.00%** |
| Enhanced | **6.66%** |

(Spike target ≤ ~15% — comfortably beaten. The Native turns the fight into a
white void; Enhanced keeps the fighters/impacts readable.)

### Player-flight-camera 4-missile volley (smoke screen coverage, alpha>0.5)
Blown-out was 0.00% for every variant at every step (premultiplied smoke does
not clip to white).

| t (s) | Native cover | Enhanced | Enhanced + near-fade | near-fade reduction |
|---|---|---|---|---|
| 0.2 | 3.37% | 3.37% | **1.14%** | **66%** (≥ ~50% ✓) |
| 0.5 | 0.83% | 0.83% | 0.76% | 8% |
| 1.0 | 0.38% | 0.38% | 0.37% | 2% |

The near-fade clears the smoke that would otherwise sit in front of the flight
camera at launch while leaving the four missile puffs visible (confirmed
visually in `volley_t0.2.jpg`).

### Cost (offscreen, fixed 1280×720, 30-frame avg; final code)
Three runs of the final configuration:

| Run | Native ms | Enhanced ms |
|---|---|---|
| 1 | 1.062 | 1.246 |
| 2 | 0.989 | 1.030 |
| 3 | 0.942 | 1.106 |

Avg **Native ≈ 0.998 ms** vs **Enhanced ≈ 1.127 ms** → **+0.13 ms (+~13%)**,
range +0.04…+0.18 ms. ±0.1 ms run-to-run (same caveat as the spike). VRAM
added: **9.89 MiB** (CC0 atlases, RGBA8).

## Live sanity (Enhanced, real server + client)

Ran the real client against a local `Allegiance.Server` (`scripts/run-tests` box;
livecombat demonstrated 8 real bots fighting over the wire) in Enhanced for 60 s
(`--capture`): CC0 atlases loaded, enhanced renderers active, entered Flying, 61
frames captured, **no exceptions / no render-state leaks**. Screenshot
`docs/P305_roster/p546/live_enhanced_shot.png` (captured frame ~24 s): intact
space scene + fully legible HUD (gauges, minimap, brackets), enemy ship visible,
no artifacts or washed-out white.

## Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/EnhancedVfxTests.cs` (13 tests):
default setting is Enhanced; near-fade factor at/inside/outside N/M; density
budget evicts oldest first; smoke is premultiplied (rgb ≤ α) and fire is capped;
the 6 CC0 atlases ship in game Content with the expected grids (clean-checkout,
no artwork path); explosion timing/size unchanged from `ExplosionSampler` for
every preset. All pass (`Passed! ... Passed: 1308` in the MonoGame assembly).

Full suite (3× `scripts/run-tests.sh`, P471): see Suite results below. All
failures observed are documented live-wire/load flakes (see `docs/TEST_FLAKES.md`);
**none are in the changed assemblies** — `Allegiance.ClientCore.MonoGame.Tests`
passed **1308/1308** on every run.

### Suite results (3 consecutive runs, final code)

All three runs via `scripts/run-tests.sh` (P471). Every run: Lobby 12/12 ✓,
**MonoGame 1308/1308 ✓** (the changed assembly; includes the 13 new
EnhancedVfxTests), ClientCore 530, Core 3745. The only failures are pre-existing
**live-wire / load flakes** in the untouched `Allegiance.ClientCore` /
`Allegiance.Core` assemblies — the rotating set documented in
`docs/TEST_FLAKES.md` (which notes they "rotate run-to-run and do not reproduce
when the assemblies run in isolation"). None touch this task's renderers.

| Run | MonoGame | Failures (documented flakes only) |
|---|---|---|
| 1 | 1308/1308 ✓ | Core ×6: StationsUpdateLiveTests.Visible_station…; LiveRipcordTests.Cancel_while_active…; LiveChatSectorWireTests.Individual_AllSector…; ConquestTerritoryLiveTests.Conquest_win_in_one…; CargoBayReloadTests.Reload_malformed…; AsteroidMinedLiveTests.No_broadcast… |
| 2 | 1308/1308 ✓ | ClientCore ×1: FullSessionKillIntegrationTests.FullSession_ThreePeers…; Core ×4: SideEliminationLiveTests.Eliminating_side2…; LoadoutChangeLiveTests.Boarding_and_leaving…; ConquestTerritoryLiveTests.Conquest_win_in_one…; AddPartTests.Part_pickup… |
| 3 | 1308/1308 ✓ | Core ×3: LiveRipcordTests.Ship_anchor_insufficient_energy…; GameOverPlayersWireTests ×2 (Conquest_win_in_one… / Conquest_win_lists…) |

Reported as each run happened; the flake set rotated run-to-run exactly as the
ledger predicts, and the changed assembly was green on all three.
