# P449a — Native ambient space dust (DebrisGeo) for a sense of speed

Stage: P449a. Landed on `master` (not pushed). Top commit: see `git log -1` after
landing. A gameplay-parity fix: the MonoGame client gave almost no visual cue of the
player's own speed or drift in open space, where native's main cue is an ambient dust
field riding the camera (`DebrisGeo`).

## 1. Problem

The port had only damage-debris particles (`Game1.cs` P297 `DamageDebris*`) and
nothing tied to the camera's own motion. In empty space a stationary starfield plus a
handful of ships gives no read on whether the pilot is creeping or screaming along.
Native solves this with `DebrisGeo` (`src/effect/debrisgeo.cpp:17-205`): a
camera-relative point field that spawns bluish-white grains ahead of the camera as it
moves, giving an unmistakable parallax "sense of speed".

## 2. What was built

Three new source files plus wiring and a setting:

### 2.1 Pure evaluate-step — `src/Allegiance.ClientCore.MonoGame/Rendering/DebrisGeo.cs`
A MonoGame-free, unit-testable port of native `DebrisGeo::Evaluate` (the port's
established pattern: pure math class separate from drawing, like
`DamageDebrisParticles` / `WarpTransitParticles`). `Step(position, forward, right, up,
focus, time)` reproduces native evaluate exactly:
- measures camera travel `length`;
- computes the reciprocal pseudo-speed `m_speed = Δt/length` and the adjusted
  `distancePerParticle = density − (0.3 − 0.004·m_speed)` when `m_speed < 75`,
  else `density` (debrisgeo.cpp:82-96);
- clears the field when `length > 100` (a jump/warp/teleport/sector change) or the
  effective spacing is 0 (debrisgeo.cpp:98-106);
- culls faded (`brightness ≤ 0`, i.e. aged ≥ 2 s) and beyond-500 m grains
  (debrisgeo.cpp:112-126);
- accumulates travel and spawns one grain per `distancePerParticle`
  (debrisgeo.cpp:128-157) at `pos = position + dist·forward + c·right + s·up` with
  `dist = maxDistance·√u₁`, `radius = rfocus·dist·√u₂`, `rfocus = 1/focus`, in a
  forward cone whose widest grains sit at roughly the view edge.
- exposes `Brightness(age) = 1 − 0.25·t²` and `ShouldCull(dist², age)` as pure
  statics (used by both the step and the render gather), plus `Clear()`.

It is driven purely by the **active** camera's eye + basis each frame, so it follows
whatever pose the camera uses — including the P447b predicted render pose — in every
flight camera mode (chase, cockpit, external-turret, external-missile, capture
overview).

### 2.2 Renderer — `src/Allegiance.ClientCore.MonoGame/Rendering/AmbientDustRenderer.cs`
A camera-facing soft-quad billboard renderer (native `DebrisGeo::Render`,
debrisgeo.cpp:163-190): each live grain is drawn at `scale = 0.1/focus` tinted by
`brightness·colour`, skipped when within 20 m of the camera. Because the decal scale
carries a `1/focus` and the perspective divide also carries a `focus`, a grain's
on-screen angular size is **FOV-independent** — the reason native can bake `focus`
into both the cone and the decal size.

### 2.3 Wiring — `Game1.*`
- **Pose/focus**: in `Game1.Rendering.cs` Draw (Flying), after the starfields, the
  active camera's basis is extracted from the view matrix (`forward = −row2`,
  `right = row0`, `up = row1`) and `focus = proj.M22 = cot(vFOV/2)`; the field is
  stepped and drawn behind the ships, over the starfield, additively.
- **Setting**: native's user `Debris` preference is exposed as the client launch
  setting `--debris <float>` (default 1.0, 0 = off), mirroring the P378 `liveSettings`
  philosophy (a client-only toggle, never sent on the wire) — same pattern as
  P385 `--debugMode` / P448b `--noDeadReckon` / P447b `--noPrediction`.

