# P979 — Visual-enhancement flags (`visuals.*`) + `visuals.radialVisibility`

Lane: Hermes (DeepSeek V4 flash), worktree `allegiance-hermes-p979`, branch `hermes/p979`, base
`66d72e6f` (Claude's master at dispatch). Changes are left in the working tree — no commit, no push;
no rig/mini/Mac or other host was contacted. Scratch (build logs, proof frames) is in the
git-excluded `.scratch/p979/`. `local.env` present (artwork + native source paths), so the artwork
environment resolved locally and the suite's artwork-dependent tests ran (skips are the suite's
normal ones, see the gate table).

## Result

There is now ONE small, reusable flag system for visual enhancements (`visuals.*` in the player
profile, a `--visuals` launch override, an Options → **Visuals** page, a startup log, and a single
definition table), and its first flag, `visuals.radialVisibility` (default **OFF**): an object is
drawn by its true distance from the camera instead of by native's planar far clip, so turning the
ship can no longer reveal or hide it. The native planar path is untouched when the flag is off and
is pinned by tests; the range parity Claude landed on 2026-11-08 (native 10 km) is not re-done.

## 1. The planar-clip defect: proof, native citation, classification

Every flight camera's far clip is a **plane in view space**. MonoGame's
`Matrix.CreatePerspectiveFieldOfView(fov, aspect, near, far)` maps view-space depth `z`, and native's
`Camera` does the same thing:

- `engine/camera.cpp:89-96` — the perspective matrix: `z_screen = (a·z + b)/−z` with
  `a = zfar/(znear−zfar)`, `b = −a·znear`. Only view-space `z` participates.
- `engine/camera.cpp:135-139` — `Camera::SetZClip(znear, zfar)` stores `m_znear = −znear`,
  `m_zfar = −zfar`; nothing else.
- `WinTrek/WinTrek.cpp:323` — `SetZClip(5.0f, 10000.0f)` for the world camera;
  `:325` posters camera `SetZClip(10, 10000)`; `:328` target camera `SetZClip(1, 20)`. The port's
  flight cameras use vertical FOV 50° (native `s_fDefaultFOV`, `WinTrek.h:26`) and, since
  2026-11-08, `ExternalCameras.FarPlane = 10000` (native's range).

For an object at distance `d` off-axis by angle θ, view-space depth is `d·cos θ`. With a 50° vertical
FOV at 16:9 the horizontal half-angle is **39.66°** (cos 0.7699) and the true corner is **43.57°**
(cos 0.7246). The visible distance therefore varied with the angle:

| far plane | dead ahead | horizontal edge (39.66°) | corner (43.57°) |
|---|---|---|---|
| old port 6000 (pre-2026-11-08) | 6000 m | 7794 m | 8281 m |
| native 10000 (current master) | 10000 m | 12989 m | 13801 m |

So an object between 6.0 and 7.8 km popped in the moment it reached the screen edge (and between
6.0 and 8.3 km only at the corners). Re-checked numerically and in the captures below: at **8000 m**
the horizontal edge gives view depth `8000·cos 39.66° = 6159 m` (still clipped by 6000) while the
corner gives `5797 m` (inside) — the reported aleph "appearing when I turn" is exactly this corner
case. At **7000 m** the edge is `5389 m` (inside 6000) while dead ahead is `7000 m` (outside) — the
clean reproduction used in the evidence. With native's 10000 m plane the same pop-in survives at
10–13 km (10500 m at 35° = `8601 m` view depth, inside 10000 — the GPU drew it; dead ahead it did
not).

**Classification.**

- The **range** (6000 vs 10000) was a port bug and is already fixed on master (Claude, 2026-11-08);
  `tests/Allegiance.ClientCore.MonoGame.Tests/FlightFarClipParityTests.cs` pins it. Not re-done here.
- The **shape** (a planar clip) is an era constraint — native's projection is the same shape and its
  depth range assumed 1999 hardware. It is therefore a *visual* departure, not a parity fix, and
  goes behind a flag that is **off by default** (Domingo, 2026-11-08). No server rule, fog/sensor
  visibility or wire message changes: the client only stops hiding objects it already has.

Depth precision was checked rather than assumed (the A/B capture in §4): near=1 (cockpit/missile), Depth24, 9.5 km →
**5.38 m** of quantization; 10 km → **5.96 m**. An A/B render with native's own near=5 (1.08 m
quantization) changed **3 of 669 lit pixels** (max channel delta 20) on the station and **0 of 664**
on the asteroid — i.e. the speckle at that distance is texture minification, not z-fighting. **No
reversed-Z was needed** and none was added.

