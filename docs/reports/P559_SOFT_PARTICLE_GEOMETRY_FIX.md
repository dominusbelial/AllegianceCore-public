# P559 — Soft-particle depth pass hides geometry: render depth BEFORE color + never mutate ship part effects

Branch `master`, committed (not pushed). DesktopGL. Fixes the live-reported
invisibility on client `762dfe5e` (P550): with `--soft on` (Enhanced default)
all solid geometry (ships, alephs, stations) vanished; only lights/particles/
HUD remained. `--soft off` restored everything. Root-caused to two defects
introduced by P547 (`a5ab6e4e`), verified and fixed here.

---

## 1. Root cause 1 — the depth pass switched the color target away mid-frame

`Game1.Rendering.cs` (P547) ran the linear-depth pass AFTER the opaque scene:

```
draw starfield, ships, asteroids, stations, …treasures   // into color target
if (soft) {
    SetRenderTarget(_post.DepthRT);   // ← switch AWAY from the color target
    Clear; DrawSceneDepth;
    SetRenderTarget(bloom ? SceneRT : (capThis ? _capTarget : null));  // ← switch BACK
}
particles/lights/HUD                                        // drawn after
```

MonoGame creates the backbuffer (and these `RenderTarget2D`s, created with the
6-arg constructor) with `RenderTargetUsage.DiscardContents`; switching away from
a target and back **discards its contents**. Everything drawn into the color
target before the switch (ships/asteroids/stations/alephs) was wiped; only what
was drawn after (particles, lights, HUD) survived — exactly Domingo's report.

### Why P547's proof missed it
Its headless proof rendered into the offscreen `_capTarget` and only checked
"no exceptions" + the HUD (which is drawn last). It never asserted that the
opaque geometry survived a target round-trip, so the wipe went unnoticed. This
P559 proof does assert geometry presence through both the real capture path and
the real backbuffer (`GetBackBufferData`).

### Fix (choice made): render the depth pass BEFORE the color pass
`PrepareSceneGeometry` (new) gathers the opaque scene (ships, asteroids,
stations) once per frame at the top of the 3D pass — same per-object world
transforms the color loops use, asteroid spins advanced exactly once — then the
linear-depth pass renders into `DepthRT`, and **the color target is set exactly
once and never switched away from** for the rest of the frame. The color loops
then draw into that fixed target (backbuffer / `SceneRT` when bloom / `_capTarget`
when capturing), and the particle passes sample the already-populated depth
target. This preserves geometry in every path with no reliance on
`PreserveContents` semantics.

Why not the second-choice `PreserveContents` patch alone: you cannot change the
**backbuffer's** usage (it is the swapchain's, not ours), so it would not fix the
default (no-bloom, no-capture) case that Domingo hit — a restructure is required.

This is a client-rendering bug fix; no native Allegiance rule was changed. The
soft-particle depth-fade behaviour ported in P547 (P546/P547 scope, unchanged
here) is preserved. The render-target semantics that broke the frame are
MonoGame's `RenderTargetUsage.DiscardContents` (backbuffer and every target here
are created with it), referenced at `Game1.Rendering.cs` (old mid-frame switch)
and `VfxPostProcessor.cs:84-85` / `Game1.Lifecycle.cs:94` (DiscardContents).

## 2. Root cause 2 — `ShipRenderer.DrawDepth` permanently replaced part effects

`ShipRenderer.DrawDepth` (P547) did, per mesh part:

```
foreach (var part in mesh.MeshParts) part.Effect = depthEffect;
… mesh.Draw();
```

It never restored the part's original `BasicEffect`, so the next normal color
pass rendered every ship with the depth shader (broken lighting / effectively
invisible hulls). `AsteroidRenderer.DrawDepth` and `StationRenderer.DrawDepth`
were checked and do NOT mutate any effect (they draw their own vertex/index
buffers directly) — unaffected.

Fix: `DrawDepth` now applies the depth effect and draws each part's own vertex/
index buffers directly (`SetVertexBuffer(part.VertexBuffer); gd.Indices =
part.IndexBuffer; DrawIndexedPrimitives(..., part.VertexOffset, part.StartIndex,
part.PrimitiveCount)`) and **never reassigns `part.Effect`**.

## 3. Failing-first proofs (both run on a real GraphicsDevice)

New deterministic harness `tools/soft-frame-proof` (a MonoGame `Game`, like the
existing `roster-capture`): loads the real `gt-fighter` model + the real
`ShipRenderer` + real `VfxPostProcessor` (compiled `.mgfxo` shaders), and reads
the **real backbuffer** with `GraphicsDevice.GetBackBufferData`.

```
[soft-frame-proof] depthEffect=True softReady=True shaderFailures=0 shipScale=0.216
  control offscreen-target ship render: brightPx=18675 nearPx=28165 => PASS
  control backbuffer bright-clear probe => brightPx=384000 (READABLE)
  sequence A (OLD mid-frame switch) backbuffer:  brightPx=0  nearPx=0   => ABSENT  (bug reproduced)
  sequence B (NEW depth-first)      backbuffer:  brightPx=18675 nearPx=28165 => PRESENT
  [effects] DrawDepth mutated 0/1 part.Effect refs => PASS (none mutated)
  RESULT: PASS (old loses geometry, new keeps it, part.Effect untouched)
```