### 2.4 Jump clearing
Native's own rule clears the field on `> 100 m` camera travel in one step
(debrisgeo.cpp:98) — a warp, teleport or sector change always exceeds that, so the
field never smears across a transit. `Clear()` is also exposed for an explicit
warp/sector-change hook and tests.

## 3. Native constants extracted (file:line)

| Constant | Value | Source |
|---|---|---|
| `maxDistance` | 100 | debrisgeo.cpp:14 |
| jump/clear distance | `length > 100` | debrisgeo.cpp:98 |
| cull squared distance | `> 250000` (500 m) | debrisgeo.cpp:121 |
| density speed threshold | `m_speed < 75` | debrisgeo.cpp:92 |
| density adjustment | `− (0.3 − 0.004·m_speed)` | debrisgeo.cpp:93 |
| fade curve | `1 − 0.25·t²` | debrisgeo.cpp:118,174 |
| near-camera render skip | `distance² ≤ 400` (within 20 m) | debrisgeo.cpp:177 |
| decal scale | `0.1/focus` | debrisgeo.cpp:166 |
| lateral cone radius | `rfocus·distance·√u₂`, `rfocus = 1/focus` | debrisgeo.cpp:139 |
| colour | r∈[0.5,0.75], g,b∈[0.75,1.0] | debrisgeo.cpp:149-154 |

> **Faithful-port note on the density direction.** The comment at debrisgeo.cpp:90
> says "more stars at lower speed", but the code as written (`dpp = density −
> (0.3 − 0.004·m_speed)`, where `m_speed = Δt/length` is the *reciprocal* speed)
> makes `distancePerParticle` smaller — denser — when `m_speed` is small, i.e.
> when moving fast. This port reproduces the **code**, not the comment.

## 4. Texture

Native loads `debris1bmp` at construction (debrisgeo.cpp:51). The port loads the real
`debris1bmp.mdl` from the artwork tree through the established `.mdl` bitmap pipeline
(`MdlTextureDecoder`, same as `GaugeTextures`/`LockTextures`) in
`AmbientDustRenderer.LoadDebrisTexture`. The asset exists
(`<Artwork>/debris1bmp.mdl`); if it is ever missing or fails to decode, the renderer
falls back to a procedural soft radial dot and logs it.

## 5. Setting

`LiveSettings`-style client launch setting `--debris <float>` →
`Game1.LaunchOptions.DebrisDensity` (default **1.0**; **0 = off**). Native's preference
is `m_userDistancePerParticle` (WinTrek.cpp, `Debris`, default 1.0).

## 6. Verification

### 6.1 Pure tests — `tests/Allegiance.ClientCore.MonoGame.Tests/DebrisGeoTests.cs` (11)
All pass (`dotnet test --filter DebrisGeo`, 11/11):
- no grains spawn while the camera is stationary;
- travelling a distance spawns ≈ distance/`distancePerParticle` grains (the
  accumulator law, native strict-`>` boundary accounted);
- `Brightness` at t = 0/1/2 s follows `1 − 0.25·t²` (and a distinguishing quadratic
  point at t = 1.5 s);
- faded (age ≥ 2 s) grains are culled on the next step; the `> 500 m` / faded
  predicate culls;
- a `> 100 m` jump clears the field; `Clear()` drops everything and resets;
- spawn positions lie in the forward cone (`fwdC ∈ [0, maxDistance]`,
  `lateral ≤ rfocus·fwdC`);
- colours are bluish-white (r 0.5–0.75, g/b 0.75–1.0);
- density 0 disables the field; the low-speed density adjustment is exact.

### 6.2 Whole-solution build
`/home/domingo/.dotnet/dotnet build` (Release): **0 warnings, 0 errors**.