## 2. The flag framework (one table, no per-flag plumbing)

`src/Allegiance.ClientCore.Game/Rendering/VisualFlags.cs`

- `VisualFlag` (record) + `VisualFlags.Definitions` — the table: key, label, one-line description,
  toggle/range kind, defaults, bounds, step, menu hotkey. A future visual task (skybox,
  tonemap/palette, lighting) appends a row and reads its value; parsing, override, page, log and
  persistence pick it up automatically.
- `VisualOptions` — the live state: `IsOn(key)` / `Value(key)` (missing/unknown → table default,
  toggles default **OFF**), `SetToggle`/`SetValue` (clamped, non-finite → default), `Clone()`,
  `ApplySpec()` (the `key=value,...` grammar: `on/off/true/false/1/0`, bare key = on), `Parse`,
  `Resolve(profile, launchSpec)` (launch wins, profile is copied not aliased), `Describe()` (startup
  log), `DescribeValue()` (menu labels).
- `VisualOptionsJsonConverter` — the profile group is one flat object
  `"visuals": { "radialVisibility": true, "viewRange": 12000 }`; unknown keys ignored on read, every
  table key written on save.
- Profile: `IntroPreferences.Visuals` (`src/Allegiance.ClientCore.Game/IntroPreferences.cs:113-114`,
  written as the lowercase `"visuals"` group with `[JsonPropertyName("visuals")]`, read
  case-insensitively), a legacy profile without the group loads the defaults; `Normalize` re-clamps
  the numeric flag (`IntroPreferences.cs:160-169`).
- Launch override: `--visuals key=on,...` / `--visuals=...` in
  `src/Allegiance.ClientCore.MonoGame/Program.cs:242-252` → `LaunchOptions.VisualsOverride`
  (`src/Allegiance.ClientCore.Game/Game1.Session.cs:155-161`) → resolved once at startup in
  `src/Allegiance.ClientCore.Game/Game1.cs:1745-1753` (field `_visuals`, `Game1.cs:744-750`), with
  one console line: `[client] visuals: radialVisibility=off viewRange=10000`.
- Options page: `OptionsMenuModel` gained `Page.Visuals`, `OpenVisualsMenu` and the generic
  `ToggleVisualFlag` / `RaiseVisualRange` / `LowerVisualRange` commands; the rows are generated from
  `VisualFlags.Definitions` (`OptionsMenuModel.cs:221-279`), so the page cannot drift from the table.
  The root menu keeps native's four groups in order and adds `Visuals >` ('V') before Controls
  (`:232-236`) — a port extension like P893's 3D-sound switch. Executed in
  `Game1.BenchmarkSharing.cs:177-187, 254-278`: the live `_visuals` object is mutated in place (next
  frame uses it) and the *profile* copy is saved, so an unrelated preferences save never persists a
  launch override.

## 3. `visuals.radialVisibility` — the rule

`src/Allegiance.ClientCore.Game/Rendering/RadialVisibility.cs`

- `RadialDrawn(distance, radius, range)` — true when the object's **nearest surface**
  (`distance − radius`) is inside `viewRange` (default 10000 m, native's dead-ahead distance; settable
  `visuals.viewRange`, clamped 1000–50000). Counting the radius keeps a giant asteroid whose centre is
  just past the limit from vanishing while its bulk is still in range.
