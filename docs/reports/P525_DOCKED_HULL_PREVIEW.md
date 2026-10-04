# P525 — Docked hull preview: native framing (2.4 × model radius), own clear, depth re-enabled

Landing date: 2026-10-04. Branch: `master` (not pushed).

## Summary

Fixed all three live-playtest defects in the P519 docked hull preview
(`DrawDockedHullPreview`, `Game1.Rendering.cs:2213`): the "looking from the
insides" camera-embedding (wrong scale), the "fake station beside the ship"
(world scene showing through a shared depth buffer), and the "gaps on the mesh"
(holes from the P339 SpriteBatch depth-state leak). The port now frames the
hull exactly as native's loadout geo pane does — normalised to its own radius,
eye at 2.4 × radius, FOV 50° — matching the locally-patched behaviour Domingo
approved live on 2026-10-04 ("they look perfect now!"). Framing math lives in a
new pure, tested helper `Rendering/DockedHullPreviewFraming.cs`.

## Root causes (all in `DrawDockedHullPreview`, `Game1.Rendering.cs`)

1. **Wrong scale → camera inside the hull.** The preview scaled by
   `1f / HullLength` (`Game1.Rendering.cs:2226-2227`). But the compiled Meshy
   meshes are ~190 model units long (true rendered merged-sphere radius
   ~95-160, see `ShipRenderScale.RenderRadius`, `Rendering/ShipRenderScale.cs`),
   so a fighter (real length ~13-21) drew at a ~5-8 unit radius while the eye
   sat at z=2.4 (`:2233`) — the camera was *inside* the hull. Native
   `LoadoutImpl::SetGeo` (`~/Development/Allegiance/src/WinTrek/load.cpp:919-934`)
   instead puts the camera at `2.4f * radius` where `radius` is the **geo's own
   radius** (`load.cpp:923,927-933`). The eye-at-2.4 convention is therefore
   right, provided the model is normalised to **radius 1**.
2. **The docked world scene shows through.** The world (including the station)
   is drawn earlier in the frame and shares the preview's depth buffer. Native's
   loadout geo is its own pane with its own z-buffer and no world scene
   (`load.cpp:860-910`, "Give this guy a zbuffer" at `load.cpp:896-898`).
3. **Depth testing is off → holes.** This is the exact SpriteBatch state leak
   P339 fixed for the flight pass (`Game1.Rendering.cs:435-449`):
   `SpriteBatch.Begin` sets `DepthStencilState.None` and never restores it. The
   preview is drawn after the HUD SpriteBatch passes, and ships are drawn
   `CullNone` (`ShipRenderer.cs:141`), so back faces overdrew front faces and
   the mesh read full of holes.

## The fix (matching the live-confirmed play patch)

`DrawDockedHullPreview` now (in order):

- `scale = DockedHullPreviewFraming.NormaliseScale(RenderRadius(model))` — the
  model draws at radius 1, so the fixed eye at `2.4 × 1` sits just outside the
  hull's bounding sphere with the whole sphere inside the 50° frustum
  (arcsin(1/2.4) ≈ 24.6° < 25°). Replaces the old `1/HullLength` formula.
- `GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0)`
  — drops the docked world (station) drawn earlier, matching native's own
  z-buffered pane (`load.cpp:860-910`).
- `BlendState.Opaque` + `DepthStencilState.Default` — re-enables depth so
  `CullNone` ships no longer let back faces overdraw front faces (the P339
  state-leak fix, applied to this pass).

The new pure helper `Rendering/DockedHullPreviewFraming.cs` (cited to
`load.cpp`) exposes `NormaliseScale`, `EyeDistanceFactor` (2.4),
`EyeDistance`, `FieldOfViewDegrees` (50)/`FovRadians`, `EyeHeightY`,
`EyeOutsideSphere`, and `AngularRadiusDegrees` — all MonoGame-free and
unit-tested.

**Frame order kept intact:** the preview is only drawn in the shop branch
(`Game1.Rendering.cs:1386-1398`) and `_dockedScreenRenderer.Draw(...)` overlays
the panes right after it, so the panes still cover the preview. The investment
(`:1369-1380`) and teleport (`:1381-1385`) sub-views never call
`DrawDockedHullPreview`, so they are unaffected. (The Clear wipes the backbuffer
before the preview ship draws; the docked view is a fullscreen dark panel
anyway, and the translucent panes redraw over it.)

## Out of scope (deliberately unchanged)

- **Preview motion.** Native's loadout spins the geo about Y at 1 rad/s with a
  fixed π/8 X tilt (`load.cpp:877-887`); P519 uses the in-flight
  `cmExternalOrbit` rates (`WinTrek.cpp:7186-7191`) via `DockedHullOrbit`.
  This is a real divergence but a separate decision — left untouched, noted as
  a follow-up. `DockedHullOrbit` is unchanged.
