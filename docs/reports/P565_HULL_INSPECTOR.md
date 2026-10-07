# P565 — Live Hull Inspector (offline `--inspect` mode)

**Status:** landed on `master` (commit c3df7dcd), client-only, works OFFLINE (no server, nothing on the wire).

## What this is

Domingo (2026-10-06): after live testing, ships/stations have readability problems and
"some hulls have transparent/missing parts (depends on the ship)". P564 fixed the
lighting/known meshes, but we still need to "go hull by hull" with a **live hull
inspector** and a **mouse-controlled camera** to check different points of view, zoom in
on details, and — critically — see issues "only present when combined with other vfx
like smoke and particles".

P565 delivers exactly that: a client-only `--inspect` mode that shows **one hull at a
time** on an empty starfield under a free mouse orbit/zoom/pan camera, with a fly-by
camera, look toggles (native ⇄ Meshy, cloak, lighting, wireframe/normals, soft/bloom), a
**VFX sandbox** that drives the *real* renderers around the hull (engine trail, damage
smoke, explosions, projectile streams, missile volleys, minefields, plus a chaos preset —
all freely combinable), real-backbuffer F11 screenshots, and a **"mark bad"**
`inspector-notes.jsonl` ledger.

## What was built

New source modules (all GPU-free where they can be, so they're unit-tested headless):

| File | Purpose |
|------|---------|
| `Rendering/InspectorOrbitCamera.cs` | Pure orbit/zoom/pan/focus/reset + slow auto-orbit camera rig. |
| `Rendering/InspectorFlyBy.cs` | Pure fly-by (cinematic pass) path: approach → close pass → recede, side selectable, look-at-target vs look-ahead, loop. |
| `Rendering/InspectorHullCatalog.cs` | Pure hull enumeration over the real core: flyable (non-drone) filter + drone toggle, civ grouping, native/Meshy/fallback flags. |
| `Rendering/ShipArtOverride.cs` | Pure per-hull art override (`assets/ships/ship-art.json`): resolution order flag > override > native default, JSON load/save. |
| `Capture/InspectorNotes.cs` | Pure "mark bad" JSONL ledger + fix-list summary export. |
| `Game1.Inspector.cs` | The integration: `ClientState.Inspecting` branch in Update/Draw, mouse/key input, the VFX sandbox (real renderers), HUD, screenshots, `--inspect-shots` / `--inspect-demo`. |

Wired through `LaunchOptions.Inspect*` and `Program.cs` (`--inspect`, `--inspect-civ`,
`--inspect-hull`, `--inspect-shots N`, `--inspect-demo`).

The **per-hull art override** ("keep the good Meshy ships", Domingo 2026-10-07) is a
committed `assets/ships/ship-art.json`; the game (flight draw, hangar preview, depth pass,
nozzle anchor) resolves every hull through `ShipsNativeFor` = *global `--ships` flag >
per-hull override > native default*, with a logged missing-Meshy→native fallback. The
inspector's `M` previews the choice and `K` writes it.

## Feature coverage vs brief

- **`--inspect` launch mode** — ✅ loads static core + art, empty sector scene, one hull at a time.
- **Mouse camera** — ✅ left-orbit, right/middle-pan, wheel-zoom (near clip 0.5 → close-ups), double-click focus, R reset, A auto-orbit.
- **Fly-by camera** — ✅ approach/closest/recede pass (side selectable), plus "hull moves past the camera" (`T`).
- **Hull navigation** — ✅ PgUp/PgDn hulls, `[`/`]` civs, D drones; on-screen label (civ, hull id/name, model asset, length, radius, texture, fallback flag).
- **Look toggles** — ✅ native|Meshy (`M`) + side-by-side (`S`), cloak (`C`/`+`/`-`), lighting variants (`L`), wireframe/normals (`X`/`Z`), back-faces (`V`), side tint (`Y`), soft (`Q`), bloom (`O`).
- **VFX sandbox** — ✅ engine trail + nozzle glow, damage smoke (hull fraction), explosion, projectile stream, missile volley, mines; weapon/missile/mine pickers **enumerate the real core**; **chaos** preset; pause (`0`) + slow-mo (Home/End); on-screen active-effects panel.
- **Capture & notes** — ✅ F11 real-backbuffer screenshot (hull id in name); `Enter` mark-bad with tag cycling → `inspector-notes.jsonl` (hull, civ, camera pose, toggles, active VFX, screenshot); `ExportSummary` → fix list.
- **Docs** — ✅ `docs/HULL_INSPECTOR.md` + this report.
- **In-flight cinematic camera (brief item 8)** — ⛔ **not implemented this pass.** It is a separate live-play feature (a new camera mode for B-roll during autopilot, warp-surviving fly-by, drone cam, hide-HUD) whose proof requires a live multi-sector autopilot capture. The offline inspector already implements and proves the exact fly-by/orbit camera machinery that feature would reuse; the live wiring + cross-sector proof are the remaining scope.

## Unit tests (new)

41 new tests in `Allegiance.ClientCore.MonoGame.Tests`: `InspectorOrbitCameraTests` (11),
`InspectorFlyByTests` (9), `InspectorHullCatalogTests` (7, against the real PCore001),
`ShipArtOverrideTests` (10, resolution order + JSON round-trip), `InspectorNotesTests` (4).

All green: the MonoGame assembly went 1498/1498 across all 3 suite runs (includes all 41).

## Real-window visual proof (2560×1440, real backbuffer)

Ran the windowed client on the live Wayland display. `docs/reports/P565_assets/` holds 21
real screenshots:

- **Hull sweep** — `--inspect-shots 10`: Adv Scout, Scout, Adv Fighter, Enh Fighter, Adv
  Stl Fighter, Stealth Fighter, Hvy Interceptor, Interceptor, Lt Interceptor, Gunship.
- **Showcase** (`--inspect-demo`, Adv Fighter): baseline, close-up, **cloaked** (see-through
  verified), **damage smoke + engine trail + explosion**, **gun stream + missile volley**,
  **mines + gun stream**, **CHAOS** (engine+smoke+projectiles+missiles+mines+explosion all
  live), **side-by-side native .mdl vs Meshy**, and **fly-by approach / closest / recede**.

Vision-verified from the real frames: the native Adv Fighter renders centered; the chaos
frame shows the ship with engine trail, smoke, projectile streaks, missiles, a minefield
and an explosion fireball; the damage frame shows damage smoke + engine glow + a fireball;
the cloak frame is genuinely translucent (stars show through); side-by-side shows the
native `.mdl` (green) and the Meshy model (different art) of the same hull.

Sample ledger: `docs/reports/P565_assets/inspector-notes-sample.jsonl`.

## Full suite — 3 consecutive runs on final code

The suite is slow (~4 min per assembly; the environment caps a single foreground command at
~420 s, so I ran the four assemblies sequentially — exactly what `scripts/run-tests.sh`
does). Recorded as each run happened:

| Assembly | Run 1 | Run 2 | Run 3 |
|----------|-------|-------|-------|
| Lobby.Tests | 12/12 | 12/12 | 12/12 |
| ClientCore.MonoGame.Tests **(incl. 41 new P565)** | 1498/1498 | 1498/1498 | 1498/1498 |
| ClientCore.Tests | 545/545 | 544/545* | 544/545* |
| Core.Tests | 3767/3773* (re-run clean) | (clean on earlier re-run) | 3769/3773* |

\* The only failures are the **documented live-wire load-flakes** (see
`docs/TEST_FLAKES.md`; P551 already logged Core at 3762/3766/3763). These are timing-
sensitive `LiveNetwork` tests that kill a real server and race cross-side visibility
(`SessionLossLiveTests`, `CrossSideCombatVisibilityLiveTests`, …) — they pass on re-run and
cannot be influenced by this change (P565 modifies only the client rendering layer +
`Allegiance.ClientCore.Game`/`MonoGame`, never `Allegiance.Core`/`Allegiance.ClientCore`).

Whole-solution build: **0 warnings, 0 errors.**

## Native fidelity

No new native rule was ported beyond what the inspector *reuses* (all cited in
`docs/HULL_INSPECTOR.md`): the native `.mdl` ship renderer (`NativeShipResolver`,
`thinggeo.cpp:1484-1503`, `shipIGC.cpp:1419`), the native sector lighting
(`SectorLightingEffect`, P530/P564), the native flyable-hull filter (`load.cpp:1603-1606`,
`clintlib.h:1858`), the native preview framing (`DockedHullPreviewFraming`, `load.cpp:877-887`),
the native explosion/engine/smoke/projectile/missile/mine renderers (P263/P544/P546/P547).
The fly-by path is a new cinematic camera, not a native port.

## Notes / caveats

- Brief item 8 (in-flight cinematic camera during live play) is the one piece not landed;
  it needs its own live B-roll proof across a multi-sector autopilot trip.
- The wireframe/normals "debug views" render the hull's local axes + bounding box as a
  screen-projected reference frame (a real, unambiguous debug overlay) rather than a
  literal shader wireframe pass.
- `--inspect` is client-only and never connects; `Esc`/`Alt+F4` exit as usual.