### 6.3 Full test suite — 3 runs
`/home/domingo/.dotnet/dotnet test` over all four test projects
(`Allegiance.Core.Tests`, `Allegiance.Lobby.Tests`, `Allegiance.ClientCore.Tests`,
`Allegiance.ClientCore.MonoGame.Tests`), 3 runs. The MonoGame test project — the one
this stage modifies — passed **1023/1023 in runs 1 and 3 and 1022/1023 in run 2**; my
11 new `DebrisGeo` tests passed in **all 3 runs**. The only failures in any run were
the pre-existing live-wire timing flakes documented in the P471 flake ledger
(`docs/prompts/p471-test-flake-ledger-and-systemic-fix-prompt.md` — "green alone,
fails under full-suite load"; no xunit parallelism config, 209 test files use
`Thread.Sleep`/`Task.Delay`, dozens of loopback live servers tick in parallel). These
are in projects this stage does not modify (Core.Tests/ClientCore.Tests exercise the
server, wire and ClientWorld; `ClientCore.Tests` does not even reference the MonoGame
project, so its binaries are bit-identical with or without this change).

**Each flake seen (named), across the 3 runs + isolation re-checks:**

| Test | Project | Seen | Isolation re-check |
|---|---|---|---|
| `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (P471 #1, 10 mentions) | ClientCore | runs 1-3 | fails alone (live race: `Assert.Single` found 2 entities) |
| `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` (P471, 4+2) | ClientCore | runs 1-3 | fails alone (bounded live pump) |
| `FullSessionKillIntegrationTests.FullSession_ThreePeers…` (P471, 3) | ClientCore | runs 1-2 | **passes alone** (confirmed load flake) |
| `GameOverPlayersWireTests.*` / `ConquestTerritoryLiveTests.*` (P471, 4, "green alone, fails under load") | Core | runs 1-3, varying subset | — |
| `CargoBayReloadTests.*` (bounded `PumpUntil` live tests) | Core | runs 1 & 3 | — |
| `ChatTargetComposeLiveTests.ComposeDefault_Team_…` (live chat round-trip) | MonoGame | run 2 only | **passes alone** (confirmed load flake; unrelated to P449a) |

None of these can be caused by P449a: the stage adds a client-only particle field +
renderer in the MonoGame project and a `--debris` launch flag; it touches no server,
wire, `ClientWorld` or `ClientCore`/`Core` logic, and the MonoGame tests that this
stage's code lives beside passed 1023/1023 in two of the three runs.

## 7. Capture evidence

Live-proof harness `.verify/p449a/harness/` (runs `p449adust <outdir>`) boots a real
in-process server + real `ConnectionSession` peer, then drives the real `DebrisGeo`
step + the real `AmbientDustRenderer` offscreen at three camera speeds, saving PNGs
and logging a per-frame dust metric (lit-pixel count + average hue). Because the test
mission stays `NotStarted` (the server does not translate a parked ship), the harness
prescribes the camera trajectory analytically (two speeds, +X), while the server,
wire session, field and renderer are all real — a harness simplification, noted
honestly. Frames: `.verify/p449a/frames/{stationary,slow,fast}.png`.

Results (`p449a_run.log`):

| Frame | Camera | dust lit-pixels | average hue (R,G,B) |
|---|---|---|---|
| stationary | parked (0 u/s) | **0** | — |
| slow | 40 u/s | 2344 | (75, 109, 106) |
| fast | 220 u/s | 3336 | (85, 119, 119) |

What's visible (inspected): the **stationary** frame is pure black — no grains, as
expected (the field spawns only on camera travel). The **slow** and **fast** frames
show a field of ~40-50 / ~60-70 small soft **bluish-white / cyan** luminous specks
scattered evenly across the view (red is the low RGB channel — native's r∈[0.5,0.75]
vs g,b∈[0.75,1.0]). The field is present only while the camera moves, and it is the
parallax streaming of these fixed grains that conveys the sense of speed (native's
density is roughly speed-independent because grains-per-metre × a fixed ~100 m
forward cone stays constant; the cue is motion, not count). This matches native
behavior and the brief's requirement (a captured frame and two frames at different
speeds showing the dust).