- Skipping the docked world draw entirely (perf optimisation) — the clear is
  enough.
- Ship orientation overrides (P520) and any Meshy model/texture.

## Unit tests — `DockedHullPreviewFramingTests.cs`

Failing-first: every assertion uses the real measured data
(`docs/P305_roster/roster_variety.txt` render radii + real PCore001 hull
lengths), so they hold for the actual compiled models the preview draws. They
would fail against the old code:

- `Normalise_scale_maps_every_hull_to_radius_one` — after the new scale, both a
  small fighter and a large carrier draw at merged-sphere radius **exactly 1**.
- `Eye_sits_outside_the_unit_sphere` — eye at 2.4 is strictly outside radius 1.
- `Whole_hull_sphere_fits_inside_the_fifty_degree_frustum` — angular radius
  24.62° < 25° half-FOV.
- `Old_one_over_length_formula_embeds_the_camera_in_a_small_hull` — the real
  fighter (len 17.1, renderR 157.5) under `1/length` draws at ~9.2-unit radius;
  the eye at 2.4 is inside → the check FAILS, proving the fix is necessary.
- `New_radius_formula_keeps_the_eye_outside_the_same_hull` — same hull under
  the new formula draws at radius 1, eye outside.
- Guards: `Nonpositive_radius_guards_to_no_scale`; constants match native.

Result: `10 passed` (7 new framing + 3 existing `DockedHullOrbitTests`), run in
the MonoGame test assembly.

## Verification

**Whole-solution build** (`/home/domingo/.dotnet/dotnet build`):
`0 Warning(s), 0 Error(s)`.

**Full suite, 3 consecutive runs** (`scripts/run-tests.sh`, P471):

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1221/1221 | 438/438 | 3704/3708 (4 flakes) |
| 2 | 12/12 | 1221/1221 | 438/438 | 3708/3708 (clean) |
| 3 | 12/12 | 1221/1221 | 438/438 | 3707/3708 (1 flake) |

The MonoGame assembly — which carries the 7 new P525 framing tests — passed
1221/1221 in all three runs. The five Core assembly failures are all
pre-existing, documented live-wire **load-flakes** (`docs/TEST_FLAKES.md`),
none touched by this rendering-only change:

- run 1: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`
  (TEST_FLAKES.md:52), `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`
  (family, :245), `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`
  (:405), `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried`
  (family, :36/:182)
- run 3: `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
  (:247/:331)

## Headless visual proof

Added a `hullpreview` mode to `tools/roster-capture` that reuses its existing
offscreen render path (`Game` + `GraphicsDevice` + `ShipRenderer.Draw` +
`RenderTarget2D`) to render the preview's exact framing (eye z=2.4, FOV 50) for
a set of hulls in BEFORE (old `1/length`) and AFTER (new radius framing).
Assets: `docs/reports/P525_DOCKED_HULL_PREVIEW_assets/`.

| hull | class (len, from PCore001) | renderR (measured) | BEFORE drawnR | AFTER drawnR |
|---|---|---|---|---|
| `ic-fighter` | Fighter (13.00) | 100.50 | **7.73 → camera INSIDE** | 1.00 (framed) |
| `ic-carrier` | Attack Carrier (300.00) | 95.63 | 0.32 → too small | 1.00 (framed) |
| `phoenix-fighter` (P515) | Fighter (20.85) | 103.07 | **4.94 → camera INSIDE** | 1.00 (framed) |

Verified visually: the fighter/phoenix BEFORE frames show the camera inside the
mesh (frame filled with interior polygons); the carrier BEFORE shows a tinier
than intended ship; all three AFTER frames show the whole ship cleanly framed
from outside with margins. Pairs:
`p525_ic-fighter_before/after.jpg`, `p525_ic-carrier_before/after.jpg`,
`p525_phoenix-fighter_before/after.jpg`.

## Deliverables

- `Rendering/DockedHullPreviewFraming.cs` (new, pure framing helper, cited to
  `load.cpp:860-934`)
- `Game1.Rendering.cs` — `DrawDockedHullPreview` rewritten to use the helper +
  clear + depth re-enable
- `tests/.../DockedHullPreviewFramingTests.cs` (new, 7 tests)
- `tools/roster-capture/Program.cs` — added `hullpreview` mode
- `docs/reports/P525_DOCKED_HULL_PREVIEW.md` (this report)
- `docs/reports/P525_DOCKED_HULL_PREVIEW_assets/` (6 before/after JPEGs)

Follow-up (separate decision, not changed here): align the preview motion with
native's loadout spin (1 rad/s about Y, fixed π/8 X tilt, `load.cpp:877-887`)
instead of the in-flight `cmExternalOrbit` rates.