- `PlanarDrawn(viewDepth, near, far)` — native's answer, kept and pinned for the OFF path.
- `IsDrawn(radial, …)` — the flag's single decision.
- `RadialFarPlane(range)` — `max(10000, range × 1.1)`: the projection's far plane goes past the range
  so the plane can never clip an object the sphere rule keeps (an object at the limit whose bulk
  extends outward stays whole); 11000 m at the default range.
- `WithFarPlane(projection, far)` — the same matrix surgery P385's benchmark distance uses
  (`M33`, `M43`), near plane and FOV preserved.
- `DepthQuantization(viewDepth, near, far, depthBits)` — the measurable z-fighting yardstick used in
  §1/§4.

Wiring (all in `src/Allegiance.ClientCore.Game/Game1.Rendering.cs`):

- `ResolveRadialView()` (`:54-66`), called once per frame before the gather (`:449`): active only for
  the flight cameras native's `SetZClip` governs — cockpit, chase, every external mode, missile,
  turret, drone — and not for the command map / free-cam / spectator orbit (port extensions with
  their own 80 km far) nor the benchmark/capture cameras (their framing stays comparable).
- Projection: `RadialVisibility.WithFarPlane(proj, _flightDepthFar)` after the camera and benchmark
  resolution (`:492-494`).
