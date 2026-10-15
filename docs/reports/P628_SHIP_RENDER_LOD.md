# P628 — Client rendering level of detail: distant ships as cheap lit sprites, near ships as full models

## Summary

The client previously drew **every** visible ship as a full 3D `.mdl` model
(`ShipRenderer.cs:123` `Draw`, `NativeShipRenderer.cs:194`) plus engine trails,
motion ribbons and side tags, costing a full model draw per ship regardless of
on-screen size. This task adds a two-tier render LOD:

- **Native LOD first**: the `.mdl` loader now **retains** the `LODGeo`
  distance-detail levels it previously discarded, and the native ship renderer
  selects the level by projected pixel radius exactly like native.
- **Far tier (modern)**: below a configurable projected-pixel-radius threshold a
  ship becomes a cheap, lit, **side-tinted sprite/point**, batched one draw call
  per hull type, with **no engine trails or motion ribbons** at that tier and a
  **crossfade (no popping)** between tiers.
- **Near tier** keeps the full model (now using native `LODGeo` levels).
- Brackets, side tags and targeting stay correct for every tier (they are
  screen-projected from the ship's world position, independent of how the hull
  draws).

Measured on the real 2560x1440 client window against a local server (before /
after, same machine):

| metric | BEFORE (LOD off) | AFTER (LOD on) |
|---|---|---|
| ship-pass draw calls, wide chase, ~63 ships (scale 5) | 63–76 | 11–23 |
| ship-pass draw calls, wide chase, ~148 ships (scale 13, 390 players) | ~148 (1:1) | **13–16** |
| frame time | ~17.1 ms (60 fps vsync-capped) | ~17.0 ms (60 fps vsync-capped) |

At a wide battle view the ship pass drops from **one draw call per ship** to
**a handful of batched sprite calls + the few near full models** — a ~3.5–4x
reduction at 63 ships and a ~10x reduction at 148 ships (390 players). Frame
time is unchanged because the client is vsync-capped at 60 fps on this machine
(draw-call savings do not push FPS past the cap); the draw-call reduction is
the measured win and is what lets the planned 680M iGPU mini PC keep up.

---

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Select LOD by projected on-screen pixel radius | `Context::GetImageRadius(pos,radius)` `context.cpp:1858-1871`; `ThingGeoImpl::Render` `SetLOD(screenRadius*lodBias)` `thinggeo.cpp:1618-1624`, bias default 1 `thinggeo.cpp:1674` | **design** | `ShipLod.ProjectedPixelRadius` (analytic sphere projection), used for both the far tier and native LOD level choice. |
| `LODGeo` levels chosen by radius, `m_radii` sorted | `LODGeoImpl::AddGeo` `geometry.cpp:1667-1713`; `LODGeoImpl::Render` `geometry.cpp:1760-1773` | **design** | Loader retains the levels; `NativeShipRenderer` sorts them descending (`DescendingOrder`) and `ShipLod.NativeLodLevelFor` picks the level. |
| Fixed level thresholds tuned for 2000s GPUs | the baked `(pixels, geo)` values (fig05: 8/32/64) | **era constraint** | Intent kept (pixel-radius metric); the **far-tier** threshold is a tunable constant sized for today (`ShipLodProfile`, env-overridable). Native's own baked LOD levels are honoured as-authored. |
| Radar per-hull icons / radar LOD levels | `WinTrek/radarimage.cpp:2` `c_szRadarLODs`, `SetRadarLOD:240`; `hullTypeIGC.cpp:51` | design | Out of scope here (radar already has `RadarLod`); unchanged. |

---

## Scope item 1 — Native LOD first

**Finding (spot-check corrected).** The `.mdl` loader's `LODGeo` branch previously
did `stack.Pop()` twice, keeping the first node and discarding the second, labelled
"keep only the base mesh". Probing real art showed the base mesh is correctly kept
(fig05 parses to its 2 base chunks), but the `lodList` — the actual `LODGeo`
distance-detail levels — was thrown away. Enabling native LOD therefore required
retaining that list.

Reading the binary list structure on **fig05.mdl / utl18a.mdl / bgrnd05.mdl** (all
carry a `LODGeo`), each of the 3 levels is a pair with **the geo first and the
pixel-radius float second** — i.e. `(geo, pixels)`, the reverse of the
`(pixels, geo)` reading one might assume from `modeler.cpp:1676-1683`'s
`GetFirst()/GetSecond()`. `CaptureLodLevels` (`MdlFile.cs`) reads `pair.B` as the
threshold and `pair.A` as the geo, resolving the geo to meshes.

- `MdlFile.cs`: new `MdlLodLevel { PixelRadius, Meshes }` and `MdlFile.LodLevels`;
  the `LODGeo` `Apply` branch calls `CaptureLodLevels` instead of discarding.
- `NativeShipRenderer.cs`: `LodLevelCount`, `LodPixelRadii` (descending, native
  `AddGeo` order), `LodLevel(i)` (lazy-built GPU geometry, cached). A broken
  level falls back to the base mesh.
