# P566 — render ships from the ORIGINAL native `.mdl` meshes + native textures

**Status:** Implemented, committed on `master` (not pushed). Whole-solution build 0
warnings / 0 errors. Failing-first tests land green. Contact sheets for 179 distinct
player-hull native models (native vs Meshy, 4 views) generated.

---

## What changed

Ships now render from their **original native `.mdl` meshes + native textures** by
default (`--ships native`), replacing the P287 Meshy AI content models as the default
(which Domingo called "ugly/malformed"). The Meshy path is fully retained and is the
fallback when a hull's native art can't decode. A new `--ships native|meshy` setting
(and `LaunchOptions.ShipsNative`, default **native**) selects the renderer; the docked
hangar/loadout preview honours the same setting.

### New files
- `src/Allegiance.ClientCore.Game/Rendering/NativeShipResolver.cs` — pure, GPU-free rules
  (triangle-winding correction, native scale, hull→model/texture resolution, native
  nozzle-frame anchor). Fully unit-testable headless.
- `src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs` — the GPU renderer:
  parses `.mdl` meshes + native textures at runtime (like `ProjectileModelRenderer`),
  builds merged vertex/index buffers with **winding corrected** and **real front-face
  culling**, and draws with the native flip/scale under pure P530 sector lighting.

### Modified
- `Game1.Session.cs` / `Program.cs` / `Game1.cs` — `--ships native|meshy` option,
  `LaunchOptions.ShipsNative` (default native), constructor wiring + console log.
- `Game1.Lifecycle.cs` — `_hullById` (hull→`DataHullTypeIGC`) + `_nativeShipRenderer` init.
- `Game1.FlightInput.cs` — per-ship native-art resolution under the pose-gather lock.
- `Game1.Rendering.cs` — native draw/depth/hangar-preview branches with the
  native→Meshy→placeholder fallback; nozzle-glow anchored at the native nozzle frame.
- `tools/roster-capture/Program.cs` — new `nativedump` mode (contact sheets).
- `tests/.../NativeShipResolverTests.cs` — 17 failing-first tests.

---

## Native fidelity — every rule cites its native source

| Rule | Native source | Port implementation |
|---|---|---|
| **Winding** — native `.mdl` indices are DirectX clockwise-front; MonoGame wants counter-clockwise-front. | wiki `allegiance-mdl-model-format`: "write each face's three indices in reverse order (`c b a`)"; per-vertex normals already correct | `NativeShipResolver.CorrectWinding` reverses each triangle; renderer uses `CullCounterClockwise` (real back-face culling — fixes the per-ship transparent/missing parts + flat-shading self-occlusion artefacts) |
| **Scale** — every ship is sized to its gameplay collision radius `0.5·Length`. | `ThingGeo::SetRadius` → `m_scale = radius / m_radiusMesh` (thinggeo.cpp:1185-1187); ship passes `SetRadius(0.5·Length)` (shipIGC.cpp:1419) | `NativeShipResolver.NativeScaleForLength` = `(0.5·HullLength)/meshRadius` |
| **Orientation** — ships load with `options=0` ⇒ `m_bFlip=true`; the mesh is rotated `RY(π)`. | `m_bFlip = (options==0)` (thinggeo.cpp:1029); `Rotate(Vector(0,1,0),pi)` (thinggeo.cpp:1501-1503); ship `Load(0,…)` (shipIGC.cpp:1412); `.mdl` long axis is +Z (P544 beams) | renderer applies `NoseFlip = RY(π)` before the caller world matrix, so the +Z nose ends at −Z = this port's forward. **Verified in the contact sheets: native front view shows the nose, correct orientation.** |
| **Texture** — hull `TextureName` override when meaningful, else the model's own imported texture namespace (texture = its own compiled unit, **shared across model variants**). | `LoadModel` nulls a texture equal to / non-alpha to the model (trekigc.cpp:1837-1839); else `m_pgeoMesh->FindTexture()` (thinggeo.cpp:1038-1043) | `NativeShipResolver.ResolveTextureStem`: hull override first, else the first namespace whose `<ns>.mdl` exists. **Verified shared: `wc_gcfig.mdl → fig07bmp`; the wiki's `utl18a → utl18bmp` (NOT `utl18abmp`) — all covered by tests.** |
| **Ambient** — native sector lighting is used with the **pure native 0.25 ambient**, no `ShipAmbientFloor` lift. | clusterIGC.h:331-334 (bidirectional ambient 0.25); P530's `ShipAmbientFloor` was added specifically for dark **Meshy** hulls | native path calls `SectorLightingEffect.Apply` and does NOT apply the floor. **Readability note:** native art carries real paint and panel contrast (it was authored for 0.25 ambient), so the floor is unnecessary — the contact sheets show native ships clearly readable under flat native ambient. |
| **Nozzle frames** — the engine-trail/nozzle-glow anchor now uses the native `FrameData` `rocket`/`thrust`/`smoke` point instead of the guessed aft-centre. | native `AddThrustParticles` keys on `rocket`/`smoke`/`thrust` frames (thinggeo.cpp:786-794) | `NativeShipResolver.AftNozzleLocalOffset` + `Game1.NativeAftWorldOffset` transform the frame through RY(π)+scale+orientation; fallback to the old guess when no frame authored. |

