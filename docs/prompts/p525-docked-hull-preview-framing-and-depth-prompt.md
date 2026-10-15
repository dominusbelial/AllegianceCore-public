# P525 — docked hull preview: native framing (2.4 × model radius), own clear, depth re-enabled (live-confirmed play patch)

## Why this task exists

**Domingo's live playtest (2026-10-04), P519's loadout-screen hull preview:**
1. "it looks like im looking the ship from the insides"
2. "the fake station seems to be right beside the ship model"
3. (after fixing 1+2) "the ship looks incomplete there are gaps on the mesh"

Three separate root causes, all in `DrawDockedHullPreview`
(`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2213-2239`):

1. **Wrong scale → camera inside the hull.** It scales by `1f / HullLength`
   (`:2226-2227`), but the compiled ship meshes are ~190 model units long
   (render radius ~95, see `ShipRenderScale.RenderRadius`,
   `Rendering/ShipRenderScale.cs`). A fighter (length ~21) drew with a ~4.5
   unit radius while the eye sits at z=2.4 (`:2233`), so the camera was
   inside it. **Native:** `LoadoutImpl::SetGeo` (`~/Development/Allegiance/
   src/WinTrek/load.cpp:919-930`) puts the camera at `2.4f * radius`, the
   geo's own radius, with FOV 50 (`load.cpp:862-864`). So the port's eye at
   2.4 is right, provided the model is normalised to **radius 1**.
2. **The world scene shows through.** The docked world, including the station,
   is drawn earlier in the frame and the preview shares its depth buffer.
   **Native:** the loadout geo is its own pane with its own z-buffer and no
   world scene (`load.cpp:860-910`, "Give this guy a zbuffer").
3. **Depth testing is off → holes.** This is the exact SpriteBatch state leak
   P339 fixed for the flight pass (`Game1.Rendering.cs:435-449`).
   `SpriteBatch.Begin` sets `DepthStencilState.None` and never restores it.
   The preview is drawn after the HUD SpriteBatch passes, and ships are drawn
   `CullNone` (`ShipRenderer.cs:141`), so back faces overdrew front faces.

**Interim:** Claude added an uncommitted LOCAL PLAY PATCH to Domingo's play
worktree (`~/allegiance-play`; diff at `~/allegiance-play-hull-preview.diff`):

```csharp
float radius = Rendering.ShipRenderScale.RenderRadius(model);
float scale = radius > 0f ? 1f / radius : 1f;
GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
GraphicsDevice.BlendState = BlendState.Opaque;
GraphicsDevice.DepthStencilState = DepthStencilState.Default;
```

**Domingo confirmed it live (2026-10-04): "they look perfect now!"** This is
the verified behaviour. Make it real; don't redesign it.

## Scope

1. Land the three fixes in `DrawDockedHullPreview`, matching the patch's
   behaviour. Pull the framing math (scale from model radius, eye distance
   2.4 × normalised radius, FOV 50) into a small pure, testable helper next
   to `DockedHullOrbit` (e.g. `Rendering/DockedHullPreviewFraming.cs`), and
   cite `load.cpp` in its doc comment.
2. Keep the remaining frame order intact: the panes (`_dockedScreenRenderer`)
   must still overlay the preview. Check that the investment and teleport
   sub-views, which don't draw the preview, are unaffected.
3. A **headless visual proof**: render the preview framing offscreen for at
   least the smallest and the largest player hull (for example a fighter and
   a carrier) plus one P511-P515 model. Reuse `tools/roster-capture`'s
   existing offscreen render path. Save the before/after JPEGs in the
   report's assets dir. "Before" should show the camera inside the small
   hull.

## Explicitly out of scope

- **The preview's motion.** Native's loadout spins the geo about Y at
  1 rad/s with a fixed π/8 X tilt (`load.cpp:877-887`). P519 used the
  `cmExternalOrbit` pitch/yaw/roll rates (`WinTrek.cpp:7187-7196`), which is
  the in-flight orbit camera. That is a real divergence, but it's a separate
  decision for Domingo. Do NOT change `DockedHullOrbit` here; note it in the
  report as a follow-up.
- Skipping the docked world draw entirely (a perf optimisation). The clear
  is enough.
- Ship orientation overrides (P520) and any Meshy model or texture.

## Verification

- Failing-first unit tests on the framing helper: for the real
  `RenderRadius` of a small and a large ship model (or representative
  radii), the normalised model radius is 1 and the eye (2.4) sits outside
  it with the whole sphere inside the 50° frustum. Against the old
  `1/length` formula the small-hull case must fail.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs. Cite `docs/TEST_FLAKES.md` for any remaining flake. **Report only
  numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P525_DOCKED_HULL_PREVIEW.md`: root causes with
   file:line, native citations, test results and the before/after renders.