- `Game1.Rendering.cs` (ship loop): when fully in the near tier
  (`farAmt <= 0`), picks `lev = ShipLod.NativeLodLevelFor(px, radii)` and draws
  that LODGeo geometry; base otherwise.
- `ShipLod.NativeLodLevelFor` — the port of `LODGeoImpl::Render`
  (geometry.cpp:1760-1773): choose the coarsest level whose threshold ≥ the
  projected pixel radius.

**Spot-check proof.** `Fig05_retains_lodgeo_levels_for_native_lod` in
`tests/Allegiance.Core.Tests/MdlParseTests.cs` parses the real fig05.mdl and
asserts: 3 LODGeo levels, each with a positive pixel threshold and meshes no
heavier than the base, spanning the expected 8/32/64 ladder.

---

## Scope item 2 — Far tier (modern)

- `ShipLod.cs` (pure): `ProjectedPixelRadius`, `FarAmount`, `SpriteAlpha` /
  `ModelAlpha` (crossfade), `NextFarState` (hysteresis). Defaults:
  `FarPixelRadius=7 px`, `EnterFullPixelRadius=12 px`, `FadeBandPixelRadius=5 px`;
  env `ALLEGIANCE_LOD_FAR_PX / _ENTER_PX / _FADE_PX`.
- `ShipLodSpriteRenderer.cs`: packs every far ship of the same hull look key
  (the native texture stem, else a soft dot) into **one batched**
  `DrawUserIndexedPrimitives` billboard call per type — the
  `ParticleBillboardBatcher` / `ProjectileSpriteRenderer` pattern. Billboards
  are alpha-blended (not additive, so team colours stay readable), lit
  (brightness in the vertex colour), side-tinted (the real side colour),
  depth-tested against the already-drawn opaque scene, with the P339/P525/P529
  render-state hygiene (states pinned then restored).
- `Game1.Rendering.cs`: in the ship draw loop, per ship compute
  `px` → `farAmt`; draw the full model at `ModelAlpha` opacity and the far
  sprite at `SpriteAlpha`, so the two crossfade with no pop. **Engine trails
  and motion ribbons are skipped for far-tier ships** (`IsFarLod`), and far-tier
  ships are excluded from the model depth silhouette (their tiny point must not
  occlude soft smoke). Brackets / side tags / targeting are screen-projected
  from world positions and are untouched by the tier — verified by the
  `Far_tier_does_not_move_the_bracket_anchor` test.
- Camera-relative rule (P612b) preserved: `eyeRel = xnaPos - cameraEye`, sprites
  are eye-relative, views are `CreateLookAt(Zero, …)`.

## Scope item 3 — Instancing / batching

The far tier is fully batched: **one draw call per hull type** (`ShipLodBatching.
GroupByKey`), so hundreds of distant ships collapse to a handful of calls. For
the **near tier**, full-model hardware instancing was evaluated but not shipped:
MonoGame's `Model`/`BasicEffect` path doesn't expose instance buffers, so real
GPU instancing would mean bypassing `Model.Draw` with a custom instanced shader
for a case (many same-hull ships simultaneously on screen at full detail) that is
rare in practice and dominated by the far tier at any wide view. The LODGeo level
choice already cuts near-tier triangle count. Trade-off documented in "Modern
alternative considered".

## Scope item 4 — Config + debug overlay

- Thresholds env-configurable (above).
- On-screen debug overlay: `ALLEGIANCE_LOD_OVERLAY=1` draws `LOD full:N far:M`
  top-right in the HUD (`HudRenderer.cs` new `lodOverlay` param). Visible in the
  real-window screenshots (`LOD full:15 far:80` wide, `LOD full:25 far:112`
  cockpit).
- The `ALLEGIANCE_VOL_PERF` frame-stats line now also reports
  `shipsDrawCalls`, `lodFull`, `lodFar` (`Game1.VolumetricProof.cs`), giving the
  benchmark its draw-call metric.

## Scope item 5 — Close-up unchanged

Up close the ship is the full native `LODGeo` finest model at native
scale/orientation/lighting — the existing P566/P612b path, untouched except that
`modelOp` folds in the crossfade alpha (1.0 when fully near, so byte-for-byte the
same appearance).

---

## Modern alternative considered

| What native does | Modern option | Chosen | Why |
|---|---|---|---|
| LOD by projected pixel radius | GPU hardware instancing for every tier | **Far-tier batching + native LODGeo levels** | The far tier is the overwhelming win (hundreds of ships → a few calls). True near-tier instancing requires bypassing MonoGame `Model.Draw` with a custom instanced shader; given near ships on screen at full detail are few, the ROI is low. Proposed as a follow-up if the 680M proves near-tier-bound. |
| Billboards drawn individually | Modern particles/batching | **Per-hull-type billboard batches** (reuses the existing `ParticleBillboardBatcher` array builder) | Minimal new GPU surface, maximises draw-call collapse. |

