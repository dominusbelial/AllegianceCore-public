# P562 — Aleph art pass: native animated vortex (double-horn, flowing alephbmp)

**Date:** 2026-10-06 · **Branch:** `master` (unpushed) · **Author:** Hermes

## Summary

Gave the port's double-horn (hourglass) aleph the original game's **animated
vortex** look — the swirling, inward-flowing `alephbmp` texture, the bright
mid-band ring, the two **counter-rotating star cores** and the **transit
swell** — by porting native `effect/alephgeo.cpp` onto the existing P242/P258
hourglass mesh. The double-horn silhouette is kept (Domingo's explicit scope);
the P242/P258 per-vertex-colour + rigid whole-mesh spin is replaced with
native's **texture-driven** flow, so the aleph now reads as water draining
instead of a spinning solid.

## What changed (native rule → port)

Every ported rule cites its native source:

| Native rule (`alephgeo.cpp`) | Port |
|---|---|
| `InitializeVertices :80-102` — disc of `countDepth` rings × `countSection` sections, `bright = 2·r·(1−r)` per vertex (dark pinch & rim, bright mid-band) | `WarpMeshFactory.Generate` (`WarpMeshFactory.cs`): double-horn mesh, per-vertex colour = `Brightness(r)=2r(1−r)` grayscale (`Brightness`). |
| `UpdateTextureCoordinates :126-145` — `v = mod(time/10,1)+r` (inward flow) and `u = 4·section/countSection + 5·(1−(1−r)²)` (spiral twist) | `WarpMeshFactory.FlowV` / `TwistU` (pure, unit-tested); base UVs baked in the mesh, renderer adds the time flow every frame. |
| `LoadAleph` (`trekigc.cpp:1900-1925`) — `LoadImage(textureName+"bmp")` | `WarpRenderer.FunnelTexture` resolves the **per-warp** `textureName + "bmp"` (case-insensitive fallback — see Spot-check below). |
| `RenderCallback :214-230` — additive blend, Gouraud, cull none, **no Z-write** | `WarpRenderer.Draw` — `BasicEffect` `TextureEnabled + VertexColorEnabled` (Gouraud: texture × vertex brightness), additive SRCALPHA/ONE, cull none, `DepthRead` (test, no write). |
| No static geometry twist (`:85` circle angle is plain `2π·sec/countSection`) — ALL swirl from animated UVs | Removed the P242/P258 baked helix twist + spiral-arm colour wedges; **stopped the rigid whole-mesh rotation** (the P242/P258 spin). Flow now comes purely from the animated UVs. |
| `Render :190-208` — two `f02bmp` star decals, white, scale `0.1` of the unit funnel, rotating `+0.25` and `−0.375` rad/s | `WarpVortex.StarRateA/B`; `WarpRenderer.DrawStarCore` — two camera-facing `f02bmp` billboards at the pinch, scale `0.1 ×` funnel world rim radius, counter-rotating. |
| `Render :158-198` — transit swell: 8 resonant pulses (`|sin(π·8.5·p)|`), ~6×, 0.25 s decay | `WarpVortex.StarScale` (pure, unit-tested) + `WarpRenderer.TriggerTransit`; `Game1.DetectTransitSwells` fires it when any ship's sector changes (arrival aleph). |
| `CreateAlephGeo :239-251` — LOD tiers (8,32)/(4,16)/(2,16)/(2,8) at 64/32/16 | `WarpMeshFactory.LodTiers`; `WarpRenderer.SelectLodTier` picks by projected on-screen pixel radius against native's 64/32/16 cutoffs. |

### ⚠️ Spot-check correction — the per-warp texture name is NOT "aleph"

The task's premise ("`textureName` is `aleph` for all 46 warps in
`m2xspiderw.igc`") is **disproven by the data**. Reading the actual
`OT_warp` records (the harness boots the real server + client and dumps
`world.Warps`; `DataWarpIGC.Decode` on the real file):

```
'plnt19' x27   e.g. 2168:Bug Nebula, 2176:Rigel, 2174:Gaia
'plnt42' x17   e.g. 2167:Terra, 2178:Plato, 2171:Tellus
```

`m2xspiderw.igc` therefore carries **planet-surface** texture names
(`plnt19bmp.mdl`/`plnt42bmp.mdl`, both 1024×1024 full-coverage). Native
`LoadAleph(textureName)` really does load `plntXXbmp` for these warps — which
would paint a solid planet over the funnel and **destroy the water-draining
vortex** the art pass exists to deliver. So the port honours the native
per-warp mechanism **and** the task's core intent:

- aleph-family names (`aleph`/`homealeph`/`neutralaleph`) resolve their own
  texture (per-warp variety, e.g. `homealephbmp` for home sectors);
- a **non-aleph** name (this map) falls back to the canonical **`alephbmp`**
  vortex texture — the look Domingo asked for.

Also a filesystem case pitfall: the igc name is lowercase `aleph` but the
artwork file is `Alephbmp.mdl` (capital A); `WarpRenderer.FindCaseInsensitive`
falls back to a case-insensitive match so it resolves on Linux.

## Files

- `src/.../Rendering/WarpMesh.cs` — `WarpMesh` record gained `Uvs`.
- `src/.../Rendering/WarpMeshFactory.cs` — rewritten: textured double-horn,
  native brightness band, native UVs, LOD tiers, pure `Brightness`/`TwistU`/
  `FlowV`.
- `src/.../Rendering/WarpVortex.cs` — **new**: star rotation rates, swell
  curve (pure).
- `src/.../Rendering/WarpCoreMeshFactory.cs` — kept (fallback disc) + `Uvs`.
- `src/.../Rendering/WarpRenderer.cs` — rewritten: textured funnel, animated
  UVs, LOD, f02 star core + transit swell, per-warp + case-insensitive texture
  resolution, no rigid spin.
- `src/.../Game1.Lifecycle.cs` — `new WarpRenderer(GraphicsDevice, _assets)`.
- `src/.../Game1.cs` — `_drawWarps` carries warp id + textureName; per-ship
  last-sector tracking for transit detection.
- `src/.../Game1.Rendering.cs` — warp draw passes `worldRadius`/`textureName`/
  `warpId`; `DetectTransitSwells` plays the arrival aleph's swell on any ship
  sector change.
- `tests/.../WarpMeshFactoryTests.cs` — rewritten + expanded (native formulas,
  brightness band, geometry, LOD, `WarpVortex` star/swell).
- `tools/warp-visual-proof/Program.cs` — proof harness updated to the new API;
  renders 3 angles × 3 distances + an 8-frame inward-flow strip + an 8-frame
  transit-swell strip through the **real `WarpRenderer`** against a **real
  in-process server** with **real artwork**.

## Proof

### Contact sheet (real renderer, real server, real textures)
Head-on / 45° / side-on × 3 distances — `docs/reports/P562_ALEPH_VORTEX_assets/warp_{headon,diag45,side90}_d{450,900,1400}.png`

Verified: the vortex is **blue-dominant** (bright-region mean RGB ≈
`[63,94,115]`, ~99% of bright pixels blue-dominant) matching Domingo's bluish
memory; the head-on view shows clear spiral arms twisting inward; the side-on
view shows the double-horn hourglass with flowing blue-white filaments and a
glowing centre core.

### Animation strip (inward flow)
`warp_anim_01..08.png` — 8 frames spanning 2 s of native's `time/10` flow
(frames step 0.25 s of simulated time). Measured frame-1→frame-8 mean pixel
change `Δ = 1.39` (vs the per-frame drift before widening), confirming the
texture window slides along the funnel (the drain motion).

### Transit-swell strip
`warp_swell_01..08.png` — after `TriggerTransit`, the star core grows and
pulses: bright-centre pixels `frame1 116 → frame7 1462` (~12×), then begins
the 0.25 s decay — native's 8-pulse resonance + decay curve
(`WarpVortex.StarScale`).

### Real client screenshot (windowed, live backbuffer)
A real standalone windowed client (the `Allegiance.ClientCore.MonoGame` head,
`--capture 127.0.0.1 <port> capture 30 <outdir>` at 1280×720) was run against a
locally booted real `Allegiance.Server` hosting `m2xspiderw.igc`
(`server.p562.json`, port 7788). It connected, spawned in Ganymede and flew to
an aleph, capturing **711 real backbuffer frames at 60 FPS** (`renderFps=60.03`).

The aleph appears centred in the view as a **blue swirling vortex with a
glowing cyan centre** (vision-confirmed on the live frames). A hero crop and a
short frame sequence from the real client:

- `docs/reports/P562_ALEPH_VORTEX_assets/realclient_aleph_headon.png`
  (centre crop of the aleph — the native bright ring band + centre core)
- `docs/reports/P562_ALEPH_VORTEX_assets/realclient_seq_344..352.png`
  (a short frame sequence from the client, aleph in view)

Measured from the captured frames: the scene is blue-dominant (89% of bright
pixels blue over red), i.e. the bluish vortex renders as intended in the real
windowed client — not a spinning solid and not a planet surface.

## Build / tests

Whole-solution build (`dotnet build -m:8`): **0 warnings, 0 errors**.
(TreatWarningsAsErrors is on.)

Full suite, **3 consecutive runs on the final code** (per-project; each
project run in its own foreground call, per the session rule):

| Project | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `Allegiance.Lobby.Tests` | 12/12 ✅ | 12/12 ✅ | 12/12 ✅ |
| `Allegiance.ClientCore.MonoGame.Tests` | 1334/1334 ✅ | 1334/1334 ✅ | 1334/1334 ✅ |
| `Allegiance.ClientCore.Tests` | 544/545 | 544/545 | 544/545 |
| `Allegiance.Core.Tests` | 3760/3766 | 3763/3766 | 3763/3766 |

The only non-green results are **pre-existing documented live-wire timing
flakes** (all pass in isolation; none touch the warp/vortex code — my new
`WarpMeshFactory`/`WarpVortex` tests are all in the MonoGame project, which is
**3× 1334/1334 green**):

- ClientCore 1 flake/run — `FullSessionKillIntegration…RealCrossSideKill`
  (TEST_FLAKES.md:33), `ShipDeleteGoneLiveTests…WatcherLatchesGone`
  (:51), `…DockOut_WatcherLatchesGone…` — all live-wire.
- Core 3–6 flakes/run — `AutoReloadOnEmpty…RELOAD129` (:173),
  `LiveRipcordTests`, `LiveTreasurePickupTests`, `AsteroidMinedLiveTests`,
  `AddPartTests`, `SideEliminationLiveTests`, `GameOverPlayersWireTests`,
  `AsteroidExportTests`, `P366LeaderBotCommandLiveTests`,
  `ShipsUpdateLiveTests` — all live-network/economy integration tests.

No warp-rendering test failed in any run.

## Out of scope (honoured)

Warp-transit flash/particles (`e_telebmp`), sounds, map icons, aleph size /
position / transit radius all unchanged.