- Per-object gate `OutsideRadialView(position, eye, radius)` (`:75-77`): ships (`:738`, with the
  ship's render radius), asteroids (`:880`), alephs (`:955`), stations (`:997`), builds (`:1060`),
  missiles (`:1312`), probes (`:1375`), mines (`:1422`), treasure (`:1499`), explosions (`:1706`),
  and the linear-depth gather for ships/asteroids/stations (`:2520, 2570, 2598`) so the
  soft-particle depth silhouette matches what the colour pass draws. Projectiles and chaff are not
  gated: they already carry their own much tighter distance filters (2 km from the own ship) and can
  never reach the range. Particles are emitted only from objects the gate keeps. The backdrop/skybox
  is untouched (its own placement; `Rendering/NativeSkyPlacement.cs:60-73`).
- Depth consistency: the depth pass uses the same resolved range —
  `_softContext` (`:646`), `DrawSceneDepth(…, near, _flightDepthFar)` (`:661`, signature `:2436`),
  flare-occlusion normalization (`:2405`); `LinearDepth.SoftnessFor(near, far)` keeps the
  soft-particle fade at the same 24 world units.

## 4. Evidence

All frames are 960×540 PNGs from `tools/p979-visual-proof` (`scene` mode): the REAL native
`WarpRenderer`/`NativeStationRenderer`, the REAL cockpit camera matrices (`CockpitCamera(near=1,
far=… )` + `RadialVisibility.WithFarPlane`), the proof applying the same gate the client applies
(a HIDDEN object is not submitted). The lit-pixel count in the logs is the corroboration; I opened
every cited image and the visible content matches the numbers.

| frame | what is visible | lit px |
|---|---|---|
| `aleph_8km_planar6000_hidden.png` | empty — the old 6000 m plane hides an aleph 8 km dead ahead | 0 |
| `aleph_8km_native10000_visible.png` | funnel + star core centred (479,269) — native's 10000 m range | 288 |
| `aleph_8km_radial_on_visible.png` | same bbox as the native frame — the flag changes nothing at the centre | 288 |
| `aleph_7km_planar6000_center_hidden.png` | empty — 7000 m > 6000 m dead ahead | 0 |
| `aleph_7km_planar6000_edge_visible.png` | funnel at x≈73 (35° off axis, screen left) — **the pop-in** | 833 |
| `aleph_7km_radial_on_center_visible.png` | funnel centred (479,269) — flag ON keeps it while you look at it | 364 |
| `aleph_7km_radial_on_edge_visible.png` | same edge frame as the planar one — same answer at both angles | 833 |
| `aleph_10_5km_native10000_edge_visible.png` | funnel at the left edge 10.5 km away — native's plane still draws it off axis | 347 |
| `aleph_10_5km_radial_on_edge_hidden.png` | empty — the sphere rule hides it past the 10 km range | 0 |
| `station_9_5km_near1_depth24.png` | station silhouette, clean depth at 9.5 km with near=1 | 669 |
| `station_9_5km_near5_depth24.png` | same frame with native's near=5 (5× finer depth) — 3 px differ | 669 |
| `asteroid_9_5km_near1_depth24.png` | asteroid silhouette, no z-fighting (0 px differ vs near=5) | 664 |

UI evidence (real client, production intro → Options → Visuals page, `ui` mode, 800×480 — recaptured by Claude's
review: the original 960×540 `ui` frames had an uncleared white band and a clipped title, an offscreen-capture
artefact of the intro canvas, not of P979):

- `p979_visuals_classic_off.png` — Classic, defaults: "Radial visibility: off - true distance, no
  far-plane pop-in (R)", "View range: 10000 m - metres the radial rule draws to", raise/lower rows,
  hint "UP/DOWN select ENTER toggle ESC back".
- `p979_visuals_modern_off.png` — byte-identical to Classic (md5 `8bb80138…`): the options screen is
  a shared model and `HudStyle` only selects the in-flight HUD renderer, so both styles show the
  exact same page (the P972 rule: put behaviour in the shared model; the test below asserts it for
  both styles).
- `p979_visuals_modern_on.png` — started with `--visuals radialVisibility=on,viewRange=12000`: the
  page reads "Radial visibility: on", "View range: 12000 m", "Raise … to 13000 m", "Lower … to
  11000 m" — the override is live and the labels are effective-state.
  At 800×480 every description fits (the one-line text was trimmed to do so).

## 5. Cost

- CPU gate: one squared-distance compare per object per frame. Measured in the proof process at
  **1.68–1.75 µs/frame for 200 objects** (200 positions × 600 frames, `RadialVisibility.RadialDrawn`
  with a `Vector3.Distance` each). At 60 Hz that is ~0.01% of a 16.7 ms frame.
- Draw calls: the gate never adds pass work — it removes objects outside the range and keeps objects
  in range that the plane used to clip. In-band (6–10 km off axis) the sphere rule draws *more*
  geometry, but that geometry was already resident and only in the angle band where the plane was
  wrong; the per-observer draw-call count changes only within that band. The projection far grows
  10000 → 11000 (same frustum shape, negligible fill change) and the depth pass range follows.
- Expected on the UM690 iGPU (Radeon 680M): no measurable cost. The extra submitted geometry is at
  6–10 km, i.e. LOD/sprites at a handful of pixels each (`ShipLod` far tier already takes over above
  ~1.5°), and the gate is ~2 µs of scalar CPU. A 200v200 frame-time A/B is a rig measurement per
  the lane rules (200v200 = Claude runs it) and was not attempted here.

## 6. Tests

New/affected test files:

- `tests/Allegiance.ClientCore.MonoGame.Tests/RadialVisibilityTests.cs` — the pure rule: 7/9.5/10.5 km
  dead ahead drawn/drawn/hidden; centre vs edge identical with the flag ON; **the native planar
  answer pinned with the flag OFF** (10.5 km centre hidden, edge drawn — the pop-in); the radial
  projection's far never clips an object the rule keeps; `WithFarPlane` keeps near/FOV; the far-plane
  formula; measured depth quantization incl. the native near=5 comparison and no reversed-Z.
- `tests/Allegiance.ClientCore.MonoGame.Tests/VisualFlagsTests.cs` — table well-formedness and
  default-OFF; launch-grammar (on/off/true/1/bare, spaces, case); unknown/malformed keys warn and
  leave the other keys applied; clamping; launch-wins-and-does-not-alias; missing group / unknown
  JSON keys; `player.json` round trip of the `visuals` group; legacy profile → defaults.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P979VisualsOptionsTests.cs` — root keeps native's four
  groups and adds `Visuals >`; the page lists every table flag with its one-line description and
  default values; labels track live values; row→key mapping and the highest/lowest stops; the
  generic toggle command and hotkeys; **both `HudStyle` values render the same shared page**.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P806OptionsMenuTests.cs` — updated for the fifth root
  row (native's four unchanged; the wrapper row index follows).
- `tests/Allegiance.ClientCore.MonoGame.Tests/FlightFarClipParityTests.cs` — unchanged and still
  green (the OFF path pins native 10000).

Counts: the five affected files are **52 passed / 0 failed / 0 skipped in each of three
isolated runs** (visual flags, radial rule, options page/Visuals, P806 menu, far-clip parity).
Both full Fast sweeps and the showcase are in §7.

## 7. Gate

| gate | result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 124 tool projects built** (including the new `tools/p979-visual-proof`), exit 0 |
| affected tests 3× alone | **52 / 0 / 0** in each of three runs |
| Fast sweep 1 (P979 tree, 13,664 cases, 565.7 s) | **13,660 passed / 1 failed / 3 not executed** — the failure is a Core NPC timing case (`MinerDecisionCycleLiveTests`) that passes **3/3 alone**; two 10 s timing exceedances, both passing |
| Fast sweep 2 (final tree, 13,664 cases, 551.6 s) | **13,658 passed / 3 failed / 3 not executed** — all three are already-ledgered pre-existing families with no P979 source on their path: `MissileLockLiveTests` (**1 pass / 2 fail isolated = the recorded clean-base rate**), `LiveSideSelectTests` (recorded clean-base 2/5), `LiveShieldRegenBroadcastTests` (**3/3 pass** isolated) |
| showcase never-parks 5× | **4 pass / 1 fail**, walls 237/196/162/169/485 s at load 2.6–3.8 (the fail is the full-480 s observation window ending on the arrival assert — the same mode P967 records as 4/5); a 6th detailed run passed in 380 s. Base rate: P966 5/0/0, P967 4/5 → at the base rate. No bots/flight/sim source changed |
| non-task failures triaged | all re-run alone: MinerDecisionCycle 3/3, P777 3/3, ShieldRegen 3/3, P750 passed; MissileLock and LiveSideSelect reproduce their recorded clean-base failure rates |
| `tools/check-glossary-links.py` | **OK** (682 distinct paths) |
| `docs/TEST_FLAKES.md` | P979 entry written with both sweeps and every isolated re-run |
| `local.env` | present (artwork + native source); the Fast sweeps ran with artwork, skips are the suite's **3 NotExecuted** markers in the MonoGame project (0 "Skipped") |
| media budget | `docs/reports/P979_assets/` = **~410 KB** (15 PNGs), under the 2 MB limit; every image opened and described before citing |

## 8. Unfinished / notes

- Nothing from the brief is outstanding. The 200v200 frame-time A/B is explicitly a rig measurement
  under the lane rules; the analysis and the in-process gate measurement are in §5.
- `visuals.viewRange` values above 10000 widen the depth pass range as well (the soft-particle
  softness keeps its 24 world units) — deliberate, and covered by the depth test.
- Command map, free-cam and spectator orbit keep their 80 km projection and are not gated (they are
  port extensions, not the native world camera); the flight cameras the brief names are all covered.

## Native intent

Native's world camera is `SetZClip(5.0f, 10000.0f)` (`WinTrek/WinTrek.cpp:323`) with a planar
perspective clip (`engine/camera.cpp:89-96, 135-139`): it drew objects out to 10 km dead ahead and to
~13 km at the 16:9 screen edge (~13.8 km at the corner), which is why turning could reveal an object
that was hidden dead ahead. Native's intent was a 10 km visibility bubble approximated with the era's
planar clip and low-precision depth buffer; the port now matches the range exactly (10000 m) and
offers the *shape* as an opt-in enhancement so native's look remains the default.

## Modern approach

`visuals.radialVisibility` replaces the planar far clip with the rule native was approximating: an
object is drawn iff its true distance from the camera eye is within `viewRange` (default 10 km,
native's number), with the far plane pushed past the range so nothing in range is sliced. The result
is angle-independent visibility — no pop-in when the ship turns, and no object drawn past the bubble
just because it sits near the screen edge. It is a client-only, default-off visual flag (the flag
table also carries the range), applies to every flight camera and every object kind the client
draws, and leaves the backdrop infinite; the server, fog/sensor rules and the wire are untouched.