- **Sequence A** (the old P547 ordering) renders the ship into the backbuffer,
  switches to `DepthRT` and back, then reads the backbuffer → **0 px** — the
  DiscardContents wipe, reproduced. Failing case.
- **Sequence B** (the new ordering, depth first, one color-target set) → ship
  geometry present. Passing case.
- **Part-effect check**: with the fixed `ShipRenderer`, `DrawDepth` mutates
  **0/1** part effects (PASS). Reverting `ShipRenderer.cs` alone to the old code
  yields **`DrawDepth mutated 1/1 part.Effect refs => FAIL`** — genuine
  failing-first for root cause 2.

## 4. Real-client capture proof (full `Draw()` frame, actual game)

Local `Allegiance.Server` (Test Mission, `XXV_simple.igc`) + the MonoGame client
in `--capture` mode (soft particles ACTIVE, Enhanced). Analysed captured frames:

| Config | centre-bright (>40) | vision check |
|---|---|---|
| **OLD code** + soft ON | ~1.3% | **no ship / station / aleph**; empty black centre, only HUD + faint artifacts |
| **FIXED code** + soft ON | ~2.5–3.0% | **two alephs / warp gates present**, scene geometry renders |

Vision-confirmed: the old-code frame has NO ship, NO station, NO aleph in the 3D
view (the exact reported symptom); the fixed frame renders the aleph/geometry.

## 5. Live check — real backbuffer, window 2560×1440 (client `--window 2560x1440`)

Added a small one-shot live-proof hook: `ALLEGIANCE_AUTOSHOT=<delayMs>` requests a
single real-backbuffer screenshot (the exact F11 `Capture.ScreenShot.
CaptureBackbuffer` path) that many ms into Flying, so the **actual presented
backbuffer** is captured without keyboard injection. Three windowed runs on the
fixed code:

| Config | 2560×1440 backbuffer shot | visible |
|---|---|---|
| soft ON | `p559_live_backbuffer_soft.png` | targeted ship renders in the 3D view |
| soft ON + bloom ON | `p559_live_backbuffer_bloom.png` | player ship (chase view) + target ship + afterburner glow + **soft smoke/exhaust trail near the hull** |
| soft OFF | `p559_live_backbuffer_softoff.png` | **aleph (warp gate)** — the object that was invisible — renders clearly |

Combined with the capture frames (alephs/geometry visible; old code = none), the
live window captures show ships, an aleph, and smoke near a hull across the
three soft/bloom configurations at 2560×1440 on the real backbuffer. The test
map (`XXV_simple`) has 0 asteroids and no station near the spawn point that the
cameras framed in these shots; the asteroid/station renderers are covered by the
`DrawSceneDepth` path in the automated harness (root-cause-1 A/B) and by the
game's own `DrawSceneDepth` exercising ship+asteroid+station `DrawDepth`.

## 6. Build / tests

Whole-solution build: **0 warnings, 0 errors** (includes the new
`tools/soft-frame-proof` project, added to `AllegianceCore.slnx`).

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs on the final
code — see Suite results below. Per `docs/TEST_FLAKES.md`, only the documented
live-wire/load flakes can appear in the untouched Core/ClientCore assemblies.

### Suite results (3 × sequential, per-assembly — recorded as each ran)

| Run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1327/1327 | 544/545 | 3765/3766 |
| 2 | 12/12 | 1327/1327 | 544/545 | 3761/3766 |
| 3 | 12/12 | 1327/1327 | **545/545** | 3763/3766 |

The **MonoGame** assembly — the project holding every changed rendering file —
is **1327/1327 green in all 3 runs**. Every single failure is a **documented
live-wire load-flake in the untouched Core/ClientCore assemblies**, all present
in `docs/TEST_FLAKES.md`: `FullSessionKillIntegration…CrossSideKill` (line 33),
`ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` (P482 residual ~1/15
flake, line 46), `ConquestTerritoryLiveTests.Conquest_win…leak…` (line 29/267),
`SideEliminationLiveTests.Eliminating_side2…` (line 39), `LiveRipcordTests…
Cancel_while_active…` (line 36), `LiveChatSectorWireTests…` (line 216),
`GameOverPlayersWireTests.{Conquest_win…, Win_by_elimination…, Conquest_win_
lists_…}` (family lines 30/31/267, "rotate run-to-run and remain", line 427),
`CargoBayReloadTests.Reload_malformed…` (line 53). None touch P559's code; none
was re-run chasing green (each run reported as it happened).

## 7. Files

- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — depth pass moved before
  the color pass; new `PrepareSceneGeometry`; color loops consume pre-gathered
  geometry; removed the mid-frame target switch.
- `src/Allegiance.ClientCore.Game/Rendering/ShipRenderer.cs` — `DrawDepth` draws
  part buffers directly, never mutates `part.Effect`.
- `src/Allegiance.ClientCore.Game/Game1.cs`, `Game1.FlightInput.cs` —
  `ALLEGIANCE_AUTOSHOT` live-proof hook (test-only).
- `tools/soft-frame-proof/` — new failing-first proof harness.
- `AllegianceCore.slnx` — added the harness project.
- `docs/reports/P559_SOFT_PARTICLE_GEOMETRY_FIX.md` (this report) + a dated
  correction note added to `docs/reports/P547_SHADERS_SOFT_PARTICLES_BLOOM.md`.

Suite numbers in §6 above were recorded from 3 sequential per-assembly runs on
the final code; the MonoGame assembly (all changed code) is 1327/1327 every run.