---

## Verification

### Tests (failing-first; new classes fail to exist on pre-P628 tree)
`tests/Allegiance.ClientCore.MonoGame.Tests/ShipLodTests.cs` (8):
- tier selection by projected size (closer/larger ⇒ bigger px; below threshold ⇒
  far, above ⇒ full, mid ⇒ smooth blend);
- **hysteresis** (no flip-flop): `NextFarState`;
- crossfade continuity (model alpha + far amount cover the screen, no pop);
- native LOD level choice (`NativeLodLevelFor`);
- **instancing batches identical hulls** (`GroupByKey`);
- **far tier keeps side colour** through the batch;
- **target brackets stay anchored** on far-tier ships (anchor invariance).

`tests/Allegiance.Core.Tests/MdlParseTests.cs` (1): fig05 `LODGeo` levels
retained (native LOD scope-1 proof on real art).

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors**.

### Real-window frames (2560×1440 backbuffer, `ALLEGIANCE_AUTOSHOT`)
Taken from the real windowed client against a local server with a spawned
showcase battle. Verified by image inspection (top-right LOD overlay visible):

- **Wide battle view (far tier)** — `docs/reports/P628_images/p628_wide_chase_fartier.png`
  (chase, `LOD full:15 far:80`): the player's ship is a full model; distant ships
  are small points/sprites.
- **Close-up (full models)** — `docs/reports/P628_images/p628_cockpit_closeup.png`
  (cockpit, `LOD full:25 far:112`): a nearby ship renders as a detailed full 3D
  model; distant ships are sprites/points.
- **Transition** — the crossfade is unit-tested for continuity, and both near
  (full) and far (sprite) ships appear in the same frames with the band ships at
  partial alpha; an exact mid-fade still is impractical to hit blind, so the
  coexistence in the wide chase frame (`p628_chase_mixed.png`) is shown.
- **LOD off reference (all full models)** — `docs/reports/P628_images/p628_before_lodoff_allmodels.png`.

### Frame-time + draw-call benchmark (local server, this machine)
Real client (`ALLEGIANCE_VOL_PERF`), wide chase, before/after:
- BEFORE (LOD disabled via `ALLEGIANCE_LOD_FAR_PX=0.001`): `shipsDrawCalls=63–76`,
  `lodFull=63–76`, `lodFar=0`, avg ~17.2 ms (60 fps).
- AFTER (defaults), scale 5: `shipsDrawCalls=11–23`, `lodFull=5–16`, `lodFar≈60`,
  avg ~17.1 ms.
- AFTER, scale 13 (390 players): `shipsDrawCalls=13–16`, `lodFull=3–5`,
  `lodFar≈140–145`, avg ~17.0 ms.
- Close-up (cockpit), scale 9: `shipsDrawCalls=21–25`, `lodFull=15–19`,
  `lodFar≈84–112`.

Draw calls collapse from ~1/ship to ~13–23 total at a wide view. Frame time is
vsync-capped at 60 fps on this machine in every run, so the FPS does not move;
the draw-call reduction (the requested target) is large and real.

### Final suite runs (`scripts/run-tests.sh`, exactly 2 runs on final code)
Run A (final code):
- Lobby: 12/12 · MonoGame: **1977/1980, 0 failed** · ClientCore: 865/865 · Core: 4146/4148 (2 load-flake failures: `LiveShipDeathFinalizeTests.Oxygen_dead_lifepod…`, `GameOverPlayersWireTests.Win_by_elimination…`) · quarantine clean.

Run B (final code):
- Lobby: 12/12 · MonoGame: clean (no failures) · ClientCore: 864/865 (1 load-flake: `RetriedConnectLiveTests.RepeatedConnects…`) · Core: 4147/4148 (1 load-flake: `P590PodRescueTests.Pod_pickup_order_streams…`) · quarantine clean.

All run A/B failures were live-network tests untouched by P628; each was re-run 3× in
isolation — `Oxygen_dead_lifepod…`, `Win_by_elimination…`, `RetriedConnect.RepeatedConnects…`
passed 3/3; `P590PodRescue.Pod_pickup…` passed 2/3 (intermittent load flake). All are
documented in `docs/TEST_FLAKES.md` (the MonoGame suite containing the P628 LOD tests was
**fully clean** in both runs — no P628 regression).


---

## Notes / pre-existing issues observed during proof

- The 221 / play2 / deploy trees were never touched; all proof used a local
  server on this workstation, and every process (server, MCP driver, client) was
  stopped by the exact PID the launcher recorded (live-proof.sh's cleanup).
- The `allegiance-mcp` StallWatchdog console-writer throws an
  `ArgumentOutOfRangeException` under load (pre-existing, unrelated to P628 — it
  did not stop the spawn). Worth a separate ticket.
- P654's per-IP connection cap defaults to 64, which refused the client's extra
  connection when ~64 bots saturate one IP; the live rig raises it via
  `ALLEGIANCE_TRANSPORT_MAX_PER_IP`.