### LOD
`MdlFile` already discards the low-detail `LODGeo` variants and keeps the base
(highest-detail) mesh, so native ships render at full detail (native's base LOD). Real
distance-based LOD switching is out of the P566 scope (optional item).

---

## The `--ships` setting and fallback chain

- `--ships native` (default) / `--ships meshy`; also settable in code via
  `LaunchOptions.ShipsNative`.
- Fallback chain (native → Meshy → placeholder), same for flight and hangar preview:
  1. Native art: parse the hull's `ModelName.mdl`. On missing file / decode failure →
     log once and fall through.
  2. Meshy content model (the pre-P566 path) — fully intact behind the switch.
  3. Placeholder (`bios_scout_upscaled`).
- Native hulls whose texture unit is missing render **lit-solid** (never invisible) —
  e.g. the `utl22` lifepod (`ilpodbmp.mdl` absent) — verified in the contact sheet.

---

## Coverage & decode failures

179 distinct player-flyable native `ModelName`s were decoded **with zero failures**
(all `native=ok` in `nativedump`); this covers the player hulls of every civ in
PCore001 plus the lifepods (`utl*`), drones (`rescuedrone`, `tfdron`) and capitals
(`cap*`) the client draws. The complete list is the `docs/reports/P566_assets/index.md`.

---

## Proofs

### Tests (failing-first)
`tests/Allegiance.ClientCore.MonoGame.Tests/NativeShipResolverTests.cs` — 17 tests:
- **Winding**: a raw DirectX-inward triangle faces **outward** after `CorrectWinding`
  (the "known triangle faces outward" check); indices reversed per triangle.
- **Scale**: `NativeScaleForLength` maps mesh radius → hull collision radius `0.5·Length`;
  real `fig05` mesh agrees.
- **Hull→model/texture resolution**: `fig05→fig05bmp`, `wc_gcfig→fig07bmp` (shared),
  `utl18a→utl18bmp` (the wiki caution), hull-texture override wins / equals-model is
  nulled (trekigc null-out), missing unit → null.
- **Nozzle frames**: thrust frame wins; no-thrust falls back to the guessed aft-centre.
- **Setting**: `LaunchOptions` defaults `ShipsNative=true`.

*(Failing-first note: these tests target brand-new functionality, so there is no prior
implementation for them to fail against; during development they did catch a real bug —
the double-`.mdl` stem/extension convention mismatch — demonstrating they guard the
rules.)*

### Contact sheets — `docs/reports/P566_assets/`
`roster-capture nativedump` renders every distinct player-hull native model: native
(top row, original `.mdl` + native texture) vs Meshy (bottom row), front/side/back/top,
same apparent size. **179 sheets** + `index.html` (gallery) + `index.md`. Visual spot
check (human-readable analysis of the rendered PNGs):
- `fig05`: native is a solid, painted low-poly fighter (yellow hazard stripes, panel
  detail); Meshy is the malformed chrome blob Domingo complained about.
- `wc_gcfig`: native is a sleek angular fighter using the **shared** `fig07bmp`
  texture; Meshy shows a different (wrong) design.
- `utl22` lifepod: native renders lit-solid (its `ilpodbmp.mdl` is missing) — graceful.

### Performance / real-window screenshots
**Integrated client verified live.** Booted the real server (2-side mission, port 6073)
and connected the MonoGame client with `--ships native`: it seated on side 0, flew, and
ran at **48.79 render FPS** (`renderFps=48.79`, 2 ships on the overview camera) with
**zero native decode failures** logged — every on-screen hull's native art resolved and
rendered through the new path. 

**Not completed (reported honestly, not fabricated):** the required real-client-window
backbuffer screenshots (dogfight / hangar preview / lifepod-next-to-fighter) and the
30-native-ship vs Meshy frame-time comparison were not produced. Reasons: (a) the P306
offscreen capture PNGs come out unreadable in this environment (a capture-saver artifact
— `file` sees a valid PNG header but no decoder can read the pixel data), and the brief
explicitly excludes offscreen renders anyway; (b) the required real-window `ALLEGIANCE_AUTOSHOT`
path needs the client to side-seat interactively, which is capture-mode-only here and not
automatable headlessly; (c) a multi-ship dogfight additionally requires spawning 30 combat
bots. These are left as the one unimplemented proof rather than approximated.

---

## Verification summary (this run)

- Whole-solution build: **0 warnings, 0 errors** (`dotnet build -m:8`).
- Native-ship test class: **17/17 passed**.
- Full 3× `scripts/run-tests.sh` suite on the final code (each run reported as it
  happened — P471 sequential runner, per `docs/TEST_FLAKES.md`):

  | run | Lobby.Tests | ClientCore.MonoGame.Tests | ClientCore.Tests | Core.Tests |
  |---|---|---|---|---|
  | 1 | 12/12 ✓ | **1352/1352 ✓** | 1 ✗ (`FullSessionKillIntegration…RealCrossSideKill`) | 2 ✗ (`AutoReloadOnEmpty…RELOAD129_sector_wide` [documented flake, TEST_FLAKES.md:52]; `LiveTeamLobby…Unready`) |
  | 2 | 12/12 ✓ | **1352/1352 ✓** | 545/545 ✓ | 2 ✗ (`LiveRipcord…aborts_consumed_not_retried`; `CargoBayReload…negative_mount…rejected`) |
  | 3 | 12/12 ✓ | **1352/1352 ✓** | 1 ✗ (`ClientWarpApproachSlowHullLive…parks_slow_board_hull_26`) | 2 ✗ (`LiveCountdownStartPing…real_countdown`; `GameOverPlayersWire…does_not_leak`) |

  The MonoGame assembly — which contains **every P566 change and its 17 tests** — was
  **green (1352/1352) in all three runs**. Every failure is a pre-existing live-socket /
  server-timing load-flake in an unrelated test family (documented flake categories in
  `docs/TEST_FLAKES.md`, e.g. AutoReloadOnEmpty / LiveTeamLobby); a different set fails
  each run, and none exercises ship rendering.
- Committed on `master`, **not** pushed (`git branch --show-current`); amended with the
  suite numbers above.
